using System;
using System.Collections.Generic;
using System.Linq;

namespace AIB.Services.Mail;

/// <summary>
/// Junta as mensagens de um lote em CONVERSAS, e responde as três perguntas da linha de
/// metadados de <c>tela-chat-v3.html §3.10</c>: quando foi a última, quantas são, e de quem é
/// a vez.
/// <para>
/// Nenhuma das três vem pronta do servidor. O IMAP entrega mensagens soltas; "3 respostas" e
/// "aguardando retorno" são leitura nossa por cima delas. É por isso que isto é código e não
/// um campo a mais no <see cref="MensagemDeEmail"/>.
/// </para>
/// <para>
/// A chave é o <c>X-GM-THRID</c>, que o Gmail já entrega em
/// <see cref="MensagemDeEmail.ThreadId"/>. Quando ele vem vazio — provedor que não é Gmail —
/// cada mensagem vira a própria conversa, e a tela mostra "1 mensagem". Errar para MENOS é
/// seguro: some o "N respostas", não aparece uma conversa que não existe.
/// </para>
/// </summary>
public static class ConversaDeEmail
{
    /// <summary>
    /// Uma conversa: a mensagem mais recente dela e o que só se sabe olhando o conjunto.
    /// </summary>
    /// <param name="Recente">A mensagem mais nova da conversa — a que dá assunto e data.</param>
    /// <param name="Mensagens">Quantas mensagens a conversa tem NESTE lote.</param>
    /// <param name="EsperandoVoce">
    /// A última palavra é do outro lado, então a resposta é sua. Falso quando o último a
    /// escrever foi você.
    /// </param>
    public sealed record Conversa(MensagemDeEmail Recente, int Mensagens, bool EsperandoVoce);

    /// <summary>
    /// Agrupa por conversa. Devolve uma entrada por thread, com a mensagem mais recente à
    /// frente, na mesma ordem em que a primeira mensagem de cada thread apareceu no lote.
    /// </summary>
    /// <param name="lote">As mensagens lidas na passada.</param>
    /// <param name="meusEnderecos">
    /// Os endereços do usuário. Uma mensagem cujo remetente é um deles foi ESCRITA por ele, e é
    /// o que faz a conversa passar de "nova mensagem" para "aguardando retorno". Comparação por
    /// e-mail, sem caixa — nomes de exibição mudam, endereço não.
    /// </param>
    public static IReadOnlyList<Conversa> Agrupar(
        IEnumerable<MensagemDeEmail>? lote,
        IEnumerable<string>? meusEnderecos = null)
    {
        var saida = new List<Conversa>();
        if (lote == null) return saida;

        var meus = new HashSet<string>(
            (meusEnderecos ?? Array.Empty<string>())
                .Select(Endereco)
                .Where(e => e.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        // Chave própria para o caso do ThreadId vazio: ali cada mensagem é a própria conversa,
        // e agrupar todas as sem-thread numa só juntaria assuntos que nada têm a ver.
        var grupos = new Dictionary<string, List<MensagemDeEmail>>(StringComparer.Ordinal);
        var ordem = new List<string>();

        foreach (var m in lote)
        {
            if (m == null) continue;

            string chave = string.IsNullOrWhiteSpace(m.ThreadId)
                ? "uid:" + m.Conta + ":" + m.Uid
                : "thr:" + m.Conta + ":" + m.ThreadId;

            if (!grupos.TryGetValue(chave, out var lista))
            {
                lista = new List<MensagemDeEmail>();
                grupos[chave] = lista;
                ordem.Add(chave);
            }

            lista.Add(m);
        }

        foreach (string chave in ordem)
        {
            var lista = grupos[chave];

            // A mais recente, e não a primeira: é a data que a tela mostra e é dela que sai
            // "de quem é a vez". Uma conversa de cinco dias mostrando a data do primeiro
            // e-mail diria que nada aconteceu desde então.
            var recente = lista.OrderBy(m => m.RecebidaUtc).Last();

            saida.Add(new Conversa(
                recente,
                lista.Count,
                EsperandoVoce: !meus.Contains(Endereco(recente.De))));
        }

        return saida;
    }

    /// <summary>
    /// O endereço dentro de um campo De. <c>"Fulano &lt;f@x.com&gt;"</c> vira <c>f@x.com</c>.
    /// <para>
    /// Sem isto a comparação com a conta do usuário falharia sempre que o servidor mandasse o
    /// nome junto — que é quase sempre — e toda conversa apareceria como "nova mensagem".
    /// </para>
    /// </summary>
    public static string Endereco(string? campoDe)
    {
        string t = (campoDe ?? "").Trim();
        if (t.Length == 0) return "";

        int abre = t.LastIndexOf('<');
        int fecha = t.LastIndexOf('>');

        if (abre >= 0 && fecha > abre)
            t = t[(abre + 1)..fecha];

        return t.Trim().Trim('"').Trim();
    }

    /// <summary>
    /// A data como a linha de metadados a escreve: dia relativo na frente, data completa
    /// depois — <c>"Hoje · 10/09/2026, 09:12"</c>.
    /// <para>
    /// O rótulo relativo vale até sete dias e some acima disso, sobrando só a data. Nunca
    /// "há 2 dias" e nunca o formato do sistema: a spec pede <c>dd/MM/yyyy</c> e <c>HH:mm</c>
    /// sempre, porque é o que o usuário vai comparar com o que vê no webmail.
    /// </para>
    /// </summary>
    public static string Quando(DateTime instante, DateTime agora)
    {
        var cultura = new System.Globalization.CultureInfo("pt-BR");
        string completa = instante.ToString("dd/MM/yyyy, HH:mm", cultura);

        int dias = (agora.Date - instante.Date).Days;

        string? relativo = dias switch
        {
            0 => "Hoje",
            1 => "Ontem",
            >= 2 and <= 6 => Maiuscula(cultura.DateTimeFormat.GetDayName(instante.DayOfWeek)),
            _ => null
        };

        return relativo == null ? completa : relativo + " · " + completa;
    }

    /// <summary>O tamanho da conversa como a tela o diz.</summary>
    public static string Tamanho(int mensagens) =>
        mensagens > 1 ? $"{mensagens} respostas" : "1 mensagem";

    /// <summary>De quem é a vez, nas duas únicas formas que a spec admite.</summary>
    public static string DeQuemEhAVez(bool esperandoVoce) =>
        esperandoVoce ? "Nova mensagem" : "Aguardando retorno";

    private static string Maiuscula(string texto) =>
        texto.Length == 0 ? texto : char.ToUpperInvariant(texto[0]) + texto[1..];
}
