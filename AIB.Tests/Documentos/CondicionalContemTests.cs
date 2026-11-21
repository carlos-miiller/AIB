using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using AIB.Services.Documentos;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// Regra condicional "contém" com texto que não é um nome de fórmula. Visto em 21/11/2026: a Ellen
/// criou o checklist de normalização com "contém 'Crítica (P0)'" nas abas de prioridade, e a
/// edição seguinte do MESMO arquivo falhou ao abrir: "The formula `Crítica (P0)` wasn't parsed
/// correctly". O ClosedXML grava o texto no atributo <c>text</c> da regra containsText e, ao reler,
/// interpreta esse atributo como fórmula; "Crítica" passa (vira um nome), "Crítica (P0)" não. Ela
/// contornou com um script Python que trocou o texto para "Crítica".
/// </summary>
public class CondicionalContemTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-contem-" + Guid.NewGuid().ToString("N"));

    public CondicionalContemTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private string Planilha()
    {
        string c = Path.Combine(_pasta, "checklist.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Fisica");
        ws.Cell("H1").Value = "Prioridade";
        ws.Cell("H2").Value = "Crítica (P0)";
        ws.Cell("H3").Value = "Média (P2)";
        ws.Cell("I1").Value = "Status";
        wb.SaveAs(c);
        return c;
    }

    [Theory]
    [InlineData("Crítica (P0)")]
    [InlineData("P0 / urgente")]
    [InlineData("50%")]
    [InlineData("diz \"sim\"")]
    public void Contem_ComTextoQualquer_AEdicaoSeguinteAbreOArquivo(string texto)
    {
        string c = Planilha();
        var regra = JsonSerializer.Serialize(new[]
        {
            new { range = "H2:H28", conditional = new { when = "contains", value = texto, style = new { fill = "#FCE4D6", bold = true } } }
        });
        EditorDePlanilha.Planejar(c, Json(regra), "Fisica").Gravar();

        // A edição seguinte, como a da Ellen: antes, aqui estourava ao abrir.
        var g = EditorDePlanilha.Planejar(c, Json("""[{"cell":"I2","value":"Ignorar"}]"""), "Fisica");
        g.Gravar();

        using var wb = new XLWorkbook(c);
        wb.Worksheet("Fisica").Cell("I2").GetString().Should().Be("Ignorar");
        wb.Worksheet("Fisica").ConditionalFormats.Should().ContainSingle("a regra continua lá depois da segunda gravação");
    }

    [Fact]
    public void Contem_GravaABuscaQueOExcelAvalia_ECartaoDescreveComoContem()
    {
        string c = Planilha();
        EditorDePlanilha.Planejar(c, Json("""[{"range":"H2:H28","conditional":{"when":"contains","value":"Crítica (P0)","style":{"bold":true}}}]"""), "Fisica").Gravar();

        string xml;
        using (var zip = ZipFile.OpenRead(c))
        using (var r = new StreamReader(zip.Entries.First(e => e.FullName.StartsWith("xl/worksheets/sheet")).Open()))
            xml = r.ReadToEnd();
        xml.Should().Contain("SEARCH(\"Crítica (P0)\",H2)", "é a mesma busca que o Excel faz no 'contém'");

        // Trocar a regra mostra no cartão o que havia antes, em palavras.
        var g = EditorDePlanilha.Planejar(c, Json("""[{"range":"H2:H28","conditional":{"when":"contains","value":"Alta","style":{"bold":true}}}]"""), "Fisica");
        g.Plano.Mudancas.Should().Contain(m => m.Antes.Contains("texto contém \"Crítica (P0)\""));
    }

    [Fact]
    public void ArquivoJaGravadoComOProblema_AbreDoMesmoJeito()
    {
        // O checklist que ficou no disco antes da correção: containsText com "Crítica (P0)".
        string c = Planilha();
        string xml;
        using (var zip = ZipFile.Open(c, ZipArchiveMode.Update))
        {
            var e = zip.Entries.First(x => x.FullName.StartsWith("xl/worksheets/sheet"));
            using (var r = new StreamReader(e.Open())) xml = r.ReadToEnd();
            e.Delete();
            xml = xml.Replace("</x:sheetData>", "</x:sheetData><x:conditionalFormatting sqref=\"H2:H28\"><x:cfRule type=\"containsText\" priority=\"1\" operator=\"containsText\" text=\"Crítica (P0)\"><x:formula>NOT(ISERROR(SEARCH(\"Crítica (P0)\",H2)))</x:formula></x:cfRule></x:conditionalFormatting>")
                     .Replace("</sheetData>", "</sheetData><conditionalFormatting sqref=\"H2:H28\"><cfRule type=\"containsText\" priority=\"1\" operator=\"containsText\" text=\"Crítica (P0)\"><formula>NOT(ISERROR(SEARCH(\"Crítica (P0)\",H2)))</formula></cfRule></conditionalFormatting>");
            using var w = new StreamWriter(zip.CreateEntry(e.FullName).Open());
            w.Write(xml);
        }
        xml.Should().Contain("containsText", "o arquivo de teste tem de ter a regra problemática");

        var g = EditorDePlanilha.Planejar(c, Json("""[{"cell":"I2","value":"Ignorar"}]"""), "Fisica");
        g.Gravar();

        using var wb = new XLWorkbook(c);
        wb.Worksheet("Fisica").Cell("I2").GetString().Should().Be("Ignorar");
    }
}
