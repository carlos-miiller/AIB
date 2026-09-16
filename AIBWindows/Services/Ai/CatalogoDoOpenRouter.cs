using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIB.Services.Ai;

/// <summary>Um modelo do catálogo do OpenRouter, com o que a tela precisa para escolher.</summary>
/// <param name="Id">O identificador que vai na requisição: "deepseek/deepseek-chat-v3".</param>
/// <param name="Janela">Contexto máximo do modelo, em tokens. 0 se o catálogo não disse.</param>
/// <param name="UsaFerramentas">Se aceita <c>tools</c>. Sem isso a AIB não faz nada além de conversar.</param>
/// <param name="Raciocina">Se aceita o parâmetro <c>reasoning</c>.</param>
/// <param name="EntradaPorMilhao">US$ por milhão de tokens de entrada. Nulo se o catálogo não disse.</param>
/// <param name="SaidaPorMilhao">US$ por milhão de tokens de saída.</param>
public sealed record ModeloDoOpenRouter(
    string Id, string Nome, int Janela, bool UsaFerramentas, bool Raciocina,
    decimal? EntradaPorMilhao, decimal? SaidaPorMilhao)
{
    /// <summary>"janela 163.840 · US$ 0,27/M entrada · US$ 1,10/M saída · raciocínio".</summary>
    public string Resumo()
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var partes = new List<string>();

        if (Janela > 0) partes.Add("janela " + Janela.ToString("N0", pt));
        if (EntradaPorMilhao is decimal e) partes.Add(e == 0 ? "grátis" : $"US$ {e.ToString("0.##", pt)}/M entrada");
        if (SaidaPorMilhao is decimal s && s > 0) partes.Add($"US$ {s.ToString("0.##", pt)}/M saída");
        partes.Add(Raciocina ? "raciocínio" : "sem raciocínio");
        if (!UsaFerramentas) partes.Add("SEM ferramentas");

        return string.Join(" · ", partes);
    }
}

/// <summary>
/// O catálogo público do OpenRouter (<c>GET /models</c>, sem chave).
/// <para>
/// Guardado em memória depois da primeira leitura: são centenas de modelos e a lista muda em
/// semanas, não em minutos. Reabrir as configurações não precisa baixar tudo de novo.
/// </para>
/// </summary>
public static class CatalogoDoOpenRouter
{
    private static IReadOnlyList<ModeloDoOpenRouter>? _cache;
    private static readonly SemaphoreSlim _trava = new(1, 1);

    /// <summary>
    /// Os modelos, ordenados por id. Vazio se a rede falhar — a tela deixa digitar o id à mão.
    /// </summary>
    public static async Task<IReadOnlyList<ModeloDoOpenRouter>> ListarAsync(HttpClient http, CancellationToken ct = default)
    {
        if (_cache != null) return _cache;

        await _trava.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache != null) return _cache;

            using var resp = await http.GetAsync(ProvedoresDeIa.UrlDoOpenRouter + "/models", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<ModeloDoOpenRouter>();

            var lista = Ler(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (lista.Count > 0) _cache = lista;
            return lista;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[CONFIG] catálogo do OpenRouter indisponível: {ex.Message}");
            return Array.Empty<ModeloDoOpenRouter>();
        }
        finally
        {
            _trava.Release();
        }
    }

    /// <summary>Interpreta a resposta de <c>/models</c>. Público para ensaio.</summary>
    public static IReadOnlyList<ModeloDoOpenRouter> Ler(string json)
    {
        var modelos = new List<ModeloDoOpenRouter>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return modelos;

        foreach (var m in data.EnumerateArray())
        {
            string id = Texto(m, "id");
            if (id.Length == 0) continue;

            var parametros = m.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array
                ? sp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet()
                : new HashSet<string>();

            int janela = m.TryGetProperty("context_length", out var cl) && cl.ValueKind == JsonValueKind.Number
                ? cl.GetInt32()
                : 0;

            decimal? entrada = null, saida = null;
            if (m.TryGetProperty("pricing", out var preco) && preco.ValueKind == JsonValueKind.Object)
            {
                entrada = PorMilhao(Texto(preco, "prompt"));
                saida = PorMilhao(Texto(preco, "completion"));
            }

            modelos.Add(new ModeloDoOpenRouter(
                id, Texto(m, "name"), janela,
                parametros.Contains("tools"),
                parametros.Contains("reasoning") || parametros.Contains("include_reasoning"),
                entrada, saida));
        }

        return modelos.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>O catálogo dá preço por TOKEN, em texto ("0.00000027"). A tela fala por milhão.</summary>
    private static decimal? PorMilhao(string porToken) =>
        decimal.TryParse(porToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v * 1_000_000m
            : null;

    private static string Texto(JsonElement el, string nome) =>
        el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
