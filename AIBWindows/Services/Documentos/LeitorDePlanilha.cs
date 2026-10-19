using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace AIB.Services.Documentos;

/// <summary>
/// Abre .xlsx, .xlsm, .xls e .csv como uma <see cref="Pasta"/>: as abas na ordem do Excel, cada
/// célula no seu lugar, com o texto como o Excel mostra, o valor bruto e a fórmula.
/// </summary>
public static class LeitorDePlanilha
{
    /// <summary>Acima disto a planilha não é lida para a memória de uma vez.</summary>
    public const long TetoDeCelulas = 3_000_000;

    public static Pasta Abrir(string caminho) => Formatos.De(caminho) switch
    {
        Formato.Xlsx => AbrirXlsx(caminho),
        Formato.Xls => LeitorDeXls.Abrir(caminho),
        Formato.Csv => new Pasta(Formato.Csv, new[] { LeitorDeCsv.Ler(caminho).Grade }),
        _ => throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' não é uma planilha que esta ferramenta leia.")
    };

    private static Pasta AbrirXlsx(string caminho)
    {
        // Compartilhando leitura e escrita: o Excel aberto com o arquivo não impede a leitura.
        using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var wb = new XLWorkbook(fs);

        var abas = new List<Grade>();
        long celulas = 0;

        foreach (var ws in wb.Worksheets)
        {
            var grade = new Grade(ws.Name, ws.Visibility != XLWorksheetVisibility.Visible);
            var usada = ws.RangeUsed(XLCellsUsedOptions.Contents);
            if (usada == null)
            {
                abas.Add(grade);
                continue;
            }

            int l1 = usada.FirstRow().RowNumber(), l2 = usada.LastRow().RowNumber();
            int c2 = usada.LastColumn().ColumnNumber();
            celulas += (long)(l2 - l1 + 1) * c2;
            if (celulas > TetoDeCelulas)
                throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' passa de {TetoDeCelulas:N0} células; grande demais para ler de uma vez.");

            grade.PrimeiraLinha = l1;
            for (int r = l1; r <= l2; r++)
            {
                var linha = new Celula[c2];
                var row = ws.Row(r);
                if (row.IsHidden)
                    grade.Ocultas.Add(r);

                for (int c = 1; c <= c2; c++)
                {
                    var cell = row.Cell(c);
                    linha[c - 1] = cell.IsEmpty(XLCellsUsedOptions.Contents) ? Celula.Vazia : Ler(cell);
                }
                grade.Acrescentar(linha);
            }

            if (ws.AutoFilter.IsEnabled && ws.AutoFilter.Range != null)
                grade.Filtro = DescreverFiltro(ws);

            abas.Add(grade);
        }

        return new Pasta(Formato.Xlsx, abas);
    }

    private static string? DescreverFiltro(IXLWorksheet ws)
    {
        var af = ws.AutoFilter;
        if (!af.IsEnabled || af.Range is not { } range) return null;

        string faixa = range.RangeAddress.ToString() ?? "";
        var criterios = new List<string>();

        int r1 = range.RangeAddress.FirstAddress.RowNumber;
        int c1 = range.RangeAddress.FirstAddress.ColumnNumber;
        int c2 = range.RangeAddress.LastAddress.ColumnNumber;

        var colunasFiltradas = ObterColunasFiltradas(af, c1, c2);
        foreach (var (colNum, col) in colunasFiltradas)
        {
            if (col.FilterType == XLFilterType.None) continue;

            string nome = ws.Cell(r1, colNum).IsEmpty(XLCellsUsedOptions.Contents)
                ? Grade.Letra(colNum)
                : Apresentacao.Achatar(Texto(ws.Cell(r1, colNum)).Trim());
            if (nome.Length == 0) nome = Grade.Letra(colNum);

            string? crit = DescreverColuna(col, nome);
            if (!string.IsNullOrEmpty(crit))
                criterios.Add(crit);
        }

        if (criterios.Count == 0)
            return $"{faixa} · sem critério";

        return $"{faixa} · {string.Join("; ", criterios)}";
    }

    private static List<(int ColNum, IXLFilterColumn Col)> ObterColunasFiltradas(IXLAutoFilter af, int c1, int c2)
    {
        var lista = new List<(int, IXLFilterColumn)>();
        try
        {
            var prop = af.GetType().GetProperty("Columns", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (prop?.GetValue(af) is System.Collections.IDictionary dict)
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    if (entry.Key is int colNum && entry.Value is IXLFilterColumn fc)
                        lista.Add((colNum, fc));
                }
                return lista.OrderBy(x => x.Item1).ToList();
            }
        }
        catch { }

        for (int c = c1; c <= c2; c++)
        {
            try
            {
                var col = af.Column(c);
                if (col.FilterType != XLFilterType.None)
                    lista.Add((c, col));
            }
            catch { }
        }
        return lista;
    }

    private static string DescreverColuna(IXLFilterColumn col, string nome)
    {
        switch (col.FilterType)
        {
            case XLFilterType.Regular:
            {
                var valores = new List<string>();
                if (col is System.Collections.IEnumerable en)
                {
                    foreach (object item in en)
                    {
                        var valProp = item.GetType().GetProperty("Value");
                        var val = valProp?.GetValue(item)?.ToString();
                        if (!string.IsNullOrEmpty(val))
                            valores.Add(val);
                    }
                }
                valores = valores.Distinct().ToList();
                return valores.Count > 0
                    ? $"{nome} = {string.Join(", ", valores)}"
                    : $"{nome} (filtro personalizado)";
            }

            case XLFilterType.Custom:
            {
                var partes = new List<(string Op, string Val, string? Conn)>();
                if (col is System.Collections.IEnumerable en)
                {
                    foreach (object item in en)
                    {
                        var opProp = item.GetType().GetProperty("Operator");
                        var customValProp = item.GetType().GetProperty("CustomValue");
                        var connProp = item.GetType().GetProperty("Connector");

                        var opObj = opProp?.GetValue(item);
                        var customValObj = customValProp?.GetValue(item);
                        var connObj = connProp?.GetValue(item);

                        string opStr = opObj?.ToString() switch
                        {
                            "Equal" => "=",
                            "NotEqual" => "!=",
                            "GreaterThan" => ">",
                            "LessThan" => "<",
                            "EqualOrGreaterThan" => ">=",
                            "EqualOrLessThan" => "<=",
                            _ => ""
                        };

                        string valStr = customValObj?.ToString() ?? "";
                        string? connStr = connObj?.ToString() switch
                        {
                            "And" => "e",
                            "Or" => "ou",
                            _ => null
                        };

                        if (!string.IsNullOrEmpty(opStr))
                            partes.Add((opStr, valStr, connStr));
                    }
                }

                if (partes.Count == 1)
                    return $"{nome} {partes[0].Op} {partes[0].Val}";

                if (partes.Count == 2)
                {
                    string conector = partes[1].Conn ?? "e";
                    return $"{nome} {partes[0].Op} {partes[0].Val} {conector} {partes[1].Op} {partes[1].Val}";
                }

                return $"{nome} (filtro personalizado)";
            }

            case XLFilterType.TopBottom:
            {
                string parte = col.TopBottomPart == XLTopBottomPart.Top ? "maiores" : "menores";
                string tipo = col.TopBottomType == XLTopBottomType.Percent ? "%" : "itens";
                return $"{nome} ({parte} {col.TopBottomValue} {tipo})";
            }

            case XLFilterType.Dynamic:
            {
                return $"{nome} ({col.DynamicType})";
            }

            default:
                return $"{nome} (filtro personalizado)";
        }
    }

    /// <summary>O texto da célula como esta ferramenta mostra — o mesmo na leitura e no cartão.</summary>
    internal static string Texto(IXLCell cell) => cell.IsEmpty(XLCellsUsedOptions.Contents) ? "" : Ler(cell).Texto;

    /// <summary>
    /// Uma célula. Fórmula usa o valor EM CACHE (o que o Excel calculou ao salvar): recalcular na
    /// leitura seria lento e erraria nas funções que o ClosedXML não conhece.
    /// </summary>
    private static Celula Ler(IXLCell cell)
    {
        string? formula = cell.HasFormula ? cell.FormulaA1 : null;
        XLCellValue v;
        try
        {
            v = formula != null ? cell.CachedValue : cell.Value;

            // Arquivo gravado sem os valores em cache (o próprio ClosedXML, geradores de
            // relatório): aí o jeito é calcular.
            if (formula != null && v.IsBlank) v = cell.Value;
        }
        catch { return new Celula("", null, formula); }

        object? bruto = v.Type switch
        {
            XLDataType.Number => v.GetNumber(),
            XLDataType.DateTime => v.GetDateTime(),
            XLDataType.TimeSpan => v.GetTimeSpan(),
            XLDataType.Boolean => v.GetBoolean(),
            XLDataType.Text => v.GetText(),
            XLDataType.Error => v.GetError().ToString(),
            _ => null
        };

        string texto;
        // Data e fórmula pelo formatador daqui: o do ClosedXML escreve a data de formato
        // embutido (id 22) no padrão americano, "1/2/2026 14:30".
        try { texto = formula != null || v.IsDateTime || v.IsNumber || EhData(cell) ? Formatar(cell, v) : cell.GetFormattedString(); }
        catch { texto = v.ToString(); }

        return new Celula(texto, bruto, formula);
    }

    private static bool EhData(IXLCell cell)
    {
        int id = cell.Style.NumberFormat.NumberFormatId;
        string f = cell.Style.NumberFormat.Format ?? "";
        return id is >= 14 and <= 22 || f.Contains("yy", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>O valor em cache com o formato da célula — sem recalcular.</summary>
    private static string Formatar(IXLCell cell, XLCellValue v)
    {
        if (v.IsBlank) return "";
        if (v.IsText) return v.GetText();
        if (v.IsError) return ErroDoExcel(v.GetError());
        if (v.IsBoolean) return v.GetBoolean() ? "VERDADEIRO" : "FALSO";

        string formato = cell.Style.NumberFormat.Format;
        int id = cell.Style.NumberFormat.NumberFormatId;
        double numero = v.IsDateTime ? v.GetDateTime().ToOADate() : v.IsTimeSpan ? v.GetTimeSpan().TotalDays : v.GetNumber();

        if (v.IsDateTime || id is >= 14 and <= 22 || formato.Contains("yy", StringComparison.OrdinalIgnoreCase)
            || formato.Contains("dd", StringComparison.OrdinalIgnoreCase))
        {
            var data = DateTime.FromOADate(numero);
            return data.TimeOfDay == TimeSpan.Zero ? data.ToString("dd/MM/yyyy") : data.ToString("dd/MM/yyyy HH:mm");
        }

        var br = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        int casas = Casas(formato);
        if (formato.Contains('%') || id is 9 or 10) return (numero * 100).ToString(casas > 0 ? "0." + new string('0', casas) : "0", br) + "%";

        // "Geral": como o Excel mostra, sem separador de milhar — um ramal 2041 não é "2.041".
        if (id == 0 || formato.Length == 0 || formato.Equals("General", StringComparison.OrdinalIgnoreCase))
            return numero.ToString("0.##########", br);

        bool milhar = formato.Contains("#,##") || formato.Contains(",0") || id is 3 or 4 or >= 37 and <= 44;
        string texto = numero.ToString((milhar ? "#,##0" : "0") + (casas > 0 ? "." + new string('0', casas) : ""), br);
        return formato.Contains("R$") ? "R$ " + texto : formato.Contains('$') ? "$ " + texto : texto;
    }

    /// <summary>Casas decimais do formato: "#,##0.00" → 2.</summary>
    private static int Casas(string formato)
    {
        string primeira = formato.Split(';')[0];
        int ponto = primeira.IndexOf('.');
        if (ponto < 0) return 0;
        int n = 0;
        for (int i = ponto + 1; i < primeira.Length && (primeira[i] == '0' || primeira[i] == '#'); i++) n++;
        return n;
    }

    private static string ErroDoExcel(XLError e) => e switch
    {
        XLError.CellReference => "#REF!",
        XLError.DivisionByZero => "#DIV/0!",
        XLError.NoValueAvailable => "#N/D",
        XLError.IncompatibleValue => "#VALOR!",
        XLError.NameNotRecognized => "#NOME?",
        XLError.NumberInvalid => "#NÚM!",
        XLError.NullValue => "#NULO!",
        _ => "#ERRO"
    };
}
