using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ClosedXML.Excel;

namespace AIB.Services.Documentos;

/// <summary>
/// Interpreta formatações de células de planilha (estilo, cores, formatos de número e amostras).
/// </summary>
public static class EstiloDePlanilha
{
    private static readonly Dictionary<string, string> CoresPorNome = new(StringComparer.OrdinalIgnoreCase)
    {
        ["preto"] = "#000000",
        ["branco"] = "#FFFFFF",
        ["cinza"] = "#808080",
        ["cinza claro"] = "#D9D9D9",
        ["cinza escuro"] = "#595959",
        ["vermelho"] = "#C00000",
        ["vermelho claro"] = "#FFC7CE",
        ["vermelho escuro"] = "#9C0006",
        ["verde"] = "#00B050",
        ["verde claro"] = "#E2EFDA",
        ["verde escuro"] = "#375623",
        ["azul"] = "#4472C4",
        ["azul claro"] = "#DDEBF7",
        ["azul escuro"] = "#1F3864",
        ["amarelo"] = "#FFC000",
        ["amarelo claro"] = "#FFF2CC",
        ["laranja"] = "#ED7D31",
        ["laranja claro"] = "#FCE4D6",
        ["roxo"] = "#7030A0",
        ["roxo claro"] = "#E4DFEC",
        ["rosa"] = "#FFC0CB",
        ["marrom"] = "#804000"
    };

    private static readonly Dictionary<string, string> NomesPorCor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#000000"] = "preto",
        ["#FFFFFF"] = "branco",
        ["#808080"] = "cinza",
        ["#D9D9D9"] = "cinza claro",
        ["#595959"] = "cinza escuro",
        ["#C00000"] = "vermelho",
        ["#FF0000"] = "vermelho",
        ["#FFC7CE"] = "vermelho claro",
        ["#9C0006"] = "vermelho escuro",
        ["#00B050"] = "verde",
        ["#70AD47"] = "verde",
        ["#E2EFDA"] = "verde claro",
        ["#375623"] = "verde escuro",
        ["#4472C4"] = "azul",
        ["#0070C0"] = "azul",
        ["#DDEBF7"] = "azul claro",
        ["#1F3864"] = "azul escuro",
        ["#002060"] = "azul escuro",
        ["#FFC000"] = "amarelo",
        ["#FFFF00"] = "amarelo",
        ["#FFF2CC"] = "amarelo claro",
        ["#ED7D31"] = "laranja",
        ["#FCE4D6"] = "laranja claro",
        ["#7030A0"] = "roxo",
        ["#E4DFEC"] = "roxo claro",
        ["#FFC0CB"] = "rosa",
        ["#804000"] = "marrom",
        ["#993300"] = "marrom"
    };

    private static readonly HashSet<string> ChavesValidas = new(StringComparer.OrdinalIgnoreCase)
    {
        "bold", "italic", "underline", "color", "fill", "size", "font",
        "border", "align", "valign", "wrap", "number_format"
    };

    /// <summary>
    /// Converte um nome PT-BR ou string hexadecimal para "#RRGGBB".
    /// </summary>
    public static string ParaHex(string cor)
    {
        if (string.IsNullOrWhiteSpace(cor))
            throw new ErroDeDocumento("cor não informada.");

        string normal = SemAcento(cor.Trim());
        if (CoresPorNome.TryGetValue(normal, out var hex))
            return hex;

        string limpa = cor.Trim().TrimStart('#');
        if (limpa.Length == 6 && limpa.All(char.IsAsciiHexDigit))
            return "#" + limpa.ToUpperInvariant();

        throw new ErroDeDocumento($"cor \"{cor}\" não existe; use #RRGGBB ou: preto, branco, cinza, cinza claro, cinza escuro, " +
            "vermelho, vermelho claro, vermelho escuro, verde, verde claro, verde escuro, azul, azul claro, azul escuro, " +
            "amarelo, amarelo claro, laranja, laranja claro, roxo, roxo claro, rosa, marrom.");
    }

    /// <summary>
    /// Devolve o nome em PT-BR correspondente à cor ou o próprio hex em maiúsculas.
    /// </summary>
    public static string NomeDaCor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "";
        string h = hex.StartsWith('#') ? hex.ToUpperInvariant() : "#" + hex.ToUpperInvariant();
        return NomesPorCor.TryGetValue(h, out var nome) ? nome : h;
    }

    /// <summary>
    /// Extrai a cor em formato "#RRGGBB" de um XLColor, convertendo tema básico quando possível.
    /// </summary>
    public static string? HexDe(XLColor? xlColor)
    {
        if (xlColor == null || !xlColor.HasValue) return null;
        try
        {
            if (xlColor.ColorType == XLColorType.Color)
            {
                var c = xlColor.Color;
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            if (xlColor.ColorType == XLColorType.Theme)
            {
                return TemaParaHex(xlColor.ThemeColor, xlColor.ThemeTint);
            }
        }
        catch { }
        return null;
    }

    private static string? TemaParaHex(XLThemeColor tema, double tint)
    {
        // Paleta base Office 2013+ padrão
        string? baseHex = tema switch
        {
            XLThemeColor.Text1 => "#000000",
            XLThemeColor.Background1 => "#FFFFFF",
            XLThemeColor.Text2 => "#44546A",
            XLThemeColor.Background2 => "#E7E6E6",
            XLThemeColor.Accent1 => "#4472C4",
            XLThemeColor.Accent2 => "#ED7D31",
            XLThemeColor.Accent3 => "#A5A5A5",
            XLThemeColor.Accent4 => "#FFC000",
            XLThemeColor.Accent5 => "#5B9BD5",
            XLThemeColor.Accent6 => "#70AD47",
            _ => null
        };
        if (baseHex == null) return null;
        if (Math.Abs(tint) < 0.01) return baseHex;

        // Tonalidades padrão conhecidas
        if (tema == XLThemeColor.Accent4 && tint > 0.7) return "#FFF2CC";
        if (tema == XLThemeColor.Accent1 && tint > 0.7) return "#DDEBF7";
        if (tema == XLThemeColor.Accent6 && tint > 0.7) return "#E2EFDA";
        if (tema == XLThemeColor.Accent2 && tint > 0.7) return "#FCE4D6";
        if (tema == XLThemeColor.Accent1 && tint < -0.4) return "#1F3864";
        if (tema == XLThemeColor.Accent6 && tint < -0.4) return "#375623";

        return baseHex;
    }

    /// <summary>
    /// Converte atalhos em português para o formato numérico correspondente do Excel.
    /// </summary>
    public static string NormalizarFormato(string formato) => formato.Trim().ToLowerInvariant() switch
    {
        "moeda" => "R$ #,##0.00",
        "porcentagem" => "0.00%",
        "data" => "dd/mm/yyyy",
        "inteiro" => "#,##0",
        "decimal" => "#,##0.00",
        "texto" => "@",
        _ => formato
    };

    /// <summary>
    /// Descreve um formato numérico em palavras legíveis.
    /// </summary>
    public static string DescreverFormato(string? formato)
    {
        if (string.IsNullOrWhiteSpace(formato) || formato.Equals("General", StringComparison.OrdinalIgnoreCase))
            return "Geral";

        string f = formato.Trim();
        if (f.Contains("R$") || (f.Contains('$') && f.Contains("#,##0.00"))) return "moeda (R$ #,##0.00)";
        if (f.Equals("0.00%", StringComparison.OrdinalIgnoreCase)) return "porcentagem (0.00%)";
        if (f.Contains('%')) return "porcentagem (" + f + ")";
        if (f.Equals("dd/mm/yyyy", StringComparison.OrdinalIgnoreCase) || f.Equals("dd/MM/yyyy", StringComparison.OrdinalIgnoreCase))
            return "data (dd/mm/yyyy)";
        if (f.Equals("#,##0", StringComparison.OrdinalIgnoreCase)) return "inteiro (#,##0)";
        if (f.Equals("#,##0.00", StringComparison.OrdinalIgnoreCase)) return "decimal (#,##0.00)";
        if (f.Equals("@", StringComparison.OrdinalIgnoreCase)) return "texto (@)";

        return $"formato {f}";
    }

    /// <summary>
    /// Aplica as propriedades do objeto 'style' sobre um intervalo ou célula.
    /// </summary>
    public static void AplicarEstilo(IXLRangeBase alvo, JsonElement style, int opIndex)
    {
        if (style.ValueKind != JsonValueKind.Object)
            throw new ErroDeDocumento($"operação {opIndex}: 'style' precisa ser um objeto.");

        foreach (var prop in style.EnumerateObject())
        {
            if (!ChavesValidas.Contains(prop.Name))
                throw new ErroDeDocumento($"operação {opIndex}: chave de estilo \"{prop.Name}\" desconhecida. Use: {string.Join(", ", ChavesValidas)}.");
        }

        if (style.TryGetProperty("bold", out var b)) alvo.Style.Font.Bold = b.GetBoolean();
        if (style.TryGetProperty("italic", out var it)) alvo.Style.Font.Italic = it.GetBoolean();
        if (style.TryGetProperty("underline", out var u))
            alvo.Style.Font.Underline = u.GetBoolean() ? XLFontUnderlineValues.Single : XLFontUnderlineValues.None;

        if (style.TryGetProperty("color", out var corProp) && corProp.GetString() is { } corTexto)
            alvo.Style.Font.FontColor = XLColor.FromHtml(ParaHex(corTexto));

        if (style.TryGetProperty("fill", out var fillProp) && fillProp.GetString() is { } fillTexto)
            alvo.Style.Fill.SetBackgroundColor(XLColor.FromHtml(ParaHex(fillTexto)));

        if (style.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetDouble(out double size))
            alvo.Style.Font.FontSize = size;

        if (style.TryGetProperty("font", out var fontProp) && fontProp.GetString() is { } fontName)
            alvo.Style.Font.FontName = fontName;

        if (style.TryGetProperty("align", out var alignProp) && alignProp.GetString() is { } align)
        {
            alvo.Style.Alignment.Horizontal = align.ToLowerInvariant() switch
            {
                "left" => XLAlignmentHorizontalValues.Left,
                "center" => XLAlignmentHorizontalValues.Center,
                "right" => XLAlignmentHorizontalValues.Right,
                _ => throw new ErroDeDocumento($"operação {opIndex}: alinhamento horizontal inválido '{align}'. Use left, center ou right.")
            };
        }

        if (style.TryGetProperty("valign", out var valignProp) && valignProp.GetString() is { } valign)
        {
            alvo.Style.Alignment.Vertical = valign.ToLowerInvariant() switch
            {
                "top" => XLAlignmentVerticalValues.Top,
                "middle" or "center" => XLAlignmentVerticalValues.Center,
                "bottom" => XLAlignmentVerticalValues.Bottom,
                _ => throw new ErroDeDocumento($"operação {opIndex}: alinhamento vertical inválido '{valign}'. Use top, middle ou bottom.")
            };
        }

        if (style.TryGetProperty("wrap", out var wrapProp))
            alvo.Style.Alignment.WrapText = wrapProp.GetBoolean();

        if (style.TryGetProperty("number_format", out var nfProp) && nfProp.GetString() is { } nf)
            alvo.Style.NumberFormat.Format = NormalizarFormato(nf);

        if (style.TryGetProperty("border", out var borderProp))
            AplicarBordas(alvo, borderProp, opIndex);
    }

    private static void AplicarBordas(IXLRangeBase alvo, JsonElement borderProp, int opIndex)
    {
        var lista = new List<string>();
        if (borderProp.ValueKind == JsonValueKind.String)
        {
            if (borderProp.GetString() is { } s) lista.Add(s);
        }
        else if (borderProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in borderProp.EnumerateArray())
                if (elem.GetString() is { } s) lista.Add(s);
        }
        else
        {
            throw new ErroDeDocumento($"operação {opIndex}: 'border' precisa ser texto ou lista de textos.");
        }

        foreach (var b in lista)
        {
            switch (b.ToLowerInvariant())
            {
                case "thin" or "all":
                    alvo.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                    alvo.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                    alvo.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
                    alvo.Style.Border.RightBorder = XLBorderStyleValues.Thin;
                    alvo.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    break;
                case "thick":
                    alvo.Style.Border.OutsideBorder = XLBorderStyleValues.Thick;
                    break;
                case "double":
                    alvo.Style.Border.BottomBorder = XLBorderStyleValues.Double;
                    break;
                case "top":
                    alvo.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                    break;
                case "bottom":
                    alvo.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                    break;
                case "left":
                    alvo.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
                    break;
                case "right":
                    alvo.Style.Border.RightBorder = XLBorderStyleValues.Thin;
                    break;
                case "outline":
                    alvo.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    break;
                case "none":
                    alvo.Style.Border.TopBorder = XLBorderStyleValues.None;
                    alvo.Style.Border.BottomBorder = XLBorderStyleValues.None;
                    alvo.Style.Border.LeftBorder = XLBorderStyleValues.None;
                    alvo.Style.Border.RightBorder = XLBorderStyleValues.None;
                    alvo.Style.Border.InsideBorder = XLBorderStyleValues.None;
                    break;
                default:
                    throw new ErroDeDocumento($"operação {opIndex}: borda inválida '{b}'. Use thin, thick, double, top, bottom, left, right, outline, all ou none.");
            }
        }
    }

    /// <summary>
    /// Obtém o registro de Aparência visual da célula para a amostra do cartão.
    /// </summary>
    public static Aparencia ObterAparencia(IXLCell c)
    {
        string texto = EditorDePlanilha.Mostrar(c) ?? "";
        if (c.HasFormula)
        {
            // Na amostra da célula o valor final desenhado não leva o prefixo da fórmula
            try { texto = c.CachedValue.IsBlank ? "" : LeitorDePlanilha.Texto(c); }
            catch { texto = ""; }
        }

        bool negrito = c.Style.Font.Bold;
        bool italico = c.Style.Font.Italic;
        bool sublinhado = c.Style.Font.Underline != XLFontUnderlineValues.None;

        string? cor = HexDe(c.Style.Font.FontColor);
        if (cor == "#000000") cor = null;

        string? fundo = c.Style.Fill.PatternType == XLFillPatternValues.None ? null : HexDe(c.Style.Fill.BackgroundColor);

        string alinhamento = c.Style.Alignment.Horizontal switch
        {
            XLAlignmentHorizontalValues.Center => "center",
            XLAlignmentHorizontalValues.Right => "right",
            _ => "left"
        };

        Bordas bordas = Bordas.Nenhuma;
        if (c.Style.Border.TopBorder != XLBorderStyleValues.None) bordas |= Bordas.Cima;
        if (c.Style.Border.BottomBorder == XLBorderStyleValues.Double) bordas |= Bordas.BaixoDupla;
        else if (c.Style.Border.BottomBorder != XLBorderStyleValues.None) bordas |= Bordas.Baixo;
        if (c.Style.Border.LeftBorder != XLBorderStyleValues.None) bordas |= Bordas.Esquerda;
        if (c.Style.Border.RightBorder != XLBorderStyleValues.None) bordas |= Bordas.Direita;

        return new Aparencia(texto, negrito, italico, sublinhado, cor, fundo, alinhamento, bordas);
    }

    /// <summary>
    /// Descreve em palavras o estilo de uma célula, detalhando apenas o que difere do padrão.
    /// </summary>
    public static string DescreverEstilo(IXLCell c, JsonElement? styleSolicitado = null)
    {
        var partes = new List<string>();

        if (c.Style.Font.Bold) partes.Add("negrito");
        if (c.Style.Font.Italic) partes.Add("itálico");
        if (c.Style.Font.Underline != XLFontUnderlineValues.None) partes.Add("sublinhado");

        if (c.Style.Font.FontName is { Length: > 0 } fn && !fn.Equals("Calibri", StringComparison.OrdinalIgnoreCase))
            partes.Add($"fonte {fn}");

        if (c.Style.Font.FontSize > 0 && Math.Abs(c.Style.Font.FontSize - 11) > 0.1)
            partes.Add($"tamanho {c.Style.Font.FontSize.ToString(CultureInfo.InvariantCulture)}");

        string? cor = HexDe(c.Style.Font.FontColor);
        if (cor != null && cor != "#000000")
            partes.Add($"texto {NomeDaCor(cor)}");

        if (c.Style.Fill.PatternType != XLFillPatternValues.None)
        {
            string? fundo = HexDe(c.Style.Fill.BackgroundColor);
            if (fundo != null) partes.Add($"fundo {NomeDaCor(fundo)}");
        }

        if (c.Style.Alignment.Horizontal == XLAlignmentHorizontalValues.Center) partes.Add("centralizado");
        else if (c.Style.Alignment.Horizontal == XLAlignmentHorizontalValues.Right) partes.Add("alinhado à direita");
        else if (c.Style.Alignment.Horizontal == XLAlignmentHorizontalValues.Left && styleSolicitado?.TryGetProperty("align", out _) == true)
            partes.Add("alinhado à esquerda");

        if (c.Style.Alignment.Vertical == XLAlignmentVerticalValues.Top) partes.Add("alinhado ao topo");
        else if (c.Style.Alignment.Vertical == XLAlignmentVerticalValues.Center) partes.Add("alinhado ao meio");

        if (c.Style.Alignment.WrapText) partes.Add("quebra de texto");

        DescreverBordas(c, partes);

        // Formato de número: só menciona se o style tratou de número ou se difere de Geral
        bool tocouNumero = styleSolicitado?.TryGetProperty("number_format", out _) == true;
        string formato = c.Style.NumberFormat.Format;
        bool naoGeral = !string.IsNullOrWhiteSpace(formato) && !formato.Equals("General", StringComparison.OrdinalIgnoreCase);

        if (tocouNumero)
        {
            partes.Add(DescreverFormato(formato));
        }
        else if (naoGeral)
        {
            partes.Add(DescreverFormato(formato));
        }

        return partes.Count == 0 ? "normal" : string.Join(" · ", partes);
    }

    private static void DescreverBordas(IXLCell c, List<string> partes)
    {
        bool cima = c.Style.Border.TopBorder != XLBorderStyleValues.None;
        bool baixo = c.Style.Border.BottomBorder != XLBorderStyleValues.None;
        bool baixoDupla = c.Style.Border.BottomBorder == XLBorderStyleValues.Double;
        bool esq = c.Style.Border.LeftBorder != XLBorderStyleValues.None;
        bool dir = c.Style.Border.RightBorder != XLBorderStyleValues.None;

        if (cima && baixo && esq && dir)
        {
            if (baixoDupla) partes.Add("todas as bordas e borda dupla embaixo");
            else partes.Add("todas as bordas");
            return;
        }

        if (baixoDupla) partes.Add("borda dupla embaixo");
        else if (baixo) partes.Add("borda embaixo");

        if (cima) partes.Add("borda em cima");
        if (esq) partes.Add("borda à esquerda");
        if (dir) partes.Add("borda à direita");
    }

    private static string SemAcento(string s)
    {
        string normalizada = s.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalizada.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
        return new string(chars).Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }
}
