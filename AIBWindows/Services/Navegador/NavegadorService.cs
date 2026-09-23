using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace AIB.Services.Navegador;

/// <summary>
/// O que a ferramenta <c>browser</c> pede ao navegador. Interface para os ensaios trocarem o Edge
/// de verdade por um falso: nenhum teste abre janela.
/// </summary>
public interface INavegador
{
    /// <summary>A última leitura, ou null se nada foi aberto ainda.</summary>
    LeituraDaPagina? Atual { get; }

    Task<LeituraDaPagina> AbrirAsync(string url);
    Task<LeituraDaPagina> LerAsync();
    Task<LeituraDaPagina> ClicarAsync(string refCompleta);
    Task<LeituraDaPagina> DigitarAsync(string refCompleta, string texto, bool enter);
    Task<LeituraDaPagina> RolarAsync(bool paraCima);
    Task<LeituraDaPagina> VoltarAsync();
}

/// <summary>
/// O Edge instalado, controlado pelo Playwright, num perfil próprio em
/// <c>~/.AIB/navegador/perfil</c>.
/// <para>
/// PERFIL PRÓPRIO, JANELA VISÍVEL. O usuário loga uma vez pela janela (senha e 2FA são dele; a IA
/// nunca vê nem digita senha) e o login fica guardado nesse perfil, separado do Edge do dia a
/// dia. A janela fica à vista para ele acompanhar e intervir.
/// </para>
/// <para>
/// UMA COISA DE CADA VEZ. As ferramentas de um turno rodam em paralelo, e duas ações na mesma
/// página ao mesmo tempo dariam leituras trocadas. Tudo passa por um semáforo.
/// </para>
/// <para>
/// Nasce preguiçoso: nada abre até a primeira chamada. Quem nunca usa o navegador não paga nada.
/// </para>
/// </summary>
public sealed class NavegadorService : INavegador, IAsyncDisposable
{
    public static NavegadorService Padrao { get; } = new(Path.Combine(DirectoryService.DataDir, "navegador", "perfil"));

    private readonly string _perfil;
    private readonly SemaphoreSlim _vez = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _contexto;
    private List<IFrame> _quadros = new();
    private int _versao;
    private static string? _script;

    public NavegadorService(string perfil) => _perfil = perfil;

    public LeituraDaPagina? Atual { get; private set; }

    // ─────────────────────────────────────────────────────────────── ciclo de vida

    private async Task<IPage> PaginaAsync()
    {
        if (_contexto == null)
        {
            Directory.CreateDirectory(_perfil);
            _playwright ??= await Playwright.CreateAsync();
            try
            {
                _contexto = await _playwright.Chromium.LaunchPersistentContextAsync(_perfil, new()
                {
                    Channel = "msedge",
                    Headless = false,
                    ViewportSize = ViewportSize.NoViewport,
                    Locale = "pt-BR",
                });
            }
            catch (PlaywrightException ex)
            {
                throw new InvalidOperationException(
                    "não foi possível abrir o Microsoft Edge (ele está instalado? o perfil está aberto em outra janela do AIB?): "
                    + Primeira(ex.Message), ex);
            }

            // O usuário fechou a janela: a próxima chamada abre de novo, e a leitura antiga some.
            _contexto.Close += (_, _) =>
            {
                _contexto = null;
                Atual = null;
                _quadros = new();
            };
        }

        return _contexto.Pages.Count > 0 ? _contexto.Pages[^1] : await _contexto.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_contexto != null) await _contexto.CloseAsync(); } catch { }
        _playwright?.Dispose();
        _contexto = null;
        _playwright = null;
    }

    // ─────────────────────────────────────────────────────────────── ações

    public Task<LeituraDaPagina> AbrirAsync(string url) => NaVez(async () =>
    {
        var page = await PaginaAsync();
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
        await AssentarAsync(page);
        return await LerInternoAsync(page);
    });

    public Task<LeituraDaPagina> LerAsync() => NaVez(async () => await LerInternoAsync(await PaginaAsync()));

    public Task<LeituraDaPagina> ClicarAsync(string refCompleta) => NaVez(async () =>
    {
        var page = await PaginaAsync();
        await Localizar(refCompleta).ClickAsync(new() { Timeout = 8000 });
        await AssentarAsync(page);
        return await LerInternoAsync(await PaginaAsync());
    });

    public Task<LeituraDaPagina> DigitarAsync(string refCompleta, string texto, bool enter) => NaVez(async () =>
    {
        var page = await PaginaAsync();
        var campo = Localizar(refCompleta);
        await campo.FillAsync(texto, new() { Timeout = 8000 });
        if (enter) await campo.PressAsync("Enter", new() { Timeout = 8000 });
        await AssentarAsync(page);
        return await LerInternoAsync(await PaginaAsync());
    });

    public Task<LeituraDaPagina> RolarAsync(bool paraCima) => NaVez(async () =>
    {
        var page = await PaginaAsync();
        await page.Mouse.WheelAsync(0, paraCima ? -600 : 600);
        await Task.Delay(400);
        return await LerInternoAsync(page);
    });

    public Task<LeituraDaPagina> VoltarAsync() => NaVez(async () =>
    {
        var page = await PaginaAsync();
        await page.GoBackAsync(new() { Timeout = 15000 });
        await AssentarAsync(page);
        return await LerInternoAsync(page);
    });

    private async Task<T> NaVez<T>(Func<Task<T>> acao)
    {
        await _vez.WaitAsync();
        try { return await acao(); }
        finally { _vez.Release(); }
    }

    private ILocator Localizar(string refCompleta)
    {
        // A ref já foi conferida pela ferramenta contra a leitura atual (versão e existência).
        var m = System.Text.RegularExpressions.Regex.Match(refCompleta, @"^s\d+(?:f(\d+))?e\d+$");
        int q = m.Success && m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
        if (q >= _quadros.Count) throw new InvalidOperationException("o quadro daquela ref não existe mais; leia a página de novo.");
        return _quadros[q].Locator($"[data-aib-ref='{refCompleta}']");
    }

    // ─────────────────────────────────────────────────────────────── leitura

    /// <summary>
    /// Espera a página assentar: a rede acalmar (até 6 s) e as animações finitas acabarem (até 2
    /// s). No Bitrix a tarefa abre num painel que desliza; lida no meio, a vista vinha vazia.
    /// </summary>
    private static async Task AssentarAsync(IPage page)
    {
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 6000 }); }
        catch (TimeoutException) { }
        await Task.Delay(300);

        for (int i = 0; i < 10; i++)
        {
            int rodando = 0;
            foreach (var f in page.Frames)
            {
                try
                {
                    rodando += await f.EvaluateAsync<int>(
                        "() => document.getAnimations().filter(a => a.playState === 'running' && isFinite(a.effect?.getComputedTiming().endTime ?? Infinity)).length");
                }
                catch { }
            }
            if (rodando == 0) break;
            await Task.Delay(200);
        }
    }

    private async Task<LeituraDaPagina> LerInternoAsync(IPage page)
    {
        int versao = ++_versao;
        var nos = new List<NoDaPagina>();
        var quadros = new List<IFrame>();
        var porCima = new List<bool>();

        foreach (var frame in page.Frames)
        {
            bool quadroPorCima = true;
            if (frame != page.MainFrame)
            {
                // Quadro sem tamanho fica de fora; quadro coberto entra, mas nada dele conta como
                // vista. Cinco pontos, não só o centro: painel que cobre parte não o esconde.
                try
                {
                    var el = await frame.FrameElementAsync();
                    if (await el.BoundingBoxAsync() is null) continue;
                    quadroPorCima = await el.EvaluateAsync<bool>(@"e => {
                        const r = e.getBoundingClientRect();
                        const l = Math.max(r.left, 0), t = Math.max(r.top, 0);
                        const w = Math.min(r.right, innerWidth) - l, h = Math.min(r.bottom, innerHeight) - t;
                        if (w <= 0 || h <= 0) return false;
                        return [[.5,.5],[.25,.25],[.75,.25],[.25,.75],[.75,.75]].some(([fx, fy]) => {
                            const p = document.elementFromPoint(l + w * fx, t + h * fy);
                            return !!p && (p === e || e.contains(p));
                        });
                    }");
                    if (frame.ParentFrame is { } pai && pai != page.MainFrame)
                    {
                        int ip = quadros.IndexOf(pai);
                        if (ip >= 0 && !porCima[ip]) quadroPorCima = false;
                    }
                }
                catch { continue; }
            }

            int q = quadros.Count;
            string prefixo = q == 0 ? $"s{versao}" : $"s{versao}f{q}";
            JsonElement lidos;
            try { lidos = await frame.EvaluateAsync<JsonElement>(Script, prefixo); }
            catch { continue; }

            foreach (var n in lidos.EnumerateArray())
            {
                nos.Add(new NoDaPagina(
                    Texto(n, "ref"), Texto(n, "papel"), Texto(n, "texto"),
                    n.GetProperty("prof").GetInt32(),
                    n.GetProperty("visivel").GetBoolean(),
                    quadroPorCima && n.GetProperty("naTela").GetBoolean(),
                    q,
                    Texto(n, "href"),
                    n.TryGetProperty("envia", out var e) && e.ValueKind == JsonValueKind.True));
            }
            quadros.Add(frame);
            porCima.Add(quadroPorCima);
        }

        _quadros = quadros;
        var leitura = new LeituraDaPagina(versao, page.Url, await page.TitleAsync(), nos);
        Atual = leitura;
        return leitura;
    }

    private static string Texto(JsonElement n, string nome) =>
        n.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Script => _script ??= LerScript();

    private static string LerScript()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AIB.Navegador.Snapshot.js")
                      ?? throw new InvalidOperationException("Snapshot.js não está embutido no AIB.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static string Primeira(string s) => s.Split('\n')[0];
}
