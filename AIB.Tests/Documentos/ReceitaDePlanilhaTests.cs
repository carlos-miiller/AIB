using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services.Documentos;
using AIB.Services.Tools;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// action='layout': a formatação de uma aba escrita como ops do edit. Em 26/10/2026 o usuário deu
/// um modelo de rubrica como exemplo do que queria que a Ellen fizesse; o 'read' só mostra os
/// valores, e ela não tinha como ver COMO o modelo era feito. A garantia principal: a receita,
/// aplicada num arquivo vazio, dá a mesma receita — o que ela ensina funciona.
/// </summary>
public class ReceitaDePlanilhaTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-receita-" + Guid.NewGuid().ToString("N"));

    public ReceitaDePlanilhaTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private string Vazia(string nome)
    {
        string c = Path.Combine(_pasta, nome);
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Painel");
        wb.SaveAs(c);
        return c;
    }

    /// <summary>Um painel no estilo da rubrica, montado pela própria ferramenta.</summary>
    private string Modelo()
    {
        string c = Vazia("modelo.xlsx");
        EditorDePlanilha.Planejar(c, Json("""
            [{"gridlines":false},
             {"columns":"A","width":3},{"columns":"B","width":40},{"columns":"C:D","width":14},
             {"rows":"1","height":44},{"rows":"3","height":10},
             {"range":"B1:D12","style":{"font":"Century Gothic","size":10}},
             {"cell":"B1","value":"PAINEL DE TAREFAS","style":{"bold":true,"size":20,"valign":"middle","color":"#44546A"}},
             {"merge":"B1:D1"},
             {"range":"B4:D4","style":{"bold":true,"color":"branco","fill":"#222A35","border":"all","valign":"middle","indent":1}},
             {"cell":"B4","value":"Tarefa"},{"cell":"C4","value":"Status"},{"cell":"D4","value":"Horas"},
             {"range":"B5:D7","style":{"border":"all","valign":"middle","indent":1}},
             {"cell":"B5","value":"Trocar switch"},{"cell":"C5","value":"No Prazo"},{"cell":"D5","value":3.5},
             {"cell":"B6","value":"Backup"},{"cell":"C6","value":"Atrasado"},{"cell":"D6","value":2},
             {"range":"D5:D8","style":{"number_format":"decimal","align":"right"}},
             {"cell":"B8","value":"TOTAL","style":{"bold":true,"border":["top","double"]}},
             {"cell":"D8","value":"=SUM(D5:D7)","style":{"bold":true}},
             {"range":"C5:C7","validation":{"list":["No Prazo","Atrasado"]}},
             {"cell":"B10","value":"Portal","link":"https://exemplo.com.br"},
             {"freeze":"A5"},
             {"print":{"orientation":"landscape","fit":"width","area":"B1:D10"}}]
            """), null).Gravar();
        return c;
    }

    private static JsonElement Ops(string receita)
    {
        int i = receita.IndexOf("\n[", StringComparison.Ordinal);
        int f = receita.IndexOf("\n]", StringComparison.Ordinal);
        return Json(receita[(i + 1)..(f + 2)]);
    }

    [Fact]
    public void Receita_AplicadaNumArquivoVazio_DaAMesmaReceita()
    {
        string receita = ReceitaDePlanilha.Descrever(Modelo(), null, null, 60000);

        string copia = Vazia("copia.xlsx");
        EditorDePlanilha.Planejar(copia, Ops(receita), null).Gravar();
        string deNovo = ReceitaDePlanilha.Descrever(copia, null, null, 60000);

        Ops(deNovo).GetRawText().Should().Be(Ops(receita).GetRawText());
    }

    [Fact]
    public void Receita_DizOQueImporta()
    {
        string r = ReceitaDePlanilha.Descrever(Modelo(), null, null, 60000);

        r.Should().Contain("""{"gridlines":false}""")
            .And.Contain("""{"columns":"B","width":40}""")
            .And.Contain("""{"rows":"1","height":44}""")
            .And.Contain("""{"range":"A1:D12","style":{"font":"Century Gothic","size":10}}""")
            .And.Contain("""{"merge":"B1:D1"}""")
            .And.Contain("\"fill\":\"#222A35\"")
            .And.Contain("\"indent\":1")
            .And.Contain("""{"cell":"D8","value":"=SUM(D5:D7)"}""")
            .And.Contain("""{"range":"C5:C7","validation":{"list":["No Prazo","Atrasado"]}}""")
            .And.Contain("""{"cell":"B10","link":"https://exemplo.com.br/"}""")
            .And.Contain("""{"freeze":"A5"}""")
            .And.Contain("\"orientation\":\"landscape\"");
    }

    [Fact]
    public void CelulaVaziaSemBordaNemCor_NaoViraOp()
    {
        // A rubrica pinta de branco até a coluna O para esconder a grade; isso virava dezenas de
        // ops sem efeito visível.
        string c = Vazia("branco.xlsx");
        using (var wb = new XLWorkbook(c))
        {
            var ws = wb.Worksheet(1);
            ws.Cell("A1").Value = "x";
            ws.Range("C1:H20").Style.Fill.SetBackgroundColor(XLColor.White);
            ws.Range("C1:H20").Style.Font.Bold = true;
            wb.Save();
        }

        string r = ReceitaDePlanilha.Descrever(c, null, null, 60000);
        r.Should().NotContain("C1:H20").And.NotContain("#FFFFFF");
    }

    [Theory]
    [InlineData(0x44, 0x54, 0x6A, 0.0, "#44546A")]
    [InlineData(0xFF, 0xFF, 0xFF, -0.0499893185216834, "#F2F2F2")]
    [InlineData(0x44, 0x54, 0x6A, -0.499984740745262, "#222A35")]
    [InlineData(0xFF, 0xC0, 0x00, 0.7999816888943144, "#FFF2CC")]
    public void CorDeTema_ComATonalidadeDoExcel(byte r, byte g, byte b, double tint, string hex)
    {
        ReceitaDePlanilha.Tonalidade(r, g, b, tint).Should().Be(hex);
    }

    [Fact]
    public void ComRange_SoAquelasLinhas()
    {
        string r = ReceitaDePlanilha.Descrever(Modelo(), null, "4:5", 60000);
        r.Should().Contain("A4:D5").And.Contain("Tarefa").And.NotContain("PAINEL DE TAREFAS");
    }

    [Fact]
    public void SaidaGrande_ParaNoTetoEDizComoContinuar()
    {
        string r = ReceitaDePlanilha.Descrever(Modelo(), null, null, 1500);
        r.Should().Contain("PAROU AQUI");
        r.Length.Should().BeLessThan(1600);
    }

    [Fact]
    public async Task PelaFerramenta_LeSemPerguntar()
    {
        var tool = new DocTool(raizDeDados: Path.Combine(_pasta, "dados"));
        string args = JsonSerializer.Serialize(new { action = "layout", path = Modelo() });

        tool.Validar(args).Should().BeNull();
        tool.PedeConfirmacao(args).Should().BeFalse();
        (await tool.ExecuteAsync(args)).Should().StartWith("Formatação da aba \"Painel\"");
    }

    [Fact]
    public async Task Csv_DizQueNaoHaFormatacao()
    {
        string c = Path.Combine(_pasta, "a.csv");
        File.WriteAllText(c, "a;b\r\n1;2\r\n");
        var tool = new DocTool(raizDeDados: Path.Combine(_pasta, "dados"));
        (await tool.ExecuteAsync(JsonSerializer.Serialize(new { action = "layout", path = c }))).Should().Contain("não há formatação");
    }
}
