using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AIB.Services.Mail;

/// <summary>
/// O que a triagem vê de uma mensagem.
/// <para>
/// Chama-se <c>MensagemDeEmail</c> e não <c>MailMessage</c> de propósito: <c>System.Net.Mail</c>
/// já tem uma <c>MailMessage</c>, e este projeto compila com WinForms junto do WPF — homônimo
/// aqui custaria um <c>using</c> de desambiguação em todo arquivo que a tocasse.
/// </para>
/// <para>
/// REGRA 3 DO VIGIA: nada daqui pode entrar no <c>raw.jsonl</c> nem virar capítulo. Este objeto
/// vive em memória durante uma triagem e morre no fim dela; o que sobrevive é o veredito, que
/// não carrega corpo nem remetente.
/// </para>
/// </summary>
/// <param name="Uid">Identificador da mensagem na caixa. Único dentro de um uidValidity.</param>
/// <param name="ThreadId">
/// X-GM-THRID, a conversa a que ela pertence. É o que liga uma resposta ao que o usuário
/// enviou — e a comparação é exata, instantânea e grátis, sem modelo nenhum.
/// </param>
/// <param name="De">Endereço do remetente, minúsculo.</param>
/// <param name="NomeDoRemetente">Nome como veio no envelope, ou o endereço quando não veio.</param>
/// <param name="Assunto">Assunto, como veio.</param>
/// <param name="RecebidaUtc">INTERNALDATE, a hora de chegada no servidor.</param>
/// <param name="Direto">
/// O usuário está no To, e não só no Cc. Cópia é notificação; destinatário é pedido.
/// </param>
/// <param name="Importante">O marcador \Important do Gmail — o degrau 0, calculado no servidor.</param>
/// <param name="Rotulos">
/// Os X-GM-LABELS. Trazem <c>\Important</c>, <c>\Starred</c> e as etiquetas que o usuário
/// criou.
/// <para>
/// O QUE ELES NÃO TRAZEM: as abas de categoria do Gmail (Promoções, Social, Atualizações,
/// Fóruns). Elas existem só na interface e na API do Gmail; sobre IMAP o servidor não as
/// expõe em X-GM-LABELS. Medido em produção — catorze mala-diretas passaram pelo funil sem
/// que um único rótulo de categoria chegasse. Quem substitui esse degrau é
/// <see cref="EnvioEmMassa"/>, que vem de header e não depende do Gmail.
/// </para>
/// </param>
/// <param name="NaoLida">Se ainda está por ler na caixa do usuário.</param>
/// <param name="Corpo">
/// Texto da mensagem, já truncado. Existe só para o degrau 3 ler e resumir; nunca é gravado.
/// </param>
/// <param name="Conta">
/// A caixa em que ela chegou. Com duas contas configuradas, dizer "chegou um urgente" sem
/// dizer ONDE obriga o usuário a procurar nas duas.
/// </param>
/// <param name="EnvioEmMassa">
/// A mensagem foi disparada para uma lista, e não escrita para uma pessoa.
/// <para>
/// Vem de <c>List-Unsubscribe</c> (RFC 2369), <c>List-Id</c> ou <c>Precedence: bulk</c>. É o
/// sinal mais limpo que existe para isto: quem dispara mala-direta é OBRIGADO a oferecer
/// descadastro, e quem escreve para uma pessoa nunca põe esse header. Não depende do domínio
/// nem de lista de palavras — <c>picpay@marketing.picpay.com</c> e
/// <c>hello@students.udemy.com</c> não têm marca nenhuma no local-part, e os dois têm o
/// header.
/// </para>
/// <para>
/// Não basta sozinho: as portas de fuga do funil (conversa vigiada, fonte do regras.md,
/// <c>\Important</c>) passam antes dele, porque uma lista interna da empresa também tem
/// List-Id e ainda assim pode carregar o aviso que importa.
/// </para>
/// </param>
public sealed record MensagemDeEmail(
    uint Uid,
    string ThreadId,
    string De,
    string NomeDoRemetente,
    string Assunto,
    DateTime RecebidaUtc,
    bool Direto,
    bool Importante,
    string[] Rotulos,
    bool NaoLida,
    string Corpo,
    bool EnvioEmMassa = false,
    string Conta = "")
{
    /// <summary>
    /// Quanto do corpo, JÁ LIMPO, é oferecido ao modelo. Menor que antes porque agora são 600
    /// caracteres de frase, e não 1200 de folha de estilo.
    /// </summary>
    public const int TetoDoCorpo = 600;

    /// <summary>
    /// Tira do corpo o que não é texto para ler.
    /// <para>
    /// Medido em produção: um lote de 21 mensagens virou 11.223 tokens de prompt, e o modelo
    /// levou ONZE MINUTOS só no prefill. O que ocupava esse espaço não era conteúdo — era
    /// folha de estilo (<c>#outlook a{padding: 0;} .ReadMsgBody{width: 100%;}…</c>) e parede
    /// de URL de rastreamento de trezentos caracteres cada.
    /// </para>
    /// <para>
    /// Some, nesta ordem: blocos de estilo e script inteiros, comentários, tags, URLs,
    /// entidades HTML e os caracteres invisíveis que os disparadores usam para esticar a
    /// pré-visualização. O que sobra é a frase que alguém escreveu.
    /// </para>
    /// </summary>
    public static string Limpar(string? bruto)
    {
        string t = bruto ?? "";
        if (t.Length == 0) return "";

        // Estilo e script primeiro: dentro deles há chaves e dois-pontos que confundiriam
        // qualquer limpeza feita depois.
        t = Regex.Replace(t, @"<(style|script)[^>]*>.*?</\1>", " ",
                          RegexOptions.Singleline | RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<!--.*?-->", " ", RegexOptions.Singleline);
        // O teto era 400 e vazava: medido em produção, a <a> de um e-mail do Patreon tinha
        // mais de 400 caracteres só no atributo style, não casava, e sobrevivia inteira dentro
        // do prompt. Mil e duzentos cobre a tag mais gorda que se vê em mala-direta, e o teto
        // continua existindo para que um "<" solto no meio de uma frase não coma o parágrafo.
        t = Regex.Replace(t, @"<[^>]{0,1200}>", " ");

        // CSS solto, em DUAS etapas. A tentacao e comer "seletor + bloco" de uma vez, mas o
        // seletor pode ser composto ("#outlook a{...}") e uma regra gulosa o bastante para
        // pega-lo engole a frase que vinha antes — medido: "Asaba #outlook a{...}" virava
        // string vazia.
        //
        // Primeiro o bloco entre chaves, que e inequivoco.
        t = Regex.Replace(t, @"\{[^{}]{0,600}\}", " ");

        // Depois o que sobrou do seletor: so o que TEM cara de seletor, e nunca uma palavra
        // comum. "#outlook" e ".ReadMsgBody" saem; "Asaba" fica.
        t = Regex.Replace(t, @"(?<![\w])[#.][\w-]{2,40}", " ");

        // URLs. Um e-mail de marketing tem dez delas, cada uma com trezentos caracteres de
        // parâmetro de rastreamento e zero informação.
        t = Regex.Replace(t, @"https?://\S+", " ");

        t = Entidades(t);

        // Invisíveis: espaço de largura zero, hífen suave, junções — os disparadores enchem a
        // mensagem com centenas deles para esticar a pré-visualização na caixa de entrada.
        t = Regex.Replace(t, @"[\u00AD\u200B-\u200F\u2060\uFEFF\u034F]+", "");

        // Sobra de pontuação de layout: linhas de asteriscos, traços e barras que sobravam das
        // molduras de tabela.
        t = Regex.Replace(t, @"[*_=~|-]{3,}", " ");

        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static string Entidades(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        sb.Append(texto);

        sb.Replace("&nbsp;", " ").Replace("&zwnj;", "").Replace("&amp;", "&")
          .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
          .Replace("&#39;", "'").Replace("&apos;", "'");

        return Regex.Replace(sb.ToString(), @"&[#\w]{1,8};", " ");
    }

    /// <summary>
    /// Limpa e corta no teto, sem partir palavra quando dá para evitar.
    /// <para>
    /// O teto não é economia de disco — é economia de PREFILL. Nesta máquina o prefill despenca
    /// de ~430 para ~17 tokens por segundo quando o prompt passa de uns poucos milhares de
    /// tokens: o custo não cresce, ele desaba.
    /// </para>
    /// </summary>
    public static string Encurtar(string? texto, int teto = TetoDoCorpo)
    {
        string limpo = Limpar(texto);
        if (limpo.Length <= teto) return limpo;

        int corte = limpo.LastIndexOf(' ', Math.Min(teto, limpo.Length - 1));
        if (corte < teto / 2) corte = teto;

        return limpo.Substring(0, corte).TrimEnd() + "…";
    }
}
