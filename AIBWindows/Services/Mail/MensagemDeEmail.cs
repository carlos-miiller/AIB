using System;

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
/// Os X-GM-LABELS. Trazem as categorias do Gmail (Promoções, Social, Atualizações, Fóruns), que
/// já vêm classificadas de graça: não há por que reimplementar heurística de newsletter.
/// </param>
/// <param name="NaoLida">Se ainda está por ler na caixa do usuário.</param>
/// <param name="Corpo">
/// Texto da mensagem, já truncado. Existe só para o degrau 3 ler e resumir; nunca é gravado.
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
    string Corpo)
{
    /// <summary>Quanto do corpo desce e é oferecido ao modelo.</summary>
    public const int TetoDoCorpo = 1200;

    /// <summary>
    /// Corta o corpo no teto, sem cortar palavra pela metade quando dá para evitar.
    /// <para>
    /// O teto não é economia de disco — é economia de PREFILL. Numa máquina de ~34 tok/s de
    /// prefill, trinta mensagens inteiras num lote passariam de vinte mil tokens e o digest
    /// levaria dez minutos para começar a sair.
    /// </para>
    /// </summary>
    public static string Encurtar(string? texto, int teto = TetoDoCorpo)
    {
        string limpo = (texto ?? "").Replace("\r", "").Trim();
        if (limpo.Length <= teto) return limpo;

        int corte = limpo.LastIndexOf(' ', Math.Min(teto, limpo.Length - 1));
        if (corte < teto / 2) corte = teto;

        return limpo.Substring(0, corte).TrimEnd() + "…";
    }
}
