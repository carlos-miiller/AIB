using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ProtoNavegador;

/// <summary>
/// Protótipo v1.0 do navegador do AIB, sem IA. Abre o Edge num perfil próprio (você loga uma
/// vez), lê a página de três jeitos e mostra quanto cada um custaria ao modelo:
///   árvore — tudo que é visível na página inteira, com papel e ref;
///   vista  — só o que está na tela agora (o que a IA "veria");
///   ocr    — o texto de uma captura da tela, pelo OCR do Windows.
/// O conteúdo da página fica só em memória: nada dela vai para o disco. O que fica no disco é
/// o perfil do navegador (cookies do login), em ~/.AIB/navegador/perfil.
/// </summary>
internal static class Program
{
    private static IBrowserContext _contexto = null!;
    private static Snapshot? _atual;
    private static int _versao;
    private static string _script = "";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        string perfil = args.Length >= 2 && args[0] == "--perfil"
            ? args[1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".AIB", "navegador", "perfil");
        Directory.CreateDirectory(perfil);
        _script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshot.js"));

        using var pw = await Playwright.CreateAsync();
        _contexto = await pw.Chromium.LaunchPersistentContextAsync(perfil, new()
        {
            Channel = "msedge",
            Headless = false,
            ViewportSize = new() { Width = 1366, Height = 768 },
            DeviceScaleFactor = 1,
            Locale = "pt-BR",
        });

        Console.WriteLine($"Navegador aberto. Perfil: {perfil}");
        Console.WriteLine("Logue no site pela janela se precisar. Digite 'ajuda' para os comandos.\n");

        while (true)
        {
            Console.Write($"{(_atual is null ? "" : $"[s{_atual.Versao}] ")}> ");
            string? linha = Console.ReadLine();
            if (linha is null) break;
            linha = linha.Trim();
            if (linha.Length == 0) continue;

            string cmd = linha.Split(' ', 2)[0].ToLowerInvariant();
            string resto = linha.Length > cmd.Length ? linha[cmd.Length..].Trim() : "";
            if (cmd is "sair" or "q") break;

            try
            {
                await Executar(cmd, resto);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERRO: {ex.GetType().Name}: {Primeira(ex.Message)}");
            }
            Console.WriteLine();
        }

        await _contexto.CloseAsync();
        return 0;
    }

    private static IPage Pagina => _contexto.Pages.Count > 0 ? _contexto.Pages[^1] : _contexto.NewPageAsync().GetAwaiter().GetResult();

    private static async Task Executar(string cmd, string resto)
    {
        switch (cmd)
        {
            case "ajuda":
            case "?":
                Console.WriteLine("""
                    abrir <url>         navega e lê a página (mostra a vista)
                    ler                 lê de novo a página atual (use depois de navegar pela janela)
                    vista               o que está na tela agora, como a IA veria
                    arvore [n|tudo]     a página inteira (primeiras n linhas; padrão 60)
                    ocultos             o texto que existe na página mas não aparece
                    ocr                 texto da tela pelo OCR do Windows
                    medir               compara árvore, vista, ocultos e OCR em tokens
                    achar <texto>       procura na página inteira (sem acento, sem caixa)
                    tabela [ref|n]      lista as tabelas, ou mostra uma em texto compacto
                    clicar <ref>        clica e lê de novo
                    digitar <ref> <txt> preenche um campo e lê de novo
                    enter <ref>         aperta Enter num campo
                    rolar [cima]        rola uma tela e mostra a vista
                    voltar              volta uma página
                    sair
                    """);
                break;

            case "abrir":
                if (resto.Length == 0) { Console.WriteLine("uso: abrir <url>"); return; }
                if (!resto.Contains("://")) resto = "https://" + resto;
                await Pagina.GotoAsync(resto, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
                await Assentar();
                await Ler();
                MostrarVista();
                break;

            case "ler":
                await Ler();
                MostrarVista();
                break;

            case "vista":
                if (await Precisa()) MostrarVista();
                break;

            case "arvore":
                if (await Precisa()) MostrarArvore(resto);
                break;

            case "ocultos":
                if (await Precisa()) MostrarOcultos();
                break;

            case "ocr":
                Console.WriteLine(await Ocr());
                break;

            case "medir":
                if (await Precisa()) await Medir();
                break;

            case "achar":
                if (await Precisa()) Achar(resto);
                break;

            case "tabela":
                if (await Precisa()) Tabela(resto);
                break;

            case "clicar":
            {
                var loc = Localizar(resto);
                if (loc is null) return;
                await loc.ClickAsync(new() { Timeout = 8000 });
                await Assentar();
                await Ler();
                MostrarVista();
                break;
            }

            case "digitar":
            {
                var partes = resto.Split(' ', 2);
                var loc = Localizar(partes[0]);
                if (loc is null) return;
                await loc.FillAsync(partes.Length > 1 ? partes[1] : "", new() { Timeout = 8000 });
                await Assentar();
                await Ler();
                MostrarVista();
                break;
            }

            case "enter":
            {
                var loc = Localizar(resto);
                if (loc is null) return;
                await loc.PressAsync("Enter", new() { Timeout = 8000 });
                await Assentar();
                await Ler();
                MostrarVista();
                break;
            }

            case "rolar":
                await Pagina.Mouse.WheelAsync(0, resto == "cima" ? -600 : 600);
                await Task.Delay(400);
                await Ler();
                MostrarVista();
                break;

            case "voltar":
                await Pagina.GoBackAsync(new() { Timeout = 15000 });
                await Assentar();
                await Ler();
                MostrarVista();
                break;

            default:
                Console.WriteLine("comando desconhecido; 'ajuda' lista os comandos");
                break;
        }
    }

    // Sistemas como o Bitrix carregam em pedaços. Espera a rede acalmar, sem travar se nunca acalma.
    private static async Task Assentar()
    {
        try { await Pagina.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 6000 }); }
        catch (TimeoutException) { }
        await Task.Delay(300);
    }

    private static async Task<bool> Precisa()
    {
        if (_atual is null) await Ler();
        return _atual is not null;
    }

    // ---------------------------------------------------------------- leitura

    private sealed record No(string Ref, string Papel, string Texto, int Prof, bool Visivel, bool NaTela, int Quadro);

    private sealed class Snapshot
    {
        public int Versao { get; init; }
        public string Url { get; init; } = "";
        public string Titulo { get; init; } = "";
        public List<No> Nos { get; } = new();
        public List<IFrame> Quadros { get; } = new();
        public List<bool> QuadrosPorCima { get; } = new();
        public int Segredos { get; set; }
        public TimeSpan Tempo { get; set; }
    }

    // Senha colada em chat, token, chave: tudo que está na tela iria para o provedor do modelo.
    // Mascara na entrada, antes de qualquer saída. Heurística da v1: palavra longa que mistura
    // pelo menos três tipos de caractere, ou 16 letras minúsculas quase sem vogal (o formato da
    // senha de app do Google). Endereço (http, e-mail) não entra.
    private static readonly Regex Palavra = new(@"\S{10,}", RegexOptions.Compiled);

    private static string Mascarar(string texto, out int segredos)
    {
        int n = 0;
        string r = Palavra.Replace(texto, m =>
        {
            // Citação grudada na palavra ("União.[3][4]") não é segredo.
            string w = Regex.Replace(m.Value, @"\[\w{0,4}\]", "").Trim('.', ',', ';', ':', '(', ')', '"', '\'');
            if (w.Contains("://") || w.StartsWith("www.") || Regex.IsMatch(w, @"^[\w.+-]+@[\w-]+\.[\w.]+$")) return m.Value;
            int tipos = (w.Any(char.IsLower) ? 1 : 0) + (w.Any(char.IsUpper) ? 1 : 0)
                      + (w.Any(char.IsDigit) ? 1 : 0) + (w.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
            // Maiúscula E minúscula: nome de máquina e patrimônio (CPAPS-NB0123) é tudo maiúsculo.
            bool misturado = w.Length >= 12 && tipos >= 3 && w.Count(char.IsDigit) >= 2
                             && w.Any(char.IsLower) && w.Any(char.IsUpper);
            bool senhaDeApp = Regex.IsMatch(w, "^[a-z]{16}$") && w.Count(c => "aeiou".Contains(c)) <= 3;
            if (!misturado && !senhaDeApp) return m.Value;
            n++;
            return "[segredo mascarado]";
        });
        segredos = n;
        return r;
    }

    private static async Task Ler()
    {
        var inicio = DateTime.Now;
        var page = Pagina;
        var snap = new Snapshot { Versao = ++_versao, Url = page.Url, Titulo = await page.TitleAsync() };

        int q = 0;
        foreach (var frame in page.Frames)
        {
            bool quadroPorCima = true;
            if (frame != page.MainFrame)
            {
                // Quadro escondido (iframe sem tamanho) fica de fora: nem a pessoa vê. Quadro
                // coberto por outro painel entra, mas nada dele conta como "na tela".
                try
                {
                    var el = await frame.FrameElementAsync();
                    if (await el.BoundingBoxAsync() is null) continue;
                    quadroPorCima = await el.EvaluateAsync<bool>(@"e => {
                        const r = e.getBoundingClientRect();
                        const x = (Math.max(r.left, 0) + Math.min(r.right, innerWidth)) / 2;
                        const y = (Math.max(r.top, 0) + Math.min(r.bottom, innerHeight)) / 2;
                        const h = document.elementFromPoint(x, y);
                        return !!h && (h === e || e.contains(h));
                    }");
                    if (frame.ParentFrame is { } pai && pai != page.MainFrame)
                    {
                        int ip = snap.Quadros.IndexOf(pai);
                        if (ip >= 0 && !snap.QuadrosPorCima[ip]) quadroPorCima = false;
                    }
                }
                catch { continue; }
            }

            string prefixo = q == 0 ? $"s{snap.Versao}" : $"s{snap.Versao}f{q}";
            JsonElement nos;
            try { nos = await frame.EvaluateAsync<JsonElement>(_script, prefixo); }
            catch { continue; }

            foreach (var n in nos.EnumerateArray())
            {
                string texto = Mascarar(n.GetProperty("texto").GetString() ?? "", out int segredos);
                snap.Segredos += segredos;
                snap.Nos.Add(new No(
                    n.GetProperty("ref").GetString() ?? "",
                    n.GetProperty("papel").GetString() ?? "",
                    texto,
                    n.GetProperty("prof").GetInt32(),
                    n.GetProperty("visivel").GetBoolean(),
                    quadroPorCima && n.GetProperty("naTela").GetBoolean(),
                    q));
            }
            snap.Quadros.Add(frame);
            snap.QuadrosPorCima.Add(quadroPorCima);
            q++;
        }

        snap.Tempo = DateTime.Now - inicio;
        _atual = snap;
    }

    private static string Linha(No n)
    {
        string recuo = new(' ', Math.Min(n.Prof, 6) * 2);
        string r = n.Ref.Length > 0 ? $"[{n.Ref}] " : "";
        return n.Papel switch
        {
            "texto" => $"{recuo}{n.Texto}",
            "linha" => $"{recuo}{r}{n.Texto}",
            _ => n.Texto.Length > 0 ? $"{recuo}{r}{n.Papel} \"{n.Texto}\"" : $"{recuo}{r}{n.Papel}",
        };
    }

    private static string Cabecalho(Snapshot s) => $"# {s.Titulo}\n# {s.Url}";

    private static string TextoDaVista(Snapshot s) =>
        Cabecalho(s) + "\n" + string.Join("\n", s.Nos.Where(n => n.Visivel && n.NaTela).Select(Linha));

    private static string TextoDaArvore(Snapshot s) =>
        Cabecalho(s) + "\n" + string.Join("\n", s.Nos.Where(n => n.Visivel).Select(Linha));

    private static string TextoOculto(Snapshot s) =>
        string.Join("\n", s.Nos.Where(n => !n.Visivel && n.Texto.Length > 0).Select(Linha));

    private static int Tokens(string s) => (s.Length + 3) / 4;

    private static void MostrarVista()
    {
        if (_atual is null) return;
        string t = TextoDaVista(_atual);
        Console.WriteLine(t);
        Console.WriteLine($"--- vista: {t.Length} caracteres, ≈{Tokens(t)} tokens · leitura em {_atual.Tempo.TotalMilliseconds:0} ms");
    }

    private static void MostrarArvore(string arg)
    {
        string t = TextoDaArvore(_atual!);
        var linhas = t.Split('\n');
        int n = arg == "tudo" ? linhas.Length : int.TryParse(arg, out var k) ? k : 60;
        Console.WriteLine(string.Join("\n", linhas.Take(n)));
        if (linhas.Length > n) Console.WriteLine($"… mais {linhas.Length - n} linhas ('arvore tudo' mostra todas)");
        Console.WriteLine($"--- árvore: {linhas.Length} linhas, {t.Length} caracteres, ≈{Tokens(t)} tokens");
    }

    private static void MostrarOcultos()
    {
        string t = TextoOculto(_atual!);
        Console.WriteLine(t.Length == 0 ? "(nada escondido com texto)" : t);
        Console.WriteLine($"--- ocultos: {t.Length} caracteres, ≈{Tokens(t)} tokens — isto a vista não entrega");
    }

    private static async Task Medir()
    {
        var s = _atual!;
        string arvore = TextoDaArvore(s), vista = TextoDaVista(s), ocultos = TextoOculto(s);
        var ini = DateTime.Now;
        string ocr = await Ocr();
        var tempoOcr = DateTime.Now - ini;

        int interativos = s.Nos.Count(n => n.Visivel && n.Ref.Length > 0);
        int interativosNaTela = s.Nos.Count(n => n.Visivel && n.NaTela && n.Ref.Length > 0);

        Console.WriteLine($"{Cabecalho(s)}");
        Console.WriteLine($"{"forma",-28}{"caracteres",12}{"≈tokens",10}   observação");
        Console.WriteLine($"{"árvore (página inteira)",-28}{arvore.Length,12}{Tokens(arvore),10}   {interativos} elementos com ref");
        Console.WriteLine($"{"vista (só a tela)",-28}{vista.Length,12}{Tokens(vista),10}   {interativosNaTela} elementos com ref");
        Console.WriteLine($"{"ocr (só a tela)",-28}{ocr.Length,12}{Tokens(ocr),10}   sem ref, sem papel · {tempoOcr.TotalMilliseconds:0} ms");
        Console.WriteLine($"{"ocultos (fora da vista)",-28}{ocultos.Length,12}{Tokens(ocultos),10}   texto que existe mas não aparece");
        Console.WriteLine($"leitura do DOM: {s.Tempo.TotalMilliseconds:0} ms · quadros lidos: {s.Quadros.Count}, por cima: {s.QuadrosPorCima.Count(b => b)}");
        Console.WriteLine($"segredos mascarados: {s.Segredos}");
        Console.WriteLine("tokens estimados por caracteres/4.");
    }

    // ---------------------------------------------------------------- pesquisa

    private static string SemAcento(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().ToLowerInvariant();
    }

    private static void Achar(string termo)
    {
        if (termo.Length == 0) { Console.WriteLine("uso: achar <texto>"); return; }
        var nos = _atual!.Nos.Where(n => n.Visivel).ToList();
        string alvo = SemAcento(termo);
        var achados = new List<int>();
        for (int i = 0; i < nos.Count; i++)
            if (SemAcento(nos[i].Texto).Contains(alvo)) achados.Add(i);

        var sb = new StringBuilder();
        int ultimo = -2;
        foreach (int i in achados.Take(20))
        {
            // Uma linha antes e uma depois, para dar contexto sem entregar a página.
            for (int j = Math.Max(0, i - 1); j <= Math.Min(nos.Count - 1, i + 1); j++)
            {
                if (j <= ultimo) continue;
                if (j > ultimo + 1 && sb.Length > 0) sb.AppendLine("  ⋯");
                string marca = j == i ? "» " : "  ";
                string tela = nos[j].NaTela ? "" : "  (fora da vista)";
                sb.AppendLine(marca + Linha(nos[j]).TrimStart() + tela);
                ultimo = j;
            }
        }
        string t = sb.ToString().TrimEnd();
        Console.WriteLine(achados.Count == 0 ? $"nada com \"{termo}\"" : t);
        if (achados.Count > 20) Console.WriteLine($"… {achados.Count - 20} ocorrências a mais");
        Console.WriteLine($"--- achar: {achados.Count} ocorrência(s), ≈{Tokens(t)} tokens");
    }

    private static void Tabela(string refe)
    {
        var nos = _atual!.Nos;
        var indices = Enumerable.Range(0, nos.Count).Where(x => nos[x].Papel == "tabela" && nos[x].Visivel).ToList();
        int i = refe.Length == 0 ? -1
            : int.TryParse(refe, out int ordem) ? (ordem >= 1 && ordem <= indices.Count ? indices[ordem - 1] : -1)
            : nos.FindIndex(n => n.Ref.Equals(refe, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            // Sem ref (ou ref errada): lista as tabelas com a primeira linha de cada, para escolher.
            if (refe.Length > 0) Console.WriteLine("ref não encontrada.");
            // Tabela de uma linha só é ficha ("Status: | Pending"), não lista: conta, não lista.
            int k = 0, fichas = 0;
            foreach (int x in indices)
            {
                k++;
                var primeira = nos.Skip(x + 1).TakeWhile(n => n.Prof > nos[x].Prof).FirstOrDefault(n => n.Papel == "linha");
                int total = nos.Skip(x + 1).TakeWhile(n => n.Prof > nos[x].Prof).Count(n => n.Papel == "linha");
                if (total <= 1) { fichas++; continue; }
                Console.WriteLine($"{k}. [{nos[x].Ref}] {total} linha(s){(nos[x].NaTela ? "" : " (fora da vista)")}: {primeira?.Texto ?? "—"}");
            }
            if (fichas > 0) Console.WriteLine($"(+ {fichas} tabela(s) de uma linha só — fichas, não listas)");
            if (indices.Count == 0) Console.WriteLine("nenhuma tabela nesta página");
            return;
        }

        int prof = nos[i].Prof;
        var sb = new StringBuilder();
        int linhas = 0;
        for (int j = i + 1; j < nos.Count && nos[j].Prof > prof && nos[j].Quadro == nos[i].Quadro; j++)
        {
            if (nos[j].Papel != "linha" || !nos[j].Visivel) continue;
            sb.AppendLine(nos[j].Texto);
            linhas++;
        }
        string t = sb.ToString().TrimEnd();
        Console.WriteLine(linhas == 0 ? "(tabela sem linhas legíveis — pode ser uma grade feita de div; tente 'achar')" : t);
        Console.WriteLine($"--- tabela: {linhas} linha(s), ≈{Tokens(t)} tokens");
    }

    // ---------------------------------------------------------------- ação

    private static ILocator? Localizar(string refe)
    {
        if (_atual is null) { Console.WriteLine("leia a página antes ('ler')"); return null; }
        var m = Regex.Match(refe, @"^s(\d+)(?:f(\d+))?e\d+$", RegexOptions.IgnoreCase);
        if (!m.Success) { Console.WriteLine("ref inválida (ex.: s3e40)"); return null; }
        if (int.Parse(m.Groups[1].Value) != _atual.Versao)
        {
            // A página mudou desde aquela leitura: o elemento pode nem existir mais.
            Console.WriteLine($"ref de uma leitura antiga (s{m.Groups[1].Value}); a atual é s{_atual.Versao}. Recusado.");
            return null;
        }
        int q = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (q >= _atual.Quadros.Count) { Console.WriteLine("quadro não existe mais"); return null; }
        return _atual.Quadros[q].Locator($"[data-aib-ref='{refe.ToLowerInvariant()}']");
    }

    // ---------------------------------------------------------------- ocr

    private static async Task<string> Ocr()
    {
        // A captura fica em memória; não é gravada.
        byte[] png = await Pagina.ScreenshotAsync(new() { Type = ScreenshotType.Png });
        using var ms = new MemoryStream(png);
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var motor = OcrEngine.TryCreateFromLanguage(new Language("pt-BR")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (motor is null) return "(OCR indisponível: nenhum idioma de OCR instalado no Windows)";
        if (bmp.PixelWidth > OcrEngine.MaxImageDimension || bmp.PixelHeight > OcrEngine.MaxImageDimension)
            return $"(imagem maior que o limite do OCR: {OcrEngine.MaxImageDimension}px)";

        var res = await motor.RecognizeAsync(bmp);
        return string.Join("\n", res.Lines.Select(l => l.Text));
    }

    private static string Primeira(string s) => s.Split('\n')[0];
}
