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
/// O relatório de QA da Ellen sobre a ferramenta doc (23/10/2026): "{add_sheet: true, sheet:
/// "Resumo Executivo"}" deu "operação desconhecida"; o rename_sheet ela não conseguiu usar; não
/// havia como pôr a aba nova na frente; a query "não aceitava escolher a aba"; e 'rows' fora do
/// formato exato falhava sem dizer o que esperava.
/// </summary>
public class AbasDePlanilhaTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-abas-" + Guid.NewGuid().ToString("N"));

    public AbasDePlanilhaTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private string Planilha()
    {
        string c = Path.Combine(_pasta, "Vendas.xlsx");
        using var wb = new XLWorkbook();
        var dados = wb.AddWorksheet("Dados");
        dados.Cell("A1").Value = "Item";
        dados.Cell("A2").Value = "x";
        var outra = wb.AddWorksheet("Custos");
        outra.Cell("A1").Value = "Conta";
        outra.Cell("A2").Value = "luz";
        outra.Cell("A3").Value = "água";
        wb.SaveAs(c);
        return c;
    }

    private static JsonElement Ops(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string[] Abas(string c)
    {
        using var wb = new XLWorkbook(c);
        return wb.Worksheets.OrderBy(w => w.Position).Select(w => w.Name).ToArray();
    }

    [Theory]
    [InlineData("""[{ "add_sheet": "Resumo Executivo" }]""")]
    [InlineData("""[{ "add_sheet": true, "sheet": "Resumo Executivo" }]""")]
    [InlineData("""[{ "add_sheet": true, "name": "Resumo Executivo" }]""")]
    public void AddSheet_AceitaONomeNoValorOuEmSheet(string ops)
    {
        string c = Planilha();
        EditorDePlanilha.Planejar(c, Ops(ops), null).Gravar();
        Abas(c).Should().Equal("Dados", "Custos", "Resumo Executivo");
    }

    [Fact]
    public void AddSheet_ComPosicao_VaiParaAFrente()
    {
        string c = Planilha();
        EditorDePlanilha.Planejar(c, Ops("""[{ "add_sheet": "Resumo", "position": 1 }]"""), null).Gravar();
        Abas(c).Should().Equal("Resumo", "Dados", "Custos");
    }

    [Theory]
    [InlineData("""[{ "rename_sheet": "Custos", "to": "Despesas" }]""")]
    [InlineData("""[{ "rename_sheet": true, "sheet": "Custos", "to": "Despesas" }]""")]
    public void RenameSheet_AceitaAsDuasFormas(string ops)
    {
        string c = Planilha();
        EditorDePlanilha.Planejar(c, Ops(ops), null).Gravar();
        Abas(c).Should().Equal("Dados", "Despesas");
    }

    [Fact]
    public void AddERenameNoMesmoPedido_EMoveDepois()
    {
        // A sequência que a Ellen tentou: criar, renomear e pôr na frente.
        string c = Planilha();
        EditorDePlanilha.Planejar(c, Ops("""
            [{ "add_sheet": "Nova" },
             { "rename_sheet": true, "sheet": "Nova", "to": "Resumo Executivo" },
             { "move_sheet": "Resumo Executivo", "position": 1 }]
            """), null).Gravar();
        Abas(c).Should().Equal("Resumo Executivo", "Dados", "Custos");
    }

    [Theory]
    [InlineData("""[{ "move_sheet": "Custos" }]""", "position")]
    [InlineData("""[{ "move_sheet": "Custos", "position": 9 }]""", "de 1 a 2")]
    [InlineData("""[{ "add_sheet": "a:b" }]""", "não pode ter")]
    [InlineData("""[{ "add_sheet": "dados" }]""", "já existe")]
    [InlineData("""[{ "add_sheet": true }]""", "precisa do nome")]
    public void ErrosDeAba_DizemOQueFalta(string ops, string trecho)
    {
        string c = Planilha();
        var planejar = () => EditorDePlanilha.Planejar(c, Ops(ops), null);
        planejar.Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain(trecho);
    }

    [Fact]
    public void OperacaoDesconhecida_MostraAsChavesQueChegaram()
    {
        var planejar = () => EditorDePlanilha.Planejar(Planilha(), Ops("""[{ "sort_sheet": "A" }]"""), null);
        planejar.Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("chaves: sort_sheet").And.Contain("move_sheet");
    }

    [Fact]
    public void InsertRows_ListaSolta_EUmaLinha_ETextoJsonVale()
    {
        string c = Planilha();
        EditorDePlanilha.Planejar(c, Ops("""
            [{ "insert_rows": 2, "rows": ["y", 1] },
             { "insert_rows": 2, "rows": "[[\"z\", 2]]" }]
            """), "Dados").Gravar();

        using var wb = new XLWorkbook(c);
        var ws = wb.Worksheet("Dados");
        ws.Cell("A2").GetString().Should().Be("z");
        ws.Cell("A3").GetString().Should().Be("y");
        ws.Cell("B3").GetDouble().Should().Be(1);
    }

    [Fact]
    public void InsertRows_ForaDoFormato_DizOFormatoEOQueChegou()
    {
        var planejar = () => EditorDePlanilha.Planejar(Planilha(),
            Ops("""[{ "insert_rows": 2, "rows": [["a"], {"Item": "b"}] }]"""), null);

        planejar.Should().Throw<ErroDeDocumento>().Which.Message
            .Should().Contain("lista de linhas").And.Contain("[[").And.Contain("linha 2 chegou como um objeto");
    }

    [Fact]
    public async Task Query_ComSheetDentroDoObjeto_ConsultaAquelaAba()
    {
        string c = Planilha();
        var tool = new DocTool(raizDeDados: Path.Combine(_pasta, "dados"));
        string args = JsonSerializer.Serialize(new
        {
            action = "query",
            path = c,
            query = new { sheet = "Custos", agg = new[] { new { fn = "count", col = "A" } } }
        });

        tool.Validar(args).Should().BeNull();
        string r = await tool.ExecuteAsync(args);

        r.Should().NotStartWith("ERRO").And.Contain("Custos");
    }
}
