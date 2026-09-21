using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AIB.Services.Mail;

/// <summary>Uma conversa que o usuário mandou ignorar.</summary>
/// <param name="Chave">A conversa: conta + thread, ou conta + assunto + data quando não há thread.</param>
/// <param name="UltimaMensagemUtc">A mensagem mais recente dela no momento em que foi ignorada.</param>
/// <param name="IgnoradaEmUtc">Quando o usuário clicou. É o que decide quando a linha envelhece.</param>
public sealed record Ignorada(string Chave, DateTime UltimaMensagemUtc, DateTime IgnoradaEmUtc);

/// <summary>
/// As conversas de e-mail que o usuário tirou da lista — o botão "Ignorar" de §3.10.
/// <para>
/// IGNORAR NÃO É APAGAR, e não toca o servidor: a caixa continua somente leitura. A conversa só
/// sai da tela da AIB — da lista da área central, da pilha do orbe e do que volta do disco no
/// arranque.
/// </para>
/// <para>
/// ATÉ CHEGAR MENSAGEM NOVA. A triagem erra para o lado de mostrar, e o ignorar segue a mesma
/// regra: ignorou-se o que estava escrito até ali. Uma resposta nova na conversa é informação
/// que o usuário ainda não viu, e esconder essa também transformaria um clique de arrumação num
/// silenciamento permanente.
/// </para>
/// <para>
/// AUDITÁVEL: o arquivo é JSON legível em <c>~/.AIB/email/ignoradas.json</c>, e cada passada do
/// vigia põe as ignoradas na lista de descartados, com o motivo. Nada some sem deixar rastro.
/// </para>
/// </summary>
public sealed class ConversasIgnoradas
{
    public const string NomeDoArquivo = "ignoradas.json";

    /// <summary>
    /// Depois disto a linha sai. Alinhado com o arquivo de conversas: passado esse prazo a
    /// própria conversa já saiu do disco, e a linha não teria mais o que esconder.
    /// </summary>
    public const int DiasMantidos = ArquivoDeConversas.DiasMantidos;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _porta = new();
    private readonly string _caminho;
    private readonly Func<DateTime> _agoraUtc;

    public ConversasIgnoradas(string? raizDeDados = null, Func<DateTime>? agoraUtc = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(raizDeDados) ? DirectoryService.DataDir : raizDeDados!;
        _caminho = Path.Combine(baseDir, "email", NomeDoArquivo);
        _agoraUtc = agoraUtc ?? (() => DateTime.UtcNow);
    }

    public string Caminho => _caminho;

    /// <summary>
    /// A chave de uma linha da lista.
    /// <para>
    /// Com thread, a mesma do arquivo de conversas. SEM thread, não dá para usar
    /// <c>conta|uid:0</c>: todas as mensagens sem thread da conta cairiam na mesma chave —
    /// ignorar uma ignoraria todas. Assunto e data exatos identificam aquela mensagem e nenhuma
    /// outra.
    /// </para>
    /// <para>
    /// <see cref="MailSummary"/> passou a carregar o UID nas linhas sem thread, e a conversa com
    /// a IA usa ele (<see cref="ArquivoDeConversas.ChaveDaConversa"/>). Esta chave NÃO mudou
    /// junto: o <c>ignoradas.json</c> já gravado usa assunto + data, e trocar o formato
    /// devolveria à tela tudo o que o usuário tinha ignorado. Por levar o assunto, ela não vai
    /// para o log em claro — quem registra usa o hash (ver <c>MailDigestService.Ignorar</c>).
    /// </para>
    /// </summary>
    public static string ChaveDe(MailSummary item) =>
        string.IsNullOrWhiteSpace(item.ThreadId)
            ? $"{item.Account}|sem-thread:{item.Name}|{Utc(item.LastMessageAt):o}"
            : ArquivoDeConversas.Chave(item.Account, item.ThreadId, 0);

    /// <summary>Tira a conversa da lista até chegar mensagem nova nela.</summary>
    public void Ignorar(MailSummary item)
    {
        if (item == null) return;

        string chave = ChaveDe(item);

        lock (_porta)
        {
            var linhas = Ler().Where(i => i.Chave != chave).ToList();
            linhas.Add(new Ignorada(chave, Utc(item.LastMessageAt), _agoraUtc()));
            Gravar(Podar(linhas));
        }
    }

    /// <summary>
    /// Separa o que continua na tela. Devolve também quantas saíram, para a lista de descartados.
    /// <para>
    /// Uma conversa volta quando a mensagem mais recente dela é MAIS NOVA que a registrada no
    /// clique. Igual não basta: a mesma conversa relida do disco chega com a mesma data.
    /// </para>
    /// </summary>
    public (IReadOnlyList<MailSummary> Visiveis, int Ignoradas) Filtrar(IEnumerable<MailSummary>? itens)
    {
        var lista = itens?.ToList() ?? new List<MailSummary>();
        if (lista.Count == 0) return (lista, 0);

        var ignoradas = Ler()
            .GroupBy(i => i.Chave, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        if (ignoradas.Count == 0) return (lista, 0);

        var visiveis = lista
            .Where(i => !(ignoradas.TryGetValue(ChaveDe(i), out var ig)
                          && Utc(i.LastMessageAt) <= ig.UltimaMensagemUtc))
            .ToList();

        return (visiveis, lista.Count - visiveis.Count);
    }

    /// <summary>O que está gravado. Arquivo ausente ou ilegível é lista vazia: ignorar é conforto.</summary>
    public IReadOnlyList<Ignorada> Ler()
    {
        try
        {
            if (!File.Exists(_caminho)) return Array.Empty<Ignorada>();
            return JsonSerializer.Deserialize<List<Ignorada>>(File.ReadAllText(_caminho), Json)
                   ?? new List<Ignorada>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] não consegui ler as conversas ignoradas: {ex.Message}");
            return Array.Empty<Ignorada>();
        }
    }

    private List<Ignorada> Podar(List<Ignorada> linhas)
    {
        var limite = _agoraUtc().AddDays(-DiasMantidos);
        return linhas.Where(i => i.IgnoradaEmUtc >= limite).ToList();
    }

    private void Gravar(List<Ignorada> linhas)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_caminho)!);
            File.WriteAllText(_caminho, JsonSerializer.Serialize(linhas, Json));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] não consegui gravar as conversas ignoradas: {ex.Message}");
        }
    }

    private static DateTime Utc(DateTime instante) =>
        instante == default ? default : instante.ToUniversalTime();
}
