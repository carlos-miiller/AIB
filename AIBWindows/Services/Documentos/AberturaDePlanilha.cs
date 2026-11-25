using System.IO;
using System.IO.Compression;
using System.Linq;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AIB.Services.Documentos;

/// <summary>
/// Abre um .xlsx/.xlsm no ClosedXML trocando, em memória, a regra condicional de texto que ele não
/// sabe regravar.
/// <para>
/// Visto em 21/11/2026: o checklist de normalização da Ellen tinha "contém 'Crítica (P0)'" (gravado
/// pela própria doc), e a edição seguinte falhava com "The formula `Crítica (P0)` wasn't parsed
/// correctly" — a doc dizia "ERRO ao abrir", mas o erro é na GRAVAÇÃO: o ClosedXML abre a regra
/// containsText (e as de não contém, começa e termina com) e, ao gravar, ajusta o texto dela como se
/// fosse fórmula. "Crítica" passava (vira um nome); com espaço, parêntese ou aspas, não. Ela
/// contornou com um script Python que encurtou o texto para "Crítica".
/// </para>
/// <para>
/// A regra vira uma de fórmula, com a fórmula que o Excel já grava junto
/// (<c>NOT(ISERROR(SEARCH("texto",H2)))</c>): pinta as mesmas células. O arquivo em disco só muda se
/// for gravado depois.
/// </para>
/// </summary>
internal static class AberturaDePlanilha
{
    private static readonly string[] TiposDeTexto =
        { "\"containsText\"", "\"notContainsText\"", "\"beginsWith\"", "\"endsWith\"" };

    /// <summary>Lê o arquivo compartilhando leitura e escrita (o Excel aberto não impede) e abre.</summary>
    public static XLWorkbook Abrir(string caminho)
    {
        using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var mem = new MemoryStream();
        fs.CopyTo(mem);
        return Abrir(mem.ToArray());
    }

    public static XLWorkbook Abrir(byte[] bytes) =>
        new(Copia(TemRegraDeTexto(bytes) ? SemTextoNasRegras(bytes) : bytes));

    private static MemoryStream Copia(byte[] bytes)
    {
        var m = new MemoryStream();
        m.Write(bytes, 0, bytes.Length);
        m.Position = 0;
        return m;
    }

    /// <summary>
    /// Se alguma aba tem regra de texto. Pelo XML cru das abas, sem montar o documento: é o caso raro,
    /// e o comum não paga o custo de abrir duas vezes.
    /// </summary>
    private static bool TemRegraDeTexto(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml")))
            {
                using var r = new StreamReader(e.Open());
                string xml = r.ReadToEnd();
                if (xml.Contains("cfRule") && TiposDeTexto.Any(xml.Contains)) return true;
            }
        }
        catch (InvalidDataException)
        {
            // Não é zip: o ClosedXML dá o erro certo logo depois.
        }
        return false;
    }

    /// <summary>As regras de texto (contém, não contém, começa, termina) viram regras de fórmula.</summary>
    private static byte[] SemTextoNasRegras(byte[] bytes)
    {
        var m = Copia(bytes);
        using (var doc = SpreadsheetDocument.Open(m, true))
        {
            foreach (var ws in doc.WorkbookPart?.WorksheetParts ?? Enumerable.Empty<WorksheetPart>())
            {
                bool mudou = false;
                foreach (var regra in ws.Worksheet.Descendants<ConditionalFormattingRule>())
                {
                    if (regra.Type?.Value is not { } tipo) continue;
                    if (tipo != ConditionalFormatValues.ContainsText && tipo != ConditionalFormatValues.NotContainsText
                        && tipo != ConditionalFormatValues.BeginsWith && tipo != ConditionalFormatValues.EndsWith) continue;
                    // Sem a fórmula, não há o que avaliar no lugar do texto: fica como está.
                    if (!regra.Elements<Formula>().Any()) continue;

                    regra.Type = ConditionalFormatValues.Expression;
                    regra.Operator = null;
                    regra.Text = null;
                    mudou = true;
                }
                if (mudou) ws.Worksheet.Save();
            }
        }
        return m.ToArray();
    }
}
