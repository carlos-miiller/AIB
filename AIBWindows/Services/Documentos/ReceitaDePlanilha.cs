using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace AIB.Services.Documentos;

/// <summary>
/// A formatação de uma aba escrita como as <c>ops</c> do edit (<c>action='layout'</c>).
/// <para>
/// O <c>read</c> mostra valores; para aprender com uma planilha bem feita, a Ellen precisa ver
/// COMO ela é feita: larguras, alturas, mesclas, cores, bordas, listas, impressão. Em 26/10/2026 o
/// usuário deu um modelo de rubrica como exemplo do que queria; esta é a forma de ela ler esse
/// modelo — e qualquer outro que ele puser em ~/.AIB/modelos — na língua que ela mesma escreve.
/// </para>
/// <para>
/// O que tem op vira op (e roda, com o mesmo resultado, num arquivo novo); o que não tem
/// (condicional fora do vocabulário, imagem, cor com tonalidade exata) vai numa lista à parte.
/// </para>
/// </summary>
public static class ReceitaDePlanilha
{
    /// <summary>Linhas e colunas lidas no máximo, sem 'range'.</summary>
    public const int TetoDeLinhas = 200;
    public const int TetoDeColunas = 40;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Descrever(string caminho, string? aba, string? faixa, int teto)
    {
        if (Formatos.De(caminho) != Formato.Xlsx)
            throw new ErroDeDocumento("'layout' lê .xlsx e .xlsm. Para .xls ou .csv não há formatação a mostrar.");

        using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var wb = new XLWorkbook(fs);
        var ws = string.IsNullOrWhiteSpace(aba)
            ? wb.Worksheets.FirstOrDefault(w => w.Visibility == XLWorksheetVisibility.Visible) ?? wb.Worksheet(1)
            : wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, aba.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new ErroDeDocumento($"não há aba \"{aba}\". Abas: {string.Join(", ", wb.Worksheets.Select(w => $"\"{w.Name}\""))}.");

        var area = Area(ws, faixa);
        var ops = new List<string>();
        var fora = new List<string>();

        if (area == null)
            return $"A aba \"{ws.Name}\" está vazia: nada para mostrar.";
        var (l1, c1, l2, c2, cortada) = area.Value;

        // A aba
        if (!ws.ShowGridLines) ops.Add(Op(new { gridlines = false }));
        Larguras(ws, c1, c2, ops);
        Alturas(ws, l1, l2, ops);

        // A fonte que domina a aba vai numa op só; as outras ops só dizem o que difere dela.
        var fonte = Fonte(ws, l1, c1, l2, c2);
        if (fonte != ("Calibri", 11.0))
            ops.Add(Op(new { range = Endereco(l1, c1, l2, c2), style = new { font = fonte.Nome, size = fonte.Tamanho } }));

        // Estilo por retângulos de células iguais, depois os valores
        Estilos(ws, l1, c1, l2, c2, fonte, ops);
        Valores(ws, l1, c1, l2, c2, ops);

        foreach (var m in ws.MergedRanges.Where(m => Dentro(m.RangeAddress, l1, c1, l2, c2)))
            ops.Add(Op(new { merge = Endereco(m.RangeAddress) }));

        Listas(ws, l1, c1, l2, c2, ops);
        Links(ws, l1, c1, l2, c2, ops);

        foreach (var cf in ws.ConditionalFormats)
            fora.Add($"condicional em {string.Join(" ", cf.Ranges.Select(r => Endereco(r.RangeAddress)))}: {CondicionalDePlanilha.DescreverRegra(cf)} (refaça com 'conditional', topic='condicional')");

        foreach (var p in ws.Pictures)
            fora.Add($"imagem \"{p.Name}\" em {p.TopLeftCell.Address}, {p.Width}×{p.Height} px (refaça com {{image:'C:\\\\...\\\\arquivo.png', cell:'{p.TopLeftCell.Address}', width:{p.Width}}})");

        if (ws.SheetView.SplitRow > 0 || ws.SheetView.SplitColumn > 0)
            ops.Add(Op(new { freeze = $"{Grade.Letra(ws.SheetView.SplitColumn + 1)}{ws.SheetView.SplitRow + 1}" }));
        if (ws.AutoFilter.IsEnabled)
            ops.Add(Op(new { filter = Endereco(ws.AutoFilter.Range.RangeAddress) }));
        if (Impressao(ws) is { } impressao) ops.Add(impressao);

        // O texto
        var sb = new StringBuilder();
        sb.Append("Formatação da aba \"").Append(ws.Name).Append("\" de ").Append(Path.GetFileName(caminho));
        sb.Append(", ").Append(Endereco(l1, c1, l2, c2)).AppendLine(", escrita como as ops de action='edit'.");
        sb.AppendLine("Copie o que servir (num arquivo novo: crie com 'create' e aplique com 'edit'). Cores em #RRGGBB.");
        if (wb.Worksheets.Count > 1)
            sb.Append("Outras abas: ").AppendLine(string.Join(", ", wb.Worksheets.Where(w => w != ws).Select(w => $"\"{w.Name}\"")) + " (use 'sheet').");
        if (cortada)
            sb.AppendLine($"Mostrando até a linha {l2} e a coluna {Grade.Letra(c2)}; para o resto, peça com range (ex.: '{l2 + 1}:{l2 + TetoDeLinhas}').");
        sb.AppendLine();
        sb.AppendLine("[");

        int usados = sb.Length;
        for (int i = 0; i < ops.Count; i++)
        {
            string linha = " " + ops[i] + (i < ops.Count - 1 ? "," : "");
            if (usados + linha.Length > teto - 300)
            {
                sb.AppendLine($" ]\n(PAROU AQUI: faltam {ops.Count - i} ops. Peça um trecho menor com range, ex.: '{l1}:{l1 + (l2 - l1) / 2}'.)");
                return sb.ToString().TrimEnd();
            }
            sb.AppendLine(linha);
            usados += linha.Length;
        }
        sb.AppendLine("]");

        if (fora.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Também há (sem op direta):");
            foreach (var f in fora) sb.Append("- ").AppendLine(f);
        }
        return sb.ToString().TrimEnd();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // A área
    // ─────────────────────────────────────────────────────────────────────────

    private static (int L1, int C1, int L2, int C2, bool Cortada)? Area(IXLWorksheet ws, string? faixa)
    {
        // Inclui as células só formatadas: o fundo de um bloco vazio também é desenho.
        var usada = ws.RangeUsed(XLCellsUsedOptions.All);
        if (usada == null) return null;

        int l1 = usada.FirstRow().RowNumber(), l2 = usada.LastRow().RowNumber();
        int c1 = 1, c2 = usada.LastColumn().ColumnNumber();

        if (!string.IsNullOrWhiteSpace(faixa))
        {
            var partes = faixa.Replace("$", "").Split(':', '-');
            if (partes.Length == 2 && int.TryParse(partes[0], out int a) && int.TryParse(partes[1], out int b) && a >= 1 && b >= a)
            {
                (l1, l2) = (a, b);
            }
            else if (partes.Length == 2 && Apresentacao.Ponto(partes[0], faixa) is { } p && Apresentacao.Ponto(partes[1], faixa) is { } q
                     && p.Item1 is int pl && p.Item2 is int pc && q.Item1 is int ql && q.Item2 is int qc)
            {
                (l1, c1, l2, c2) = (Math.Min(pl, ql), Math.Min(pc, qc), Math.Max(pl, ql), Math.Max(pc, qc));
            }
            else
            {
                throw new ErroDeDocumento($"'range' do layout é '1:40' (linhas) ou 'A1:H40'.");
            }
        }

        bool cortada = false;
        if (l2 - l1 + 1 > TetoDeLinhas) { l2 = l1 + TetoDeLinhas - 1; cortada = true; }
        if (c2 - c1 + 1 > TetoDeColunas) { c2 = c1 + TetoDeColunas - 1; cortada = true; }
        return (l1, c1, l2, c2, cortada);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Linhas e colunas
    // ─────────────────────────────────────────────────────────────────────────

    private static void Larguras(IXLWorksheet ws, int c1, int c2, List<string> ops)
    {
        double padrao = ws.ColumnWidth;
        int inicio = c1;
        while (inicio <= c2)
        {
            double w = Math.Round(ws.Column(inicio).Width, 2);
            int fim = inicio;
            while (fim + 1 <= c2 && Math.Abs(Math.Round(ws.Column(fim + 1).Width, 2) - w) < 0.01) fim++;
            if (Math.Abs(w - padrao) > 0.01)
                ops.Add(Op(new { columns = inicio == fim ? Grade.Letra(inicio) : $"{Grade.Letra(inicio)}:{Grade.Letra(fim)}", width = w }));
            inicio = fim + 1;
        }
    }

    private static void Alturas(IXLWorksheet ws, int l1, int l2, List<string> ops)
    {
        double padrao = ws.RowHeight;
        int inicio = l1;
        while (inicio <= l2)
        {
            double h = Math.Round(ws.Row(inicio).Height, 2);
            int fim = inicio;
            while (fim + 1 <= l2 && Math.Abs(Math.Round(ws.Row(fim + 1).Height, 2) - h) < 0.01) fim++;
            if (Math.Abs(h - padrao) > 0.01)
                ops.Add(Op(new { rows = inicio == fim ? inicio.ToString(Inv) : $"{inicio}:{fim}", height = h }));
            inicio = fim + 1;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Estilo
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Agrupa as células de estilo igual: primeiro em trechos na linha, depois empilha os trechos
    /// iguais das linhas seguintes. Uma tabela de 20 linhas com o mesmo corpo vira uma op só.
    /// </summary>
    private static void Estilos(IXLWorksheet ws, int l1, int c1, int l2, int c2, (string Nome, double Tamanho) fonte, List<string> ops)
    {
        var abertos = new Dictionary<(int C1, int C2, string Chave), (int L1, int L2)>();
        var prontos = new List<(int L1, int C1, int L2, int C2, string Chave)>();

        for (int l = l1; l <= l2; l++)
        {
            var trechos = new List<(int, int, string)>();
            int c = c1;
            while (c <= c2)
            {
                string chave = Chave(ws.Cell(l, c), fonte);
                int fim = c;
                while (fim + 1 <= c2 && Chave(ws.Cell(l, fim + 1), fonte) == chave) fim++;
                if (chave.Length > 0) trechos.Add((c, fim, chave));
                c = fim + 1;
            }

            var continuam = new HashSet<(int, int, string)>(trechos);
            foreach (var k in abertos.Keys.ToList())
            {
                if (continuam.Contains(k)) { abertos[k] = (abertos[k].L1, l); continuam.Remove(k); }
                else { prontos.Add((abertos[k].L1, k.C1, abertos[k].L2, k.C2, k.Chave)); abertos.Remove(k); }
            }
            foreach (var t in continuam) abertos[t] = (l, l);
        }
        foreach (var (k, v) in abertos) prontos.Add((v.L1, k.C1, v.L2, k.C2, k.Chave));

        foreach (var p in prontos.OrderBy(p => p.L1).ThenBy(p => p.C1))
        {
            string onde = Endereco(p.L1, p.C1, p.L2, p.C2);
            ops.Add(p.L1 == p.L2 && p.C1 == p.C2
                ? $"{{\"cell\":\"{onde}\",\"style\":{p.Chave}}}"
                : $"{{\"range\":\"{onde}\",\"style\":{p.Chave}}}");
        }
    }

    /// <summary>
    /// O 'style' da célula em JSON, só com o que difere de uma célula nova com a fonte da aba;
    /// vazio se nada. Célula vazia só conta com borda ou fundo de cor: fonte e alinhamento numa
    /// célula sem nada não se veem, e o fundo branco numa aba sem grade também não (a rubrica de
    /// exemplo pinta de branco até a coluna O, e isso virava dezenas de ops sem efeito).
    /// </summary>
    private static string Chave(IXLCell c, (string Nome, double Tamanho) fonte)
    {
        var s = c.Style;
        var d = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var wb = c.Worksheet.Workbook;

        bool vazia = !c.HasFormula && (c.Value.IsBlank || (c.Value.IsText && c.GetString().Length == 0));
        if (vazia)
        {
            bool temBorda = Bordas(s.Border) != null;
            string? fundoVazio = s.Fill.PatternType != XLFillPatternValues.None ? Cor(s.Fill.BackgroundColor, wb) : null;
            if (!temBorda && (fundoVazio == null || fundoVazio == "#FFFFFF")) return "";
        }

        if (s.Font.Bold) d["bold"] = true;
        if (s.Font.Italic) d["italic"] = true;
        if (s.Font.Underline != XLFontUnderlineValues.None) d["underline"] = true;
        if (!string.Equals(s.Font.FontName, fonte.Nome, StringComparison.OrdinalIgnoreCase)) d["font"] = s.Font.FontName;
        if (Math.Abs(s.Font.FontSize - fonte.Tamanho) > 0.01) d["size"] = s.Font.FontSize;
        if (Cor(s.Font.FontColor, wb) is { } cor && cor != "#000000") d["color"] = cor;
        if (s.Fill.PatternType != XLFillPatternValues.None && Cor(s.Fill.BackgroundColor, wb) is { } fundo) d["fill"] = fundo;

        if (s.Alignment.Horizontal is XLAlignmentHorizontalValues.Left or XLAlignmentHorizontalValues.Center or XLAlignmentHorizontalValues.Right)
            d["align"] = s.Alignment.Horizontal.ToString().ToLowerInvariant();
        if (s.Alignment.Vertical == XLAlignmentVerticalValues.Center) d["valign"] = "middle";
        else if (s.Alignment.Vertical == XLAlignmentVerticalValues.Top) d["valign"] = "top";
        if (s.Alignment.WrapText) d["wrap"] = true;
        if (s.Alignment.Indent > 0) d["indent"] = s.Alignment.Indent;

        if (FormatoDeNumero(s.NumberFormat) is { } nf) d["number_format"] = nf;
        if (Bordas(s.Border) is { } b) d["border"] = b;

        return d.Count == 0 ? "" : JsonSerializer.Serialize(d, Json);
    }

    private static object? Bordas(IXLBorder b)
    {
        var lados = new List<string>();
        if (b.TopBorder != XLBorderStyleValues.None) lados.Add("top");
        if (b.BottomBorder == XLBorderStyleValues.Double) lados.Add("double");
        else if (b.BottomBorder != XLBorderStyleValues.None) lados.Add("bottom");
        if (b.LeftBorder != XLBorderStyleValues.None) lados.Add("left");
        if (b.RightBorder != XLBorderStyleValues.None) lados.Add("right");

        if (lados.Count == 0) return null;
        if (lados.Count == 4 && !lados.Contains("double")) return "all";
        return lados.Count == 1 ? lados[0] : lados;
    }

    /// <summary>A fonte e o tamanho mais comuns nas células com conteúdo.</summary>
    private static (string Nome, double Tamanho) Fonte(IXLWorksheet ws, int l1, int c1, int l2, int c2)
    {
        var mais = ws.Range(l1, c1, l2, c2).CellsUsed(XLCellsUsedOptions.Contents)
            .GroupBy(c => (c.Style.Font.FontName, c.Style.Font.FontSize))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        return mais == null ? ("Calibri", 11.0) : (mais.Key.FontName, mais.Key.FontSize);
    }

    private static string? FormatoDeNumero(IXLNumberFormat nf)
    {
        if (!string.IsNullOrEmpty(nf.Format) && !nf.Format.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            var atalho = EstiloDePlanilha.AtalhosDeFormato.FirstOrDefault(a => a.Value == nf.Format).Key;
            return atalho ?? nf.Format;
        }
        return nf.NumberFormatId switch
        {
            1 => "0",
            2 => "0.00",
            3 => "inteiro",
            4 => "decimal",
            9 => "0%",
            10 => "porcentagem",
            14 => "data",
            22 => "dd/mm/yyyy hh:mm",
            49 => "texto",
            _ => null
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cores
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A cor em #RRGGBB. Cor de tema vem da paleta do PRÓPRIO arquivo, com a tonalidade aplicada
    /// como o Excel faz (na luminosidade): a rubrica de exemplo é toda em "tema 3, 49% mais
    /// escuro", e a paleta padrão sem tonalidade dava um cinza que não era o dela.
    /// </summary>
    public static string? Cor(XLColor? cor, XLWorkbook wb)
    {
        if (cor == null || !cor.HasValue) return null;
        try
        {
            if (cor.ColorType == XLColorType.Color)
            {
                var c = cor.Color;
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            if (cor.ColorType == XLColorType.Theme)
            {
                var t = wb.Theme;
                XLColor? baseCor = cor.ThemeColor switch
                {
                    XLThemeColor.Background1 => t.Background1,
                    XLThemeColor.Text1 => t.Text1,
                    XLThemeColor.Background2 => t.Background2,
                    XLThemeColor.Text2 => t.Text2,
                    XLThemeColor.Accent1 => t.Accent1,
                    XLThemeColor.Accent2 => t.Accent2,
                    XLThemeColor.Accent3 => t.Accent3,
                    XLThemeColor.Accent4 => t.Accent4,
                    XLThemeColor.Accent5 => t.Accent5,
                    XLThemeColor.Accent6 => t.Accent6,
                    XLThemeColor.Hyperlink => t.Hyperlink,
                    _ => null
                };
                if (baseCor == null || baseCor.ColorType != XLColorType.Color) return null;
                var b = baseCor.Color;
                return Tonalidade(b.R, b.G, b.B, cor.ThemeTint);
            }
            if (cor.ColorType == XLColorType.Indexed && cor.Indexed == 64) return null; // automática
            var ix = cor.Color;
            return $"#{ix.R:X2}{ix.G:X2}{ix.B:X2}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A tonalidade do Excel: escurece ou clareia a luminosidade (HSL).</summary>
    public static string Tonalidade(byte r, byte g, byte b, double tint)
    {
        if (Math.Abs(tint) < 0.0001) return $"#{r:X2}{g:X2}{b:X2}";

        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double h = 0, s = 0, l = (max + min) / 2;
        if (max > min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == rf ? (gf - bf) / d + (gf < bf ? 6 : 0) : max == gf ? (bf - rf) / d + 2 : (rf - gf) / d + 4;
            h /= 6;
        }

        l = tint < 0 ? l * (1 + tint) : l * (1 - tint) + tint;

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        byte Canal(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            double v = t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
            return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        }
        byte nr = s == 0 ? (byte)Math.Round(l * 255) : Canal(h + 1.0 / 3);
        byte ng = s == 0 ? nr : Canal(h);
        byte nb = s == 0 ? nr : Canal(h - 1.0 / 3);
        return $"#{nr:X2}{ng:X2}{nb:X2}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Valores, listas, links, impressão
    // ─────────────────────────────────────────────────────────────────────────

    private static void Valores(IXLWorksheet ws, int l1, int c1, int l2, int c2, List<string> ops)
    {
        foreach (var c in ws.Range(l1, c1, l2, c2).CellsUsed(XLCellsUsedOptions.Contents))
        {
            object? valor;
            if (c.HasFormula) valor = "=" + c.FormulaA1;
            else if (c.Value.IsNumber) valor = c.Value.GetNumber();
            else if (c.Value.IsBoolean) valor = c.Value.GetBoolean();
            else if (c.Value.IsDateTime)
                valor = c.Value.GetDateTime().TimeOfDay == TimeSpan.Zero
                    ? c.Value.GetDateTime().ToString("yyyy-MM-dd", Inv)
                    : c.Value.GetDateTime().ToString("yyyy-MM-ddTHH:mm", Inv);
            else if (c.Value.IsText && c.GetString().Length > 0) valor = c.GetString();
            else continue;
            ops.Add(Op(new { cell = c.Address.ToString(), value = valor }));
        }
    }

    private static void Listas(IXLWorksheet ws, int l1, int c1, int l2, int c2, List<string> ops)
    {
        foreach (var dv in ws.DataValidations)
        {
            if (dv.AllowedValues != XLAllowedValues.List || string.IsNullOrWhiteSpace(dv.Value)) continue;
            object lista = dv.Value.StartsWith('"')
                ? dv.Value.Trim('"').Split(',')
                : "=" + dv.Value.TrimStart('=');
            foreach (var r in dv.Ranges.Where(r => Dentro(r.RangeAddress, l1, c1, l2, c2)))
                ops.Add(Op(new { range = Faixa(r.RangeAddress), validation = new { list = lista } }));
        }
    }

    private static void Links(IXLWorksheet ws, int l1, int c1, int l2, int c2, List<string> ops)
    {
        foreach (var c in ws.Range(l1, c1, l2, c2).Cells(x => x.HasHyperlink))
        {
            // Numa faixa mesclada só a primeira célula aparece; o link das outras é repetição.
            if (c.IsMerged() && !c.MergedRange().FirstCell().Address.Equals(c.Address)) continue;
            var h = c.GetHyperlink();
            string alvo = h.IsExternal ? h.ExternalAddress.ToString() : "#" + h.InternalAddress;
            ops.Add(Op(new { cell = c.Address.ToString(), link = alvo }));
        }
    }

    private static string? Impressao(IXLWorksheet ws)
    {
        var ps = ws.PageSetup;
        var d = new SortedDictionary<string, object>(StringComparer.Ordinal);
        if (ps.PageOrientation == XLPageOrientation.Landscape) d["orientation"] = "landscape";
        if (ps.PagesWide == 1 && ps.PagesTall == 0) d["fit"] = "width";
        else if (ps.PagesWide == 1 && ps.PagesTall == 1) d["fit"] = "page";
        if (ps.PrintAreas.FirstOrDefault() is { } area) d["area"] = Faixa(area.RangeAddress);
        if (ps.FirstRowToRepeatAtTop > 0)
            d["repeat_rows"] = ps.FirstRowToRepeatAtTop == ps.LastRowToRepeatAtTop
                ? ps.FirstRowToRepeatAtTop.ToString(Inv) : $"{ps.FirstRowToRepeatAtTop}:{ps.LastRowToRepeatAtTop}";
        if (ps.CenterHorizontally) d["center"] = true;
        if (ps.Margins.Left < 0.5) d["margins"] = "estreita";
        else if (ps.Margins.Left > 0.9) d["margins"] = "larga";
        return d.Count == 0 ? null : JsonSerializer.Serialize(new Dictionary<string, object> { ["print"] = d }, Json);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Auxiliares
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Op(object o) => JsonSerializer.Serialize(o, Json);

    private static string Endereco(int l1, int c1, int l2, int c2) =>
        l1 == l2 && c1 == c2 ? $"{Grade.Letra(c1)}{l1}" : $"{Grade.Letra(c1)}{l1}:{Grade.Letra(c2)}{l2}";

    private static string Endereco(IXLRangeAddress a) =>
        Endereco(a.FirstAddress.RowNumber, a.FirstAddress.ColumnNumber, a.LastAddress.RowNumber, a.LastAddress.ColumnNumber);

    /// <summary>Sempre com as duas pontas: 'range' do edit não aceita célula sozinha.</summary>
    private static string Faixa(IXLRangeAddress a) =>
        $"{Grade.Letra(a.FirstAddress.ColumnNumber)}{a.FirstAddress.RowNumber}:{Grade.Letra(a.LastAddress.ColumnNumber)}{a.LastAddress.RowNumber}";

    private static bool Dentro(IXLRangeAddress a, int l1, int c1, int l2, int c2) =>
        a.FirstAddress.RowNumber <= l2 && a.LastAddress.RowNumber >= l1 &&
        a.FirstAddress.ColumnNumber <= c2 && a.LastAddress.ColumnNumber >= c1;
}
