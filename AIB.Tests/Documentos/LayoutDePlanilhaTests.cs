using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIB.Services.Documentos;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// O acabamento de uma aba. Visto em 26/10/2026: o painel que a Ellen montou
/// (dashboard-tarefas-ti-mock.xlsx) saiu com todas as linhas na altura padrão, o título pintado
/// em seis células sem mesclar, grade do Excel à mostra e borda até nas linhas vazias; o modelo de
/// rubrica que o usuário deu de exemplo tem título numa linha de 50 pontos, linha fina de 11
/// separando blocos, recuo 1 no texto, folha sem grade, lista suspensa nas notas e impressão
/// ajustada à largura. Nada disso a ferramenta deixava fazer.
/// </summary>
public class LayoutDePlanilhaTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-layout-" + Guid.NewGuid().ToString("N"));

    public LayoutDePlanilhaTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private string Planilha()
    {
        string c = Path.Combine(_pasta, Guid.NewGuid().ToString("N") + ".xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Painel");
        ws.Cell("A1").Value = "PAINEL";
        ws.Cell("A2").Value = "Item";
        ws.Cell("B2").Value = "Status";
        ws.Cell("A3").Value = "x";
        ws.Cell("A4").Value = "y";
        wb.SaveAs(c);
        return c;
    }

    private static JsonElement Ops(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Gravacao Gravar(string c, string ops)
    {
        var g = EditorDePlanilha.Planejar(c, Ops(ops), null);
        g.Gravar();
        return g;
    }

    private static Action Planejar(string c, string ops) => () => EditorDePlanilha.Planejar(c, Ops(ops), null);

    [Fact]
    public void Indent_DaORecuoEOCartaoDiz()
    {
        string c = Planilha();
        var g = Gravar(c, """[{"range":"A2:B4","style":{"indent":1,"valign":"middle"}}]""");

        g.Plano.Mudancas.Single().Depois.Should().Contain("recuo 1");
        using var wb = new XLWorkbook(c);
        wb.Worksheet(1).Cell("B4").Style.Alignment.Indent.Should().Be(1);
    }

    [Fact]
    public void Indent_ForaDaFaixa_DizOQueAceita()
    {
        Planejar(Planilha(), """[{"cell":"A1","style":{"indent":20}}]""")
            .Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("0 a 15");
    }

    [Fact]
    public void Altura_EmPontos_EAuto()
    {
        string c = Planilha();
        var g = Gravar(c, """[{"rows":"1","height":50},{"rows":"3:4","height":11}]""");

        g.Plano.Titulo.Should().Be("Ajustar o layout desta planilha?");
        g.Plano.Mudancas[0].Onde.Should().Be("Altura da linha 1");
        using (var wb = new XLWorkbook(c))
        {
            wb.Worksheet(1).Row(1).Height.Should().Be(50);
            wb.Worksheet(1).Row(4).Height.Should().Be(11);
        }

        Gravar(c, """[{"rows":1,"height":"auto"}]""");
        using (var wb = new XLWorkbook(c))
            wb.Worksheet(1).Row(1).Height.Should().NotBe(50);
    }

    [Theory]
    [InlineData("""[{"height":40}]""", "vai com 'rows'")]
    [InlineData("""[{"rows":"1","height":500}]""", "1 a 409")]
    [InlineData("""[{"rows":"1","height":"alta"}]""", "'auto'")]
    public void Altura_Errada_DizOFormato(string ops, string trecho)
    {
        Planejar(Planilha(), ops).Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain(trecho);
    }

    [Fact]
    public void Mesclar_ETirarAMescla()
    {
        string c = Planilha();
        Gravar(c, """[{"merge":"A1:F1"}]""");
        using (var wb = new XLWorkbook(c))
            wb.Worksheet(1).MergedRanges.Single().RangeAddress.ToString().Should().Be("A1:F1");

        Gravar(c, """[{"unmerge":"A1:F1"}]""");
        using (var wb = new XLWorkbook(c))
            wb.Worksheet(1).MergedRanges.Should().BeEmpty();
    }

    [Fact]
    public void Mesclar_ComTextoVazioNasOutras_Aceita()
    {
        // O título da Ellen: "" gravado em B1:F1 para pintar o fundo. Texto vazio não se perde.
        string c = Planilha();
        Gravar(c, """[{"range":"B1:F1","value":""},{"merge":"A1:F1"}]""");
        using var wb = new XLWorkbook(c);
        wb.Worksheet(1).Cell("A1").GetString().Should().Be("PAINEL");
    }

    [Fact]
    public void Mesclar_EscondendoConteudo_Recusa()
    {
        Planejar(Planilha(), """[{"merge":"A2:B2"}]""")
            .Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("B2 ('Status')").And.Contain("escondido");
    }

    [Fact]
    public void Mesclar_SobreOutraMescla_MandaDesfazerAntes()
    {
        string c = Planilha();
        Gravar(c, """[{"merge":"C1:F1"}]""");
        Planejar(c, """[{"merge":"E1:H1"}]""").Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("unmerge:'C1:F1'");

        Gravar(c, """[{"unmerge":"C1:F1"},{"merge":"E1:H1"}]""");
    }

    [Fact]
    public void Grade_Escondida()
    {
        string c = Planilha();
        var g = Gravar(c, """[{"gridlines":false}]""");

        g.Plano.Mudancas.Single().Depois.Should().Be("escondidas");
        using var wb = new XLWorkbook(c);
        wb.Worksheet(1).ShowGridLines.Should().BeFalse();
    }

    [Fact]
    public void ListaSuspensa_DeValores_DeFaixa_ETirar()
    {
        string c = Planilha();
        var g = Gravar(c, """
            [{"range":"B3:B4","validation":{"list":["No Prazo","Atrasado"]}},
             {"range":"C3:C4","validation":{"list":"=$A$3:$A$4"}}]
            """);

        g.Plano.Mudancas[0].Depois.Should().Be("aceita só: No Prazo, Atrasado");
        using (var wb = new XLWorkbook(c))
        {
            var ws = wb.Worksheet(1);
            ws.Cell("B4").GetDataValidation().Value.Should().Be("\"No Prazo,Atrasado\"");
            ws.Cell("C3").GetDataValidation().Value.Should().Be("$A$3:$A$4");
        }

        Gravar(c, """[{"range":"B3:B4","validation":"none"}]""");
        using (var wb = new XLWorkbook(c))
            wb.Worksheet(1).DataValidations.GetAllInRange(wb.Worksheet(1).Range("B3:B4").RangeAddress).Should().BeEmpty();
    }

    [Fact]
    public void ListaSuspensa_ListaSoltaVale_EVirgulaRecusa()
    {
        string c = Planilha();
        Gravar(c, """[{"range":"B3:B4","validation":["Sim","Não"]}]""");

        Planejar(c, """[{"range":"B3:B4","validation":{"list":["1,5","2"]}}]""")
            .Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("vírgula").And.Contain("=$H$2:$H$9");
    }

    [Fact]
    public void Filtro_LigaEDesliga()
    {
        string c = Planilha();
        Gravar(c, """[{"filter":"A2:B4"}]""");
        using (var wb = new XLWorkbook(c))
            wb.Worksheet(1).AutoFilter.Range.RangeAddress.ToString().Should().Be("A2:B4");

        Gravar(c, """[{"filter":"none"}]""");
        using (var wb = new XLWorkbook(c))
            wb.Worksheet(1).AutoFilter.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Impressao_ComoARubrica()
    {
        string c = Planilha();
        var g = Gravar(c, """[{"print":{"orientation":"portrait","fit":"width","area":"A1:F30","margins":"estreita","repeat_rows":"1:2","center":true}}]""");

        g.Plano.Mudancas.Single().Depois.Should().Contain("cabe na largura de uma página").And.Contain("imprime só A1:F30");
        using var wb = new XLWorkbook(c);
        var ps = wb.Worksheet(1).PageSetup;
        ps.PageOrientation.Should().Be(XLPageOrientation.Portrait);
        ps.PagesWide.Should().Be(1);
        ps.PagesTall.Should().Be(0);
        ps.PrintAreas.Single().RangeAddress.ToString()!.Replace("$", "").Should().Be("A1:F30");
        ps.Margins.Left.Should().Be(0.25);
        ps.FirstRowToRepeatAtTop.Should().Be(1);
        ps.LastRowToRepeatAtTop.Should().Be(2);
        ps.CenterHorizontally.Should().BeTrue();
    }

    [Theory]
    [InlineData("""[{"print":{"fit":"tudo"}}]""", "'width'")]
    [InlineData("""[{"print":{"papel":"A4"}}]""", "desconhecida")]
    [InlineData("""[{"print":{}}]""", "vazio")]
    public void Impressao_Errada_DizOQueAceita(string ops, string trecho)
    {
        Planejar(Planilha(), ops).Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain(trecho);
    }

    [Fact]
    public void Csv_RecusaLayout()
    {
        string c = Path.Combine(_pasta, "a.csv");
        File.WriteAllText(c, "a;b\r\n1;2\r\n");
        Planejar(c, """[{"merge":"A1:B1"}]""").Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("CSV");
    }
}
