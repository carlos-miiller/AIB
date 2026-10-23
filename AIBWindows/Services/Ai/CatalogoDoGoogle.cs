using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIB.Services.Ai;

/// <summary>
/// Os modelos que a chave do Google enxerga (<c>GET /v1beta/models</c>, grátis), para a lista da
/// tela. Antes a lista era fixa e envelhecia: oferecia o <c>gemini-2.5-pro</c>, que o Google só
/// serve a quem já o usava, e escolhê-lo dava 404 (23/10/2026).
/// <para>
/// Só os que conversam: aceitam <c>generateContent</c> e não são de imagem, voz, vídeo ou
/// embedding. Falhou ou sem chave, a tela volta à lista fixa (<see cref="ProvedoresDeIa.ModelosDoGoogle"/>).
/// </para>
/// </summary>
public static class CatalogoDoGoogle
{
    /// <summary>A raiz da API nativa: a URL do provedor sem o <c>/openai</c> do endpoint compatível.</summary>
    public const string Raiz = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>Teto de páginas: a lista cabe em uma; isto só impede laço com token que não acaba.</summary>
    private const int TetoDePaginas = 10;

    /// <summary>Trechos de nome de modelo que não conversa, mesmo aceitando <c>generateContent</c>.</summary>
    private static readonly string[] NaoConversa =
        { "embedding", "imagen", "image", "tts", "audio", "live", "veo", "aqa", "robotics", "computer-use" };

    /// <summary>
    /// Os modelos de conversa da chave, aliases <c>-latest</c> primeiro e os mais novos depois.
    /// Vazio sem chave, sem rede ou com recusa.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListarAsync(HttpClient http, string chave, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chave)) return Array.Empty<string>();

        var todos = new List<string>();
        string? pagina = null;

        try
        {
            for (int i = 0; i < TetoDePaginas; i++)
            {
                string url = Raiz + "/models?pageSize=1000" + (pagina == null ? "" : "&pageToken=" + Uri.EscapeDataString(pagina));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("x-goog-api-key", chave);

                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[CONFIG] modelos do Google indisponíveis ({(int)resp.StatusCode}).");
                    return Array.Empty<string>();
                }

                todos.AddRange(Ler(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), out pagina));
                if (string.IsNullOrEmpty(pagina)) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[CONFIG] modelos do Google indisponíveis: {ex.Message}");
            return Array.Empty<string>();
        }

        return Ordenar(todos);
    }

    /// <summary>Interpreta uma página de <c>/models</c>. Público para ensaio.</summary>
    public static IReadOnlyList<string> Ler(string json, out string? proximaPagina)
    {
        var saida = new List<string>();
        proximaPagina = null;

        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;

        if (raiz.TryGetProperty("nextPageToken", out var np) && np.ValueKind == JsonValueKind.String)
            proximaPagina = np.GetString();

        if (!raiz.TryGetProperty("models", out var modelos) || modelos.ValueKind != JsonValueKind.Array) return saida;

        foreach (var m in modelos.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            if (!m.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;

            string nome = n.GetString() ?? "";
            if (nome.StartsWith("models/", StringComparison.Ordinal)) nome = nome["models/".Length..];

            bool gera = m.TryGetProperty("supportedGenerationMethods", out var metodos)
                        && metodos.ValueKind == JsonValueKind.Array
                        && metodos.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "generateContent");

            if (gera && Conversa(nome) && !saida.Contains(nome)) saida.Add(nome);
        }

        return saida;
    }

    /// <summary>Se o nome é de um Gemini de texto. Gemma e afins ficam de fora: não chamam ferramenta.</summary>
    public static bool Conversa(string nome) =>
        nome.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
        && !NaoConversa.Any(t => nome.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Aliases <c>-latest</c> primeiro; depois do nome maior para o menor (os mais novos em cima).</summary>
    public static IReadOnlyList<string> Ordenar(IEnumerable<string> nomes) =>
        nomes.Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(n => n.EndsWith("-latest", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
             .ThenByDescending(n => n, StringComparer.OrdinalIgnoreCase)
             .ToList();
}
