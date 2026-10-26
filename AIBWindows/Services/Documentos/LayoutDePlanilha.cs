using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClosedXML.Excel;

namespace AIB.Services.Documentos;

/// <summary>
/// O acabamento de uma aba: altura de linha, mesclar, linhas de grade, lista suspensa, filtro e
/// impressão.
/// <para>
/// Visto em 26/10/2026, comparando o painel que a Ellen montou com um modelo de rubrica bem
/// acabado: o que separava os dois não era cor nem fórmula, era o que a ferramenta não deixava
/// fazer — título numa linha alta e mesclada, folha sem grade, linha fina separando blocos,
/// nota escolhida numa lista, página ajustada à largura na impressão.
/// </para>
/// </summary>
public static class LayoutDePlanilha
{
    /// <summary>As chaves de operação que esta classe atende, para a recusa do CSV e a lista de erro.</summary>
    public static readonly string[] Chaves = { "height", "merge", "unmerge", "gridlines", "filter", "print", "image" };

    /// <summary>As imagens que entram na planilha, pela extensão.</summary>
    public static readonly string[] ExtensoesDeImagem = { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

    /// <summary>Tamanho máximo de uma imagem: um logotipo, não uma foto de câmera.</summary>
    public const long TetoDeImagem = 5 * 1024 * 1024;

    /// <summary>Margens de impressão por nome, em polegadas como o Excel guarda (as mesmas do Excel).</summary>
    private static readonly Dictionary<string, (double Lados, double TopoEBase, double Cabecalho)> Margens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["estreita"] = (0.25, 0.75, 0.3),
        ["narrow"] = (0.25, 0.75, 0.3),
        ["normal"] = (0.7, 0.75, 0.3),
        ["larga"] = (1.0, 1.0, 0.5),
        ["wide"] = (1.0, 1.0, 0.5)
    };

    /// <summary>
    /// Aplica a operação se ela for de layout de aba, e devolve a linha do cartão. Nulo quando a
    /// operação é de outro tipo.
    /// </summary>
    public static Mudanca? Processar(IXLWorksheet ws, JsonElement op, int i)
    {
        if (op.TryGetProperty("image", out var imagem)) return Imagem(ws, op, imagem, i);
        if (op.TryGetProperty("height", out var altura)) return Altura(ws, op, altura, i);
        if (op.TryGetProperty("merge", out var mesclar)) return Mesclar(ws, Texto(mesclar, "merge", i), i);
        if (op.TryGetProperty("unmerge", out var desfazer)) return Desmesclar(ws, Texto(desfazer, "unmerge", i), i);
        if (op.TryGetProperty("gridlines", out var grade)) return Grade(ws, grade, i);
        if (op.TryGetProperty("filter", out var filtro)) return Filtro(ws, Texto(filtro, "filter", i), i);
        if (op.TryGetProperty("print", out var impressao)) return Impressao(ws, impressao, i);
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Linhas e células
    // ─────────────────────────────────────────────────────────────────────────

    private static Mudanca Altura(IXLWorksheet ws, JsonElement op, JsonElement altura, int i)
    {
        if (!op.TryGetProperty("rows", out var rows))
            throw new ErroDeDocumento($"operação {i}: 'height' vai com 'rows': {{rows:'1', height:40}} ou {{rows:'2:20', height:'auto'}}.");

        var (de, ate) = Linhas(rows, i);
        string antes = Pontos(ws.Row(de));
        string depois;

        if (altura.ValueKind == JsonValueKind.Number && altura.TryGetDouble(out double pontos))
        {
            if (pontos is < 1 or > 409)
                throw new ErroDeDocumento($"operação {i}: 'height' é de 1 a 409 pontos (o padrão do Excel é 15).");
            ws.Rows(de, ate).Height = pontos;
            depois = pontos.ToString("0.#", Br);
        }
        else if (altura.ValueKind == JsonValueKind.String && string.Equals(altura.GetString(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            // Sem altura gravada, o Excel ajusta a linha ao conteúdo quando abre.
            foreach (var r in ws.Rows(de, ate)) r.ClearHeight();
            depois = "automática";
        }
        else
        {
            throw new ErroDeDocumento($"operação {i}: 'height' é um número em pontos (15 é o padrão) ou 'auto'.");
        }

        return new Mudanca(TipoDeMudanca.Valor, de == ate ? $"Altura da linha {de}" : $"Altura das linhas {de}:{ate}", antes, depois);
    }

    private static Mudanca Mesclar(IXLWorksheet ws, string faixa, int i)
    {
        var r = Faixa(ws, faixa, i);
        if (r.RowCount() * r.ColumnCount() < 2)
            throw new ErroDeDocumento($"operação {i}: mesclar precisa de duas células ou mais ('B1:G1').");

        if (ws.MergedRanges.FirstOrDefault(m => m.Intersects(r)) is { } ja)
            throw new ErroDeDocumento($"operação {i}: {faixa} encosta em {ja.RangeAddress}, que já está mesclada. "
                + $"Desfaça antes ({{unmerge:'{ja.RangeAddress}'}}) no mesmo pedido.");

        // O Excel só mostra a primeira célula da mescla; o resto some da tela sem aviso.
        var escondidas = r.Cells().Skip(1).Where(c => !c.IsEmpty() && !(c.Value.IsText && c.GetString().Length == 0)).Take(3).ToList();
        if (escondidas.Count > 0)
            throw new ErroDeDocumento($"operação {i}: a mescla mostra só {r.FirstCell().Address}, e "
                + string.Join(", ", escondidas.Select(c => $"{c.Address} ('{Apresentacao.Curto(EditorDePlanilha.Mostrar(c) ?? "", 30)}')"))
                + " ficaria escondido. Apague essas células antes (value null) ou mescle outra faixa.");

        r.Merge();
        return new Mudanca(TipoDeMudanca.Valor, $"Mesclar {r.RangeAddress}", "células separadas", "uma célula só");
    }

    private static Mudanca Desmesclar(IXLWorksheet ws, string faixa, int i)
    {
        var r = Faixa(ws, faixa, i);
        var mescladas = ws.MergedRanges.Where(m => m.Intersects(r)).ToList();
        if (mescladas.Count == 0) throw new ErroDeDocumento($"operação {i}: nada mesclado em {faixa}.");

        foreach (var m in mescladas) m.Unmerge();
        string onde = string.Join(", ", mescladas.Select(m => m.RangeAddress.ToString()));
        return new Mudanca(TipoDeMudanca.Valor, $"Desfazer mescla {onde}", "uma célula só", "células separadas");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // A aba
    // ─────────────────────────────────────────────────────────────────────────

    private static Mudanca Grade(IXLWorksheet ws, JsonElement grade, int i)
    {
        if (grade.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ErroDeDocumento($"operação {i}: 'gridlines' é true (mostra as linhas de grade) ou false (esconde).");

        string antes = ws.ShowGridLines ? "aparecem" : "escondidas";
        ws.ShowGridLines = grade.GetBoolean();
        return new Mudanca(TipoDeMudanca.Valor, $"Linhas de grade de \"{ws.Name}\"", antes, ws.ShowGridLines ? "aparecem" : "escondidas");
    }

    private static Mudanca Filtro(IXLWorksheet ws, string faixa, int i)
    {
        string antes = ws.AutoFilter.IsEnabled ? $"filtro em {ws.AutoFilter.Range.RangeAddress}" : "sem filtro";

        if (faixa.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (!ws.AutoFilter.IsEnabled) throw new ErroDeDocumento($"operação {i}: a aba \"{ws.Name}\" não tem filtro.");
            ws.AutoFilter.Clear();
            return new Mudanca(TipoDeMudanca.Valor, "Filtro", antes, "sem filtro");
        }

        var r = Faixa(ws, faixa, i);
        if (r.RowCount() < 2)
            throw new ErroDeDocumento($"operação {i}: o filtro vai na faixa da tabela com o cabeçalho, ex.: 'A1:O26' (a primeira linha ganha as setas).");
        r.SetAutoFilter();
        return new Mudanca(TipoDeMudanca.Valor, "Filtro", antes, $"setas no cabeçalho {r.FirstRow().RangeAddress}");
    }

    private static Mudanca Impressao(IXLWorksheet ws, JsonElement p, int i)
    {
        if (p.ValueKind != JsonValueKind.Object)
            throw new ErroDeDocumento($"operação {i}: 'print' é um objeto: {{print:{{orientation:'landscape', fit:'width', area:'A1:F40'}}}}.");

        var ps = ws.PageSetup;
        var feito = new List<string>();

        foreach (var prop in p.EnumerateObject())
        {
            string? t = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()?.Trim() : null;
            switch (prop.Name.ToLowerInvariant())
            {
                case "orientation":
                    ps.PageOrientation = t?.ToLowerInvariant() switch
                    {
                        "landscape" or "paisagem" => XLPageOrientation.Landscape,
                        "portrait" or "retrato" => XLPageOrientation.Portrait,
                        _ => throw new ErroDeDocumento($"operação {i}: 'orientation' é 'portrait' (em pé) ou 'landscape' (deitada).")
                    };
                    feito.Add(ps.PageOrientation == XLPageOrientation.Landscape ? "página deitada" : "página em pé");
                    break;

                case "fit":
                    switch (t?.ToLowerInvariant())
                    {
                        case "width" or "largura":
                            ps.FitToPages(1, 0);
                            feito.Add("cabe na largura de uma página");
                            break;
                        case "page" or "pagina" or "página":
                            ps.FitToPages(1, 1);
                            feito.Add("cabe numa página só");
                            break;
                        case "none":
                            ps.Scale = 100;
                            feito.Add("tamanho real (100%)");
                            break;
                        default:
                            throw new ErroDeDocumento($"operação {i}: 'fit' é 'width' (todas as colunas numa página de largura), 'page' (tudo numa página) ou 'none'.");
                    }
                    break;

                case "area":
                    if (t is null) throw new ErroDeDocumento($"operação {i}: 'area' é a faixa a imprimir ('B1:G30') ou 'none'.");
                    ps.PrintAreas.Clear();
                    if (t.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        feito.Add("imprime a aba toda");
                    }
                    else
                    {
                        var r = Faixa(ws, t, i);
                        ps.PrintAreas.Add(r.RangeAddress.ToString()!);
                        feito.Add($"imprime só {r.RangeAddress}");
                    }
                    break;

                case "margins":
                    if (t is not null && Margens.TryGetValue(t, out var m))
                    {
                        ps.Margins.Left = ps.Margins.Right = m.Lados;
                        ps.Margins.Top = ps.Margins.Bottom = m.TopoEBase;
                        ps.Margins.Header = ps.Margins.Footer = m.Cabecalho;
                        feito.Add($"margens {t.ToLowerInvariant()}");
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out double cm) && cm is >= 0 and <= 10)
                    {
                        double pol = cm / 2.54;
                        ps.Margins.Left = ps.Margins.Right = ps.Margins.Top = ps.Margins.Bottom = pol;
                        ps.Margins.Header = ps.Margins.Footer = Math.Min(pol, 0.3);
                        feito.Add($"margens de {cm.ToString("0.#", Br)} cm");
                    }
                    else
                    {
                        throw new ErroDeDocumento($"operação {i}: 'margins' é 'estreita', 'normal', 'larga' ou um número em centímetros (ex.: 1).");
                    }
                    break;

                case "repeat_rows":
                    var (de, ate) = Linhas(prop.Value, i);
                    ps.SetRowsToRepeatAtTop(de, ate);
                    feito.Add(de == ate ? $"repete a linha {de} em cada página" : $"repete as linhas {de}:{ate} em cada página");
                    break;

                case "center":
                    if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new ErroDeDocumento($"operação {i}: 'center' é true ou false (centraliza na largura da página).");
                    ps.CenterHorizontally = prop.Value.GetBoolean();
                    feito.Add(ps.CenterHorizontally ? "centralizada na página" : "encostada à esquerda");
                    break;

                default:
                    throw new ErroDeDocumento($"operação {i}: chave de 'print' \"{prop.Name}\" desconhecida. Use orientation, fit, area, margins, repeat_rows ou center.");
            }
        }

        if (feito.Count == 0) throw new ErroDeDocumento($"operação {i}: 'print' veio vazio.");
        return new Mudanca(TipoDeMudanca.Valor, $"Impressão de \"{ws.Name}\"", null, string.Join(" · ", feito));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lista suspensa (vai junto de 'range')
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>{range, validation:{list:[...]}}</c>, <c>{list:'=$C$14:$G$14'}</c> ou <c>'none'</c>. A célula passa
    /// a aceitar só o que está na lista, escolhido numa seta.
    /// </summary>
    public static Mudanca Validacao(IXLWorksheet ws, IXLRange r, JsonElement v, int i)
    {
        string onde = $"Lista suspensa em {r.RangeAddress}";
        var existentes = ws.DataValidations.GetAllInRange(r.RangeAddress).ToList();

        if (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "none", StringComparison.OrdinalIgnoreCase))
        {
            if (existentes.Count == 0) throw new ErroDeDocumento($"operação {i}: {r.RangeAddress} não tem lista suspensa.");
            foreach (var dv in existentes) dv.ClearRanges();
            return new Mudanca(TipoDeMudanca.Sai, onde, "aceita só a lista", null);
        }

        // Aceita a lista solta também: validation:['Sim','Não'].
        JsonElement lista = v.ValueKind == JsonValueKind.Array ? v
            : v.ValueKind == JsonValueKind.Object && v.TryGetProperty("list", out var l) ? l
            : throw new ErroDeDocumento($"operação {i}: 'validation' é {{list:['Sim','Não']}}, {{list:'=$C$14:$G$14'}} ou 'none'.");

        string formula;
        string descricao;
        if (lista.ValueKind == JsonValueKind.String && lista.GetString() is { } refer && refer.StartsWith('='))
        {
            formula = refer[1..];
            descricao = $"aceita só os valores de {formula}";
        }
        else if (lista.ValueKind == JsonValueKind.Array && lista.GetArrayLength() > 0)
        {
            var itens = lista.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToList();
            if (itens.FirstOrDefault(x => x.Contains(',') || x.Contains('"')) is { } ruim)
                throw new ErroDeDocumento($"operação {i}: \"{ruim}\" tem vírgula ou aspas, que a lista do Excel não aceita. "
                    + "Ponha os itens numa faixa da planilha e use {list:'=$H$2:$H$9'}.");
            formula = "\"" + string.Join(",", itens) + "\"";
            if (formula.Length > 257)
                throw new ErroDeDocumento($"operação {i}: a lista passa de 255 caracteres, o limite do Excel. Ponha os itens numa faixa e use {{list:'=$H$2:$H$40'}}.");
            descricao = "aceita só: " + string.Join(", ", itens);
        }
        else
        {
            throw new ErroDeDocumento($"operação {i}: 'list' é uma lista de textos (['Sim','Não']) ou uma faixa começando com '=' ('=$C$14:$G$14').");
        }

        foreach (var dv in existentes) dv.ClearRanges();
        var nova = r.CreateDataValidation();
        nova.List(formula, true);
        nova.IgnoreBlanks = true;
        nova.ShowErrorMessage = true;
        nova.ErrorTitle = "Valor fora da lista";
        nova.ErrorMessage = "Escolha um valor da lista.";

        return new Mudanca(TipoDeMudanca.Valor, onde, existentes.Count > 0 ? "tinha outra lista" : "aceita qualquer valor", descricao);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Link e imagem
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>{cell, link:'https://…'}</c>, <c>'mailto:…'</c>, <c>'#Aba!A1'</c> (outra aba) ou null (tira).
    /// Só http, https e mailto saem da planilha: link para arquivo ou programa num documento que
    /// o usuário vai abrir e repassar é isca, não acabamento.
    /// </summary>
    public static Mudanca Link(IXLCell c, JsonElement v, bool temEstilo, int i)
    {
        string onde = $"Link em {c.Address}";
        string? antes = c.HasHyperlink ? Destino(c.GetHyperlink()) : null;

        if (v.ValueKind == JsonValueKind.Null)
        {
            if (!c.HasHyperlink) throw new ErroDeDocumento($"operação {i}: {c.Address} não tem link.");
            c.Worksheet.Hyperlinks.Delete(c.Address);
            return new Mudanca(TipoDeMudanca.Sai, onde, antes, null);
        }

        string alvo = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        XLHyperlink link;
        bool externo = alvo.Contains("://") || alvo.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        if (!externo && alvo.Contains('!'))
        {
            string endereco = alvo.TrimStart('#');
            string aba = endereco[..endereco.LastIndexOf('!')].Trim('\'');
            if (!c.Worksheet.Workbook.Worksheets.Any(w => string.Equals(w.Name, aba, StringComparison.OrdinalIgnoreCase)))
                throw new ErroDeDocumento($"operação {i}: o link aponta para a aba \"{aba}\", que não existe.");
            link = new XLHyperlink(endereco);
        }
        else if (Uri.TryCreate(alvo, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            link = new XLHyperlink(uri);
        }
        else
        {
            throw new ErroDeDocumento($"operação {i}: 'link' é um endereço http(s)://, mailto: ou outra aba ('#Resumo!A1'). Link para arquivo ou programa não entra.");
        }

        // O ClosedXML pinta o link de azul sublinhado. Com estilo no pedido (um botão verde de
        // letra branca), vale o que foi pedido; sem estilo, fica com cara de link.
        var cor = c.Style.Font.FontColor;
        var sublinhado = c.Style.Font.Underline;
        c.SetHyperlink(link);
        if (temEstilo)
        {
            c.Style.Font.FontColor = cor;
            c.Style.Font.Underline = sublinhado;
        }
        else
        {
            c.Style.Font.FontColor = XLColor.FromHtml("#0563C1");
            c.Style.Font.Underline = XLFontUnderlineValues.Single;
        }
        return new Mudanca(TipoDeMudanca.Valor, onde, antes, alvo);
    }

    private static string Destino(XLHyperlink h) =>
        h.IsExternal ? h.ExternalAddress.ToString() : "#" + h.InternalAddress;

    /// <summary>
    /// <c>{image:'C:\…\logo.png', cell:'B1', width?, height?}</c> (em pixels; um só mantém a proporção)
    /// ou <c>{image:'none', cell:'B1'}</c>, que tira a imagem ancorada na célula. Quem confere se o
    /// arquivo pode ser lido (fora da pasta de dados do AIB) é o DocTool, antes do cartão.
    /// </summary>
    private static Mudanca Imagem(IXLWorksheet ws, JsonElement op, JsonElement imagem, int i)
    {
        string? celula = op.TryGetProperty("cell", out var cp) && cp.ValueKind == JsonValueKind.String ? cp.GetString() : null;
        var (l, col) = celula is null ? (null, null) : Apresentacao.Ponto(celula.Replace("$", ""), celula);
        if (l is null || col is null)
            throw new ErroDeDocumento($"operação {i}: 'image' vai com 'cell', o canto de cima à esquerda da imagem: {{image:'C:\\logo.png', cell:'B1'}}.");
        var canto = ws.Cell(l.Value, col.Value);

        string caminho = imagem.ValueKind == JsonValueKind.String ? imagem.GetString()!.Trim() : "";
        if (caminho.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var ali = ws.Pictures.Where(p => p.TopLeftCell.Address.Equals(canto.Address)).ToList();
            if (ali.Count == 0) throw new ErroDeDocumento($"operação {i}: não há imagem em {canto.Address}.");
            foreach (var p in ali) p.Delete();
            return new Mudanca(TipoDeMudanca.Sai, $"Imagem em {canto.Address}", ali.Count == 1 ? ali[0].Name : $"{ali.Count} imagens", null);
        }

        string arquivo = ConferirImagem(caminho, i);
        var pic = ws.AddPicture(arquivo).MoveTo(canto);

        bool temLargura = op.TryGetProperty("width", out var wp);
        bool temAltura = op.TryGetProperty("height", out var hp);
        int Pixels(JsonElement e, string nome) =>
            e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int px) && px is >= 8 and <= 4000 ? px
                : throw new ErroDeDocumento($"operação {i}: '{nome}' da imagem é em pixels, de 8 a 4000.");

        if (temLargura && temAltura)
        {
            pic.WithSize(Pixels(wp, "width"), Pixels(hp, "height"));
        }
        else if (temLargura)
        {
            int w = Pixels(wp, "width");
            pic.WithSize(w, Math.Max(1, (int)Math.Round((double)pic.OriginalHeight * w / pic.OriginalWidth)));
        }
        else if (temAltura)
        {
            int h = Pixels(hp, "height");
            pic.WithSize(Math.Max(1, (int)Math.Round((double)pic.OriginalWidth * h / pic.OriginalHeight)), h);
        }

        return new Mudanca(TipoDeMudanca.Valor, $"Imagem em {canto.Address}", null,
            $"{Path.GetFileName(arquivo)} · {pic.Width}×{pic.Height} px");
    }

    /// <summary>O caminho da imagem, conferido: absoluto, existe, é imagem e não é grande demais.</summary>
    public static string ConferirImagem(string caminho, int i)
    {
        if (caminho.Length == 0 || !Path.IsPathFullyQualified(caminho))
            throw new ErroDeDocumento($"operação {i}: 'image' é o caminho absoluto do arquivo (C:\\...\\logo.png) ou 'none'.");
        if (!ExtensoesDeImagem.Contains(Path.GetExtension(caminho).ToLowerInvariant()))
            throw new ErroDeDocumento($"operação {i}: a imagem precisa ser {string.Join(", ", ExtensoesDeImagem)}.");
        var info = new FileInfo(caminho);
        if (!info.Exists) throw new ErroDeDocumento($"operação {i}: a imagem '{caminho}' não existe.");
        if (info.Length > TetoDeImagem)
            throw new ErroDeDocumento($"operação {i}: a imagem tem {info.Length / 1024 / 1024} MB; o teto é {TetoDeImagem / 1024 / 1024} MB.");
        return info.FullName;
    }

    /// <summary>Os caminhos de imagem de uma lista de ops, para o DocTool conferir onde ficam.</summary>
    public static IEnumerable<string> Imagens(JsonElement ops) =>
        ops.ValueKind != JsonValueKind.Array ? Enumerable.Empty<string>()
            : ops.EnumerateArray()
                .Where(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("image", out var v) && v.ValueKind == JsonValueKind.String)
                .Select(o => o.GetProperty("image").GetString()!.Trim())
                .Where(c => !c.Equals("none", StringComparison.OrdinalIgnoreCase));

    // ─────────────────────────────────────────────────────────────────────────
    // Auxiliares
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    private static string Pontos(IXLRow r) => r.Height.ToString("0.#", Br);

    private static string Texto(JsonElement v, string chave, int i) =>
        v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } t ? t.Trim()
            : throw new ErroDeDocumento($"operação {i}: '{chave}' é uma faixa como 'B1:G1'.");

    /// <summary>'5', '5:9', '5-9' ou o número 5.</summary>
    private static (int, int) Linhas(JsonElement v, int i)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int unica) && unica >= 1) return (unica, unica);

        string t = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var partes = t.Split(':', '-');
        if (partes.Length is 1 or 2 && int.TryParse(partes[0], out int de) && de >= 1)
        {
            int ate = partes.Length == 2 && int.TryParse(partes[1], out int a) && a >= 1 ? a : de;
            if (ate < de) (de, ate) = (ate, de);
            return (de, ate);
        }
        throw new ErroDeDocumento($"operação {i}: 'rows' é '1', '2:20' ou um número.");
    }

    private static IXLRange Faixa(IXLWorksheet ws, string faixa, int i)
    {
        var partes = faixa.Replace("$", "").Split(':');
        if (partes.Length == 2)
        {
            var (l1, c1) = Apresentacao.Ponto(partes[0], faixa);
            var (l2, c2) = Apresentacao.Ponto(partes[1], faixa);
            if (l1 is not null && c1 is not null && l2 is not null && c2 is not null)
                return ws.Range(l1.Value, c1.Value, l2.Value, c2.Value);
        }
        throw new ErroDeDocumento($"operação {i}: '{faixa}' não é uma faixa; use as duas pontas, como 'B1:G1'.");
    }
}
