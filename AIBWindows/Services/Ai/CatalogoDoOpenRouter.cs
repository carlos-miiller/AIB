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
/// <param name="Parametros">
/// O <c>supported_parameters</c> do catálogo. É por ele que o provider decide o que mandar: com
/// <c>require_parameters</c> ligado, um campo que o modelo não aceita derruba a requisição inteira.
/// </param>
public sealed record ModeloDoOpenRouter(
    string Id, string Nome, int Janela, bool UsaFerramentas, bool Raciocina,
    decimal? EntradaPorMilhao, decimal? SaidaPorMilhao,
    IReadOnlySet<string>? Parametros = null)
{
    /// <summary>Se o modelo aceita o parâmetro. Sem a lista, ninguém sabe — e a resposta é sim.</summary>
    public bool Aceita(string parametro) => Parametros == null || Parametros.Contains(parametro);

    /// <summary>
    /// Variante <c>:batch</c>: metade do preço, mas a resposta chega depois, em até 24 h. Não
    /// serve para conversa — quem a escolhia na lista ficava esperando uma resposta que não vinha.
    /// </summary>
    public bool EmLote => Id.EndsWith(":batch", StringComparison.OrdinalIgnoreCase);

    /// <summary>Se entra nas listas de escolha: precisa de ferramentas e de resposta na hora.</summary>
    public bool ServeParaConversa => UsaFerramentas && !EmLote;

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
    /// Quando a última leitura falhou. O provider consulta o catálogo antes de cada requisição;
    /// sem esta pausa, uma rede sem acesso ao /models custaria uma tentativa por turno.
    /// </summary>
    private static DateTime _falhouEm = DateTime.MinValue;

    private static readonly TimeSpan PausaDepoisDeFalhar = TimeSpan.FromMinutes(10);

    /// <summary>O modelo do catálogo já baixado, sem ir à rede. Nulo se não há catálogo ou id.</summary>
    public static ModeloDoOpenRouter? NoCache(string id) =>
        _cache?.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// O modelo, baixando o catálogo se preciso. Nulo se a rede falhar ou o id não existir —
    /// quem chama segue sem saber o que o modelo aceita, como antes.
    /// </summary>
    public static async Task<ModeloDoOpenRouter?> BuscarAsync(HttpClient http, string id, CancellationToken ct)
    {
        if (_cache == null && DateTime.UtcNow - _falhouEm > PausaDepoisDeFalhar)
        {
            // Prazo PRÓPRIO, curto. O HttpClient do provider não tem timeout — o turno pode durar
            // minutos, e quem vigia o silêncio é o prazo do stream. Mas esta busca vem ANTES do
            // turno: um /models pendurado segurava a conversa inteira sem nada na tela. O
            // catálogo é ajuda, não requisito; passado o prazo o turno segue sem ele, como já
            // seguia quando a rede devolvia erro.
            using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
            prazo.CancelAfter(PrazoDaBusca);
            try
            {
                await ListarAsync(http, prazo.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Cancelamento do PRAZO, não de quem chamou: conta como falha, com a mesma pausa
                // de antes para não pagar os quinze segundos a cada turno.
                _falhouEm = DateTime.UtcNow;
                Console.WriteLine($"[CONFIG] catálogo do OpenRouter não respondeu em {PrazoDaBusca.TotalSeconds:0}s; seguindo sem ele.");
            }
        }

        return NoCache(id);
    }

    /// <summary>Quanto o turno espera pelo catálogo antes de seguir sem ele.</summary>
    public static readonly TimeSpan PrazoDaBusca = TimeSpan.FromSeconds(15);

    /// <summary>Ensaio: põe um catálogo no lugar, sem rede. Nulo limpa.</summary>
    public static void DefinirCache(IReadOnlyList<ModeloDoOpenRouter>? modelos)
    {
        _cache = modelos;
        _falhouEm = DateTime.MinValue;
    }

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
            if (!resp.IsSuccessStatusCode)
            {
                _falhouEm = DateTime.UtcNow;
                return Array.Empty<ModeloDoOpenRouter>();
            }

            var lista = Ler(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (lista.Count > 0) _cache = lista; else _falhouEm = DateTime.UtcNow;
            return lista;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _falhouEm = DateTime.UtcNow;
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
                entrada, saida, parametros));
        }

        return modelos.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Os provedores de cada modelo já consultados, pelo id do modelo.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ProvedorDoModelo>> _provedores =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Os provedores que servem o modelo (<c>GET /models/{id}/endpoints</c>, público), com o que
    /// importa para escolher: preço de entrada, de cache e de saída, quantização, disponibilidade e
    /// velocidade. Vazio se a rede falhar ou o id não existir — a tela deixa digitar.
    /// </summary>
    public static async Task<IReadOnlyList<ProvedorDoModelo>> ProvedoresDoModeloAsync(HttpClient http, string id, CancellationToken ct = default)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0 || !id.Contains('/')) return Array.Empty<ProvedorDoModelo>();
        if (_provedores.TryGetValue(id, out var guardados)) return guardados;

        try
        {
            string caminho = string.Join("/", id.Split('/').Select(Uri.EscapeDataString));
            using var resp = await http.GetAsync($"{ProvedoresDeIa.UrlDoOpenRouter}/models/{caminho}/endpoints", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<ProvedorDoModelo>();

            var lista = LerProvedores(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (lista.Count > 0) _provedores[id] = lista;
            return lista;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[CONFIG] provedores de {id} indisponíveis: {ex.Message}");
            return Array.Empty<ProvedorDoModelo>();
        }
    }

    /// <summary>
    /// Interpreta a resposta de <c>/models/{id}/endpoints</c>. Público para ensaio.
    /// <para>
    /// Um nome por provedor, mesmo quando ele serve variantes (fp4 e fp8): o roteamento da AIB
    /// pede pelo NOME, e duas linhas "DeepInfra" iguais na lista só confundiriam. Fica a variante
    /// mais disponível. A ordem é a de escolha: os que aceitam ferramentas e estão no ar primeiro,
    /// e entre eles, o mais barato no que mais pesa numa conversa longa — a entrada lida do cache.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ProvedorDoModelo> LerProvedores(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("endpoints", out var endpoints) || endpoints.ValueKind != JsonValueKind.Array)
            return Array.Empty<ProvedorDoModelo>();

        var todos = new List<ProvedorDoModelo>();

        foreach (var e in endpoints.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string nome = Texto(e, "provider_name");
            if (nome.Length == 0) continue;

            decimal? entrada = null, cache = null, saida = null;
            if (e.TryGetProperty("pricing", out var preco) && preco.ValueKind == JsonValueKind.Object)
            {
                entrada = PorMilhao(Texto(preco, "prompt"));
                cache = PorMilhao(Texto(preco, "input_cache_read"));
                saida = PorMilhao(Texto(preco, "completion"));
            }

            bool ferramentas = e.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array
                               && sp.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "tools");

            todos.Add(new ProvedorDoModelo(
                nome,
                Texto(e, "quantization") is { Length: > 0 } q && q != "unknown" ? q : null,
                entrada, cache, saida,
                Numero(e, "uptime_last_30m"),
                Numero(e, "throughput_last_30m"),
                ferramentas,
                !e.TryGetProperty("status", out var st) || st.ValueKind != JsonValueKind.Number || st.GetInt32() >= 0));
        }

        return todos
            .GroupBy(p => p.Nome, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.Ativo).ThenByDescending(p => p.Disponibilidade ?? 0).First())
            .OrderByDescending(p => p.UsaFerramentas && p.Ativo)
            .ThenBy(p => p.CachePorMilhao ?? p.EntradaPorMilhao ?? decimal.MaxValue)
            .ThenBy(p => p.EntradaPorMilhao ?? decimal.MaxValue)
            .ThenBy(p => p.Nome, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double? Numero(JsonElement el, string nome) =>
        el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    /// <summary>O catálogo dá preço por TOKEN, em texto ("0.00000027"). A tela fala por milhão.</summary>
    private static decimal? PorMilhao(string porToken) =>
        decimal.TryParse(porToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v * 1_000_000m
            : null;

    private static string Texto(JsonElement el, string nome) =>
        el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>Um provedor que serve um modelo no OpenRouter, com o que importa para escolher.</summary>
/// <param name="Nome">O nome usado no roteamento (<c>provider.order</c>) e no campo <c>provider</c> do stream.</param>
/// <param name="CachePorMilhao">US$ por milhão de tokens de entrada LIDOS DO CACHE. Nulo: o provedor não cacheia.</param>
/// <param name="Disponibilidade">% no ar nos últimos 30 minutos.</param>
/// <param name="TokensPorSegundo">Velocidade de geração nos últimos 30 minutos.</param>
/// <param name="Ativo">Falso quando o OpenRouter marca o provedor como degradado.</param>
public sealed record ProvedorDoModelo(
    string Nome, string? Quantizacao,
    decimal? EntradaPorMilhao, decimal? CachePorMilhao, decimal? SaidaPorMilhao,
    double? Disponibilidade, double? TokensPorSegundo, bool UsaFerramentas, bool Ativo)
{
    /// <summary>"entrada US$ 0,06 · cache 0,015 · saída 0,18 /M · fp8 · 99,8% no ar · 45 tok/s".</summary>
    public string Resumo
    {
        get
        {
            var pt = CultureInfo.GetCultureInfo("pt-BR");
            var partes = new List<string>();

            if (EntradaPorMilhao is decimal e)
                partes.Add($"entrada US$ {e.ToString("0.###", pt)}"
                           + (CachePorMilhao is decimal c ? $" · cache {c.ToString("0.####", pt)}" : " · sem cache")
                           + (SaidaPorMilhao is decimal s ? $" · saída {s.ToString("0.###", pt)}" : "") + " /M");
            if (Quantizacao != null) partes.Add(Quantizacao);
            if (Disponibilidade is double d) partes.Add($"{d.ToString("0.#", pt)}% no ar");
            if (TokensPorSegundo is double t && t > 0) partes.Add($"{t:0} tok/s");
            if (!UsaFerramentas) partes.Add("SEM ferramentas");
            if (!Ativo) partes.Add("degradado");

            return string.Join(" · ", partes);
        }
    }

    /// <summary>A linha de cima da lista. O item de nome vazio é o "deixar o OpenRouter escolher".</summary>
    public string Rotulo => Nome.Length == 0 ? "Automático — o OpenRouter escolhe" : Nome;

    /// <summary>O item "automático", primeiro da lista.</summary>
    public static readonly ProvedorDoModelo Automatico = new("", null, null, null, null, null, null, true, true);

    /// <summary>O ComboBox editável mostra este texto na caixa: o nome, que é o que vale no roteamento.</summary>
    public override string ToString() => Nome;
}