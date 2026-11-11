using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ClosedXML.Excel;

namespace AIB.Services.Documentos;

/// <summary>
/// Cria e manipula regras de formatação condicional (quando, escalas, barras de dados e ícones).
/// </summary>
public static class CondicionalDePlanilha
{
    private static readonly Dictionary<string, string[]> EscalasDeCores = new(StringComparer.OrdinalIgnoreCase)
    {
        ["verde-vermelho"] = new[] { "#63BE7B", "#F8696B" },
        ["vermelho-verde"] = new[] { "#F8696B", "#63BE7B" },
        ["verde-amarelo-vermelho"] = new[] { "#63BE7B", "#FFEB84", "#F8696B" },
        ["vermelho-amarelo-verde"] = new[] { "#F8696B", "#FFEB84", "#63BE7B" },
        ["branco-azul"] = new[] { "#FFFFFF", "#5A8AC6" }
    };

    private static readonly Dictionary<string, (XLIconSetStyle Estilo, string Simbolos)> ConjuntosDeIcones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["setas"] = (XLIconSetStyle.ThreeArrows, "▲ ► ▼"),
        ["semaforo"] = (XLIconSetStyle.ThreeTrafficLights1, "● ● ●"),
        ["sinais"] = (XLIconSetStyle.ThreeSigns, "● ● ●"),
        ["estrelas"] = (XLIconSetStyle.ThreeSymbols, "● ● ●")
    };

    public static IEnumerable<string> Escalas => EscalasDeCores.Keys;
    public static IEnumerable<string> Icones => ConjuntosDeIcones.Keys;

    /// <summary>
    /// Processa a operação 'conditional' sobre a faixa informada, registrando a regra no ClosedXML
    /// e devolvendo a mudança correspondente para o plano.
    /// <para>
    /// A regra nova substitui a que o ARQUIVO já tinha na mesma faixa, nunca uma criada no mesmo
    /// pedido (<paramref name="criadas"/>). Visto em 26/10/2026: a Ellen mandou "verde se &lt; 0" e
    /// "vermelho se &gt; 0" em D15:D18 num edit só, e a segunda apagou a primeira; ela precisou de
    /// outro pedido, com clear_conditional, para ter as duas.
    /// </para>
    /// </summary>
    public static (Mudanca Mudanca, IXLConditionalFormat Regra) Processar(IXLWorksheet ws, IXLRange faixa, JsonElement cond, int opIndex,
        IReadOnlySet<IXLConditionalFormat>? criadas = null)
    {
        if (cond.ValueKind != JsonValueKind.Object)
            throw new ErroDeDocumento($"operação {opIndex}: 'conditional' precisa ser um objeto.");

        // Substituição: se já havia regra exatamente nesta faixa, aproveitamos a descrição anterior
        string faixaTexto = faixa.RangeAddress.ToString() ?? "";
        var existente = ws.ConditionalFormats.FirstOrDefault(cf =>
            cf.Range != null && criadas?.Contains(cf) != true
            && string.Equals(cf.Range.RangeAddress.ToString(), faixaTexto, StringComparison.OrdinalIgnoreCase));
        string antes = existente != null ? DescreverRegra(existente) : "sem regra";
        if (existente != null)
            ws.ConditionalFormats.Remove(x => ReferenceEquals(x, existente));

        IXLConditionalFormat novaRegra = faixa.AddConditionalFormat();

        if (cond.TryGetProperty("when", out var whenProp))
        {
            return ProcessarWhen(faixa, cond, whenProp.GetString() ?? "", antes, novaRegra, opIndex);
        }
        if (cond.TryGetProperty("scale", out var scaleProp))
        {
            return ProcessarScale(faixa, scaleProp.GetString() ?? "", antes, novaRegra, opIndex);
        }
        if (cond.TryGetProperty("bar", out var barProp))
        {
            return ProcessarBar(faixa, barProp.GetString() ?? "", antes, novaRegra, opIndex);
        }
        if (cond.TryGetProperty("icons", out var iconsProp))
        {
            return ProcessarIcons(faixa, iconsProp.GetString() ?? "", antes, novaRegra, opIndex);
        }

        throw new ErroDeDocumento($"operação {opIndex}: 'conditional' precisa de 'when', 'scale', 'bar' ou 'icons'.");
    }

    private static (Mudanca, IXLConditionalFormat) ProcessarWhen(
        IXLRange faixa, JsonElement cond, string when, string antes, IXLConditionalFormat cf, int opIndex)
    {
        string opNorm = when.Trim().ToLowerInvariant();
        object? val1 = cond.TryGetProperty("value", out var v1) ? DoJson(v1) : null;
        object? val2 = cond.TryGetProperty("value2", out var v2) ? DoJson(v2) : null;

        if (opNorm != "duplicates" && opNorm != "unique" && val1 == null)
            throw new ErroDeDocumento($"operação {opIndex}: 'when' precisa de 'value'.");
        if (opNorm == "between" && val2 == null)
            throw new ErroDeDocumento($"operação {opIndex}: 'between' precisa de 'value' e 'value2'.");

        IXLStyle styleAlvo = opNorm switch
        {
            ">" => val1 is double d ? cf.WhenGreaterThan(d) : cf.WhenGreaterThan(val1?.ToString() ?? ""),
            "<" => val1 is double d ? cf.WhenLessThan(d) : cf.WhenLessThan(val1?.ToString() ?? ""),
            ">=" => val1 is double d ? cf.WhenEqualOrGreaterThan(d) : cf.WhenEqualOrGreaterThan(val1?.ToString() ?? ""),
            "<=" => val1 is double d ? cf.WhenEqualOrLessThan(d) : cf.WhenEqualOrLessThan(val1?.ToString() ?? ""),
            "=" => val1 is double d ? cf.WhenEquals(d) : cf.WhenEquals(val1?.ToString() ?? ""),
            "!=" => val1 is double d ? cf.WhenNotEquals(d) : cf.WhenNotEquals(val1?.ToString() ?? ""),
            "between" => val1 is double d1 && val2 is double d2
                ? cf.WhenBetween(d1, d2)
                : cf.WhenBetween(val1?.ToString() ?? "", val2?.ToString() ?? ""),
            "contains" => cf.WhenContains(val1?.ToString() ?? ""),
            "duplicates" => cf.WhenIsDuplicate(),
            "unique" => cf.WhenIsUnique(),
            _ => throw new ErroDeDocumento($"operação {opIndex}: 'when' desconhecido: '{when}'. Use >, <, >=, <=, =, !=, between, contains, duplicates ou unique.")
        };

        bool? bold = null, italic = null;
        string? cor = null, fill = null;
        if (cond.TryGetProperty("style", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            if (st.TryGetProperty("bold", out var b)) { styleAlvo.Font.Bold = b.GetBoolean(); bold = b.GetBoolean(); }
            if (st.TryGetProperty("italic", out var it)) { styleAlvo.Font.Italic = it.GetBoolean(); italic = it.GetBoolean(); }
            if (st.TryGetProperty("color", out var cProp) && cProp.GetString() is { } cNome)
            {
                cor = EstiloDePlanilha.ParaHex(cNome);
                styleAlvo.Font.FontColor = XLColor.FromHtml(cor);
            }
            if (st.TryGetProperty("fill", out var fProp) && fProp.GetString() is { } fNome)
            {
                fill = EstiloDePlanilha.ParaHex(fNome);
                styleAlvo.Fill.SetBackgroundColor(XLColor.FromHtml(fill));
            }
        }

        // Descrição da condição
        string condDesc = opNorm switch
        {
            "contains" => $"texto contém \"{val1}\"",
            "between" => $"se o valor entre {FormatarValor(val1)} e {FormatarValor(val2)}",
            "duplicates" => "se o valor for repetido",
            "unique" => "se o valor for único",
            _ => $"se o valor {opNorm} {FormatarValor(val1)}"
        };

        var partesEstilo = new List<string>();
        if (cor != null) partesEstilo.Add($"texto {EstiloDePlanilha.NomeDaCor(cor)}");
        if (fill != null) partesEstilo.Add($"fundo {EstiloDePlanilha.NomeDaCor(fill)}");
        if (bold == true) partesEstilo.Add("negrito");
        if (italic == true) partesEstilo.Add("itálico");
        string estiloDesc = partesEstilo.Count > 0 ? string.Join(" · ", partesEstilo) : "estilo";

        // Hoje atinge
        var atingidas = CelulasAtingidas(faixa, opNorm, val1, val2);
        string atingeDesc = DescreverAtingidas(atingidas);
        string depois = $"{condDesc} → {estiloDesc} · {atingeDesc}";

        // AmostraDepois: a primeira célula que a regra pinta hoje; se nenhuma, a primeira da faixa
        var celulaAmostra = atingidas.Count > 0
            ? faixa.Worksheet.Cell(atingidas[0])
            : faixa.FirstCell();

        var apBase = EstiloDePlanilha.ObterAparencia(celulaAmostra);
        var amostraDepois = new Aparencia(
            apBase.Texto,
            Negrito: bold ?? apBase.Negrito,
            Italico: italic ?? apBase.Italico,
            Sublinhado: apBase.Sublinhado,
            Cor: cor ?? apBase.Cor,
            Fundo: fill ?? apBase.Fundo,
            Alinhamento: apBase.Alinhamento,
            Bordas: apBase.Bordas);

        var mudanca = new Mudanca(TipoDeMudanca.Valor, faixa.RangeAddress.ToString() ?? "", antes, depois, AmostraDepois: amostraDepois);
        return (mudanca, cf);
    }

    private static (Mudanca, IXLConditionalFormat) ProcessarScale(
        IXLRange faixa, string scale, string antes, IXLConditionalFormat cf, int opIndex)
    {
        string norm = scale.Trim().ToLowerInvariant();
        if (!EscalasDeCores.TryGetValue(norm, out var cores))
            throw new ErroDeDocumento($"operação {opIndex}: escala '{scale}' inválida. Use: {string.Join(", ", EscalasDeCores.Keys)}.");

        if (cores.Length == 2)
        {
            cf.ColorScale()
              .LowestValue(XLColor.FromHtml(cores[0]))
              .HighestValue(XLColor.FromHtml(cores[1]));
        }
        else
        {
            cf.ColorScale()
              .LowestValue(XLColor.FromHtml(cores[0]))
              .Midpoint(XLCFContentType.Percentile, 50, XLColor.FromHtml(cores[1]))
              .HighestValue(XLColor.FromHtml(cores[2]));
        }

        var atingidas = CelulasNumericas(faixa);
        string atingeDesc = DescreverAtingidas(atingidas);
        string depois = $"escala {norm} · {atingeDesc}";

        var amostraDepois = new Aparencia("", Escala: cores);
        var mudanca = new Mudanca(TipoDeMudanca.Valor, faixa.RangeAddress.ToString() ?? "", antes, depois, AmostraDepois: amostraDepois);
        return (mudanca, cf);
    }

    private static (Mudanca, IXLConditionalFormat) ProcessarBar(
        IXLRange faixa, string bar, string antes, IXLConditionalFormat cf, int opIndex)
    {
        string hex = EstiloDePlanilha.ParaHex(bar);
        string corNome = EstiloDePlanilha.NomeDaCor(hex);

        cf.DataBar(XLColor.FromHtml(hex)).LowestValue().HighestValue();

        var atingidas = CelulasNumericas(faixa);
        string atingeDesc = DescreverAtingidas(atingidas);
        string depois = $"barra de dados {corNome} · {atingeDesc}";

        var amostraDepois = new Aparencia("", Escala: new[] { hex, "#FFFFFF" });
        var mudanca = new Mudanca(TipoDeMudanca.Valor, faixa.RangeAddress.ToString() ?? "", antes, depois, AmostraDepois: amostraDepois);
        return (mudanca, cf);
    }

    private static (Mudanca, IXLConditionalFormat) ProcessarIcons(
        IXLRange faixa, string icons, string antes, IXLConditionalFormat cf, int opIndex)
    {
        string norm = icons.Trim().ToLowerInvariant();
        if (!ConjuntosDeIcones.TryGetValue(norm, out var conf))
            throw new ErroDeDocumento($"operação {opIndex}: conjunto de ícones '{icons}' inválido. Use: setas, semaforo, sinais ou estrelas.");

        cf.IconSet(conf.Estilo, false, false);

        var atingidas = CelulasNumericas(faixa);
        string atingeDesc = DescreverAtingidas(atingidas);
        string depois = $"conjunto de ícones {norm} · {atingeDesc}";

        var amostraDepois = new Aparencia(conf.Simbolos, Escala: null);
        var mudanca = new Mudanca(TipoDeMudanca.Valor, faixa.RangeAddress.ToString() ?? "", antes, depois, AmostraDepois: amostraDepois);
        return (mudanca, cf);
    }

    /// <summary>
    /// Avalia a condição sobre as células da faixa para relatar quais são atingidas hoje.
    /// </summary>
    private static List<string> CelulasAtingidas(IXLRange faixa, string opNorm, object? val1, object? val2)
    {
        var lista = new List<string>();

        if (opNorm == "duplicates" || opNorm == "unique")
        {
            var frequencias = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in faixa.Cells(false))
            {
                string txt = LeitorDePlanilha.Texto(cell).Trim();
                if (txt.Length == 0) continue;
                frequencias[txt] = frequencias.GetValueOrDefault(txt) + 1;
            }

            foreach (var cell in faixa.Cells(false))
            {
                string txt = LeitorDePlanilha.Texto(cell).Trim();
                if (txt.Length == 0) continue;
                int count = frequencias.GetValueOrDefault(txt);
                string addr = cell.Address.ToString() ?? "";
                if (opNorm == "duplicates" && count > 1) lista.Add(addr);
                if (opNorm == "unique" && count == 1) lista.Add(addr);
            }
            return lista;
        }

        double? d1 = val1 is double n1 ? n1 : double.TryParse(val1?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed1) ? parsed1 : null;
        double? d2 = val2 is double n2 ? n2 : double.TryParse(val2?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed2) ? parsed2 : null;
        string str1 = val1?.ToString() ?? "";

        foreach (var cell in faixa.Cells(false))
        {
            bool atinge = false;
            if (opNorm == "contains")
            {
                string t = LeitorDePlanilha.Texto(cell);
                atinge = t.Contains(str1, StringComparison.OrdinalIgnoreCase);
            }
            else if (d1.HasValue && cell.Value.IsNumber)
            {
                double num = cell.Value.GetNumber();
                atinge = opNorm switch
                {
                    ">" => num > d1.Value,
                    "<" => num < d1.Value,
                    ">=" => num >= d1.Value,
                    "<=" => num <= d1.Value,
                    "=" => Math.Abs(num - d1.Value) < 1e-9,
                    "!=" => Math.Abs(num - d1.Value) >= 1e-9,
                    "between" => d2.HasValue && num >= Math.Min(d1.Value, d2.Value) && num <= Math.Max(d1.Value, d2.Value),
                    _ => false
                };
            }
            else
            {
                string t = LeitorDePlanilha.Texto(cell);
                int comp = string.Compare(t, str1, StringComparison.OrdinalIgnoreCase);
                atinge = opNorm switch
                {
                    ">" => comp > 0,
                    "<" => comp < 0,
                    ">=" => comp >= 0,
                    "<=" => comp <= 0,
                    "=" => comp == 0,
                    "!=" => comp != 0,
                    _ => false
                };
            }

            if (atinge) lista.Add(cell.Address.ToString() ?? "");
        }

        return lista;
    }

    private static List<string> CelulasNumericas(IXLRange faixa)
    {
        var lista = new List<string>();
        foreach (var cell in faixa.Cells(false))
            if (cell.Value.IsNumber)
                lista.Add(cell.Address.ToString() ?? "");
        return lista;
    }

    private static string DescreverAtingidas(IReadOnlyList<string> atingidas)
    {
        if (atingidas.Count == 0) return "hoje não atinge nenhuma célula";
        if (atingidas.Count == 1) return $"hoje atinge 1 célula ({atingidas[0]})";
        if (atingidas.Count <= 5) return $"hoje atinge {atingidas.Count} células ({string.Join(", ", atingidas)})";
        return $"hoje atinge {atingidas.Count} células ({string.Join(", ", atingidas.Take(5))}, …)";
    }

    /// <summary>
    /// Descreve uma regra existente em palavras para o cartão ("barra de dados azul", "se o valor > 100 → fundo amarelo"...).
    /// </summary>
    public static string DescreverRegra(IXLConditionalFormat cf)
    {
        if (cf.ConditionalFormatType == XLConditionalFormatType.DataBar)
        {
            string cor = cf.Colors.Count > 0 ? EstiloDePlanilha.NomeDaCor(EstiloDePlanilha.HexDe(cf.Colors[1])) : "azul";
            return $"barra de dados {cor}";
        }
        if (cf.ConditionalFormatType == XLConditionalFormatType.ColorScale)
        {
            return "escala de cores";
        }
        if (cf.ConditionalFormatType == XLConditionalFormatType.IconSet)
        {
            string nome = cf.IconSetStyle switch
            {
                XLIconSetStyle.ThreeArrows => "setas",
                XLIconSetStyle.ThreeTrafficLights1 or XLIconSetStyle.ThreeTrafficLights2 => "semáforo",
                XLIconSetStyle.ThreeSigns => "sinais",
                _ => "ícones"
            };
            return $"conjunto de ícones {nome}";
        }
        if (cf.ConditionalFormatType == XLConditionalFormatType.ContainsText)
        {
            string val = cf.Values.Count > 0 ? cf.Values[1].Value : "";
            string estilo = DescreverEstiloRegra(cf.Style);
            return estilo.Length > 0 ? $"texto contém \"{val}\" → {estilo}" : $"texto contém \"{val}\"";
        }
        if (cf.ConditionalFormatType == XLConditionalFormatType.IsDuplicate)
        {
            string estilo = DescreverEstiloRegra(cf.Style);
            return $"valores repetidos → {estilo}";
        }
        if (cf.ConditionalFormatType == XLConditionalFormatType.IsUnique)
        {
            string estilo = DescreverEstiloRegra(cf.Style);
            return $"valores únicos → {estilo}";
        }

        if (cf.ConditionalFormatType == XLConditionalFormatType.CellIs)
        {
            string op = cf.Operator switch
            {
                XLCFOperator.GreaterThan => ">",
                XLCFOperator.LessThan => "<",
                XLCFOperator.EqualOrGreaterThan => ">=",
                XLCFOperator.EqualOrLessThan => "<=",
                XLCFOperator.Equal => "=",
                XLCFOperator.NotEqual => "!=",
                XLCFOperator.Between => "entre",
                _ => ""
            };
            string v1 = cf.Values.Count > 0 ? cf.Values[1].Value : "";
            string v2 = cf.Values.Count > 1 ? cf.Values[2].Value : "";
            string cond = op == "entre" ? $"se o valor entre {v1} e {v2}" : $"se o valor {op} {v1}";
            string estilo = DescreverEstiloRegra(cf.Style);
            return estilo.Length > 0 ? $"{cond} → {estilo}" : cond;
        }

        return "regra condicional";
    }

    private static string DescreverEstiloRegra(IXLStyle s)
    {
        var partes = new List<string>();
        string? cor = EstiloDePlanilha.HexDe(s.Font.FontColor);
        if (cor != null && cor != "#000000") partes.Add($"texto {EstiloDePlanilha.NomeDaCor(cor)}");

        string? fundo = EstiloDePlanilha.HexDe(s.Fill.BackgroundColor);
        if (fundo != null) partes.Add($"fundo {EstiloDePlanilha.NomeDaCor(fundo)}");

        if (s.Font.Bold) partes.Add("negrito");
        if (s.Font.Italic) partes.Add("itálico");

        return string.Join(" · ", partes);
    }

    private static string FormatarValor(object? v) => v is double d
        ? d.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"))
        : v?.ToString() ?? "";

    private static object? DoJson(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => v.GetRawText()
    };
}
