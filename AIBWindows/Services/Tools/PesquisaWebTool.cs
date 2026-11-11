using System;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services.Ai;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Pesquisa na internet pela busca do PROVEDOR (decidido com o usuário em 23/10/2026, contra uma
/// busca própria pelo navegador): o OpenRouter com o plugin <c>web</c>, o Google com o
/// <c>google_search</c>. O Ollama não tem, e aí a ferramenta não é oferecida.
/// <para>
/// A busca roda numa chamada à parte, e o resultado volta como o de qualquer ferramenta. Ver
/// <see cref="Pesquisa"/> para o porquê de não ligar a busca direto na conversa.
/// </para>
/// <para>
/// Não passa pelo portão: só lê, e o provedor que faz a busca já recebe a conversa inteira. Mas
/// a consulta sai para um buscador, e com texto de terceiros no contexto (e-mail, página) uma
/// ordem escondida nele poderia mandar o que está ali dentro de uma consulta. Por isso, nesse
/// caso, o cartão pergunta (<see cref="SoComFalaDoUsuario"/>).
/// </para>
/// </summary>
public sealed class PesquisaWebTool : ITool
{
    /// <summary>Uma consulta é uma frase de busca, não um texto.</summary>
    public const int TetoDaConsulta = 300;

    /// <summary>Quanto a busca pode demorar. A resposta do modelo vem junto, e por isso não é curta.</summary>
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(120);

    private readonly Func<IChatProvider?> _provedor;

    /// <param name="provedor">
    /// O provedor da conversa, lido na hora: o usuário troca de provedor sem reabrir o app. Nulo
    /// quando não dá para montar um.
    /// </param>
    public PesquisaWebTool(Func<IChatProvider?> provedor) => _provedor = provedor;

    public string Name => Ferramentas.Pesquisar;

    public string Description =>
        "Pesquisa na internet e devolve um resumo com as fontes. Para o que é recente ou você não "
        + "sabe com certeza: notícia, preço, versão, documentação. Para ler uma página dada, use browser.";

    public int RequiredLevel => 1;

    public bool SoComFalaDoUsuario => true;

    public bool Oferecida => Provedor()?.TemPesquisa == true;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "query": { "type": "string", "description": "O que pesquisar, como se digitaria num buscador." }
            },
            "required": ["query"]
        }
        """)
    );

    private IChatProvider? Provedor()
    {
        try { return _provedor(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[web_search] sem provedor: {ex.Message}");
            return null;
        }
    }

    /// <summary>A consulta dos argumentos, numa linha só, ou vazio.</summary>
    public static string ConsultaDe(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            if (!doc.RootElement.TryGetProperty("query", out var q) || q.ValueKind != JsonValueKind.String) return "";

            return Regex.Replace(q.GetString() ?? "", @"\s+", " ").Trim();
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public string? Validar(string argumentsJson)
    {
        string consulta = ConsultaDe(argumentsJson);

        if (consulta.Length == 0)
            return "ERRO: 'query' vazio. Passe o que pesquisar.";

        if (consulta.Length > TetoDaConsulta)
            return $"ERRO: a consulta tem {consulta.Length} caracteres; o teto é {TetoDaConsulta}. Pesquise com poucas palavras.";

        // A consulta sai do computador para um buscador: segredo não vai.
        Segredos.Redigir(consulta, out int segredos);
        if (segredos > 0)
            return "ACESSO NEGADO: a consulta tem cara de senha ou chave, e segredo não vai para a internet.";

        return null;
    }

    /// <summary>
    /// O cartão que o registry mostra quando há texto de terceiros no contexto: a consulta
    /// inteira, que é o que sai do computador. Sem "sempre": cada consulta é uma decisão.
    /// </summary>
    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        string consulta = ConsultaDe(argumentsJson);
        if (consulta.Length == 0) return null;

        return new CommandConfirmationContext
        {
            Tool = Name,
            Command = $"PESQUISAR NA INTERNET: \"{consulta}\"",
            Level = userLevel,
            SemSempre = true
        };
    }

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string consulta = ConsultaDe(argumentsJson);

        var provedor = Provedor();
        if (provedor == null || !provedor.TemPesquisa)
            return $"ERRO: o provedor configurado ({provedor?.Name ?? "nenhum"}) não tem busca na internet.";

        ResultadoDaPesquisa r;
        try
        {
            using var prazo = new CancellationTokenSource(Prazo);
            r = await provedor.PesquisarAsync(consulta, prazo.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return $"ERRO: a pesquisa passou de {Prazo.TotalSeconds:0}s sem resposta. Nada foi achado.";
        }
        catch (Exception ex)
        {
            return "ERRO: a pesquisa falhou: " + ex.Message;
        }

        Console.WriteLine($"[web_search] {provedor.Name}: {r.Fontes.Count} fonte(s)"
                          + (r.CustoUsd is decimal c ? $", US$ {c:0.####}" : ""));

        return Formatar(r);
    }

    /// <summary>A resposta e as fontes numeradas, com o trecho curto de cada uma. Público para ensaio.</summary>
    public static string Formatar(ResultadoDaPesquisa r)
    {
        string texto = r.Texto.Trim();
        if (texto.Length == 0 && r.Fontes.Count == 0) return "A pesquisa não achou nada.";

        var sb = new StringBuilder(texto.Length == 0 ? "(o buscador não resumiu; veja as fontes)" : texto);
        if (r.Fontes.Count == 0) return sb.ToString();

        sb.Append("\n\nFontes:");
        for (int i = 0; i < r.Fontes.Count; i++)
        {
            var f = r.Fontes[i];
            sb.Append($"\n{i + 1}. ");
            if (f.Titulo.Length > 0) sb.Append(f.Titulo).Append(" — ");
            sb.Append(f.Url);

            string trecho = Regex.Replace(f.Trecho, @"\s+", " ").Trim();
            if (trecho.Length > Pesquisa.TetoDoTrecho) trecho = trecho[..(Pesquisa.TetoDoTrecho - 1)] + "…";
            if (trecho.Length > 0) sb.Append("\n   ").Append(trecho);
        }

        return sb.ToString();
    }
}
