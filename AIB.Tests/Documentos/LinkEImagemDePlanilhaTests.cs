using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIB.Services.Documentos;
using AIB.Services.Tools;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// Link e imagem numa planilha. O modelo de rubrica que o usuário deu de exemplo (26/10/2026) tem
/// um logotipo no canto e uma faixa final que é um link; a ferramenta não fazia nenhum dos dois.
/// O link só sai para http, https e mailto, e a imagem não pode vir da pasta de dados do AIB.
/// </summary>
public class LinkEImagemDePlanilhaTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-link-" + Guid.NewGuid().ToString("N"));

    public LinkEImagemDePlanilhaTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private string Planilha()
    {
        string c = Path.Combine(_pasta, Guid.NewGuid().ToString("N") + ".xlsx");
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Painel").Cell("A1").Value = "Site";
        wb.AddWorksheet("Custos").Cell("A1").Value = "Conta";
        wb.SaveAs(c);
        return c;
    }

    private string Png(string? pasta = null, int largura = 100, int altura = 50)
    {
        string dir = pasta ?? _pasta;
        Directory.CreateDirectory(dir);
        string c = Path.Combine(dir, "logo.png");
        using var bmp = new System.Drawing.Bitmap(largura, altura);
        bmp.Save(c, System.Drawing.Imaging.ImageFormat.Png);
        return c;
    }

    private static JsonElement Ops(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Lista(object ops) => JsonSerializer.Serialize(ops);

    private static Gravacao Gravar(string c, string ops)
    {
        var g = EditorDePlanilha.Planejar(c, Ops(ops), null);
        g.Gravar();
        return g;
    }

    private static Action Planejar(string c, string ops) => () => EditorDePlanilha.Planejar(c, Ops(ops), null);

    [Fact]
    public void Link_ParaSite_ComCaraDeLink()
    {
        string c = Planilha();
        var g = Gravar(c, """[{"cell":"A2","value":"Abrir o portal","link":"https://exemplo.com.br/portal"}]""");

        g.Plano.Mudancas.Should().Contain(m => m.Onde == "Link em A2" && m.Depois == "https://exemplo.com.br/portal");
        using var wb = new XLWorkbook(c);
        var cel = wb.Worksheet("Painel").Cell("A2");
        cel.HasHyperlink.Should().BeTrue();
        cel.GetHyperlink().ExternalAddress.ToString().Should().Be("https://exemplo.com.br/portal");
        cel.Style.Font.Underline.Should().Be(XLFontUnderlineValues.Single);
    }

    [Fact]
    public void Link_ComEstilo_ManteOEstiloPedido()
    {
        string c = Planilha();
        Gravar(c, """[{"cell":"A2","value":"ABRIR","link":"https://exemplo.com.br","style":{"color":"branco","fill":"verde","bold":true}}]""");

        using var wb = new XLWorkbook(c);
        var cel = wb.Worksheet("Painel").Cell("A2");
        StyleHex(cel.Style.Font.FontColor).Should().Be("#FFFFFF");
        cel.Style.Font.Underline.Should().Be(XLFontUnderlineValues.None);
    }

    private static string? StyleHex(XLColor cor) => EstiloDePlanilha.HexDe(cor);

    [Fact]
    public void Link_ParaOutraAba_ETirar()
    {
        string c = Planilha();
        Gravar(c, """[{"cell":"A2","value":"Ver custos","link":"#Custos!A1"}]""");
        using (var wb = new XLWorkbook(c))
            wb.Worksheet("Painel").Cell("A2").GetHyperlink().InternalAddress.Should().Be("Custos!A1");

        Gravar(c, """[{"cell":"A2","link":null}]""");
        using (var wb = new XLWorkbook(c))
            wb.Worksheet("Painel").Cell("A2").HasHyperlink.Should().BeFalse();
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("C:\\Users\\x\\a.exe")]
    [InlineData("\\\\servidor\\pasta\\a.bat")]
    public void Link_ParaArquivoOuPrograma_Recusa(string alvo)
    {
        Planejar(Planilha(), Lista(new[] { new { cell = "A2", link = alvo } }))
            .Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("não entra");
    }

    [Fact]
    public void Link_ParaAbaQueNaoExiste_Recusa()
    {
        Planejar(Planilha(), """[{"cell":"A2","link":"#Vendas!A1"}]""")
            .Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("\"Vendas\"");
    }

    [Fact]
    public void Imagem_NoCanto_ComLarguraMantendoAProporcao()
    {
        string c = Planilha();
        string png = Png();
        var g = Gravar(c, Lista(new object[] { new { image = png, cell = "D1", width = 50 } }));

        g.Plano.Mudancas.Single().Depois.Should().Be("logo.png · 50×25 px");
        using var wb = new XLWorkbook(c);
        var pic = wb.Worksheet("Painel").Pictures.Single();
        pic.TopLeftCell.Address.ToString().Should().Be("D1");
        pic.Width.Should().Be(50);
        pic.Height.Should().Be(25);
    }

    [Fact]
    public void Imagem_SobreviveAOutraEdicao_ESeTira()
    {
        string c = Planilha();
        Gravar(c, Lista(new object[] { new { image = Png(), cell = "D1" } }));
        Gravar(c, """[{"cell":"A2","value":"outro"}]""");
        using (var wb = new XLWorkbook(c))
            wb.Worksheet("Painel").Pictures.Should().HaveCount(1);

        Gravar(c, """[{"image":"none","cell":"D1"}]""");
        using (var wb = new XLWorkbook(c))
            wb.Worksheet("Painel").Pictures.Should().BeEmpty();
    }

    [Fact]
    public void Imagem_Errada_DizOQueFalta()
    {
        string c = Planilha();
        string png = Png();
        Planejar(c, Lista(new object[] { new { image = png } })).Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("'cell'");
        Planejar(c, Lista(new object[] { new { image = "logo.png", cell = "A1" } })).Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("absoluto");
        Planejar(c, Lista(new object[] { new { image = Path.Combine(_pasta, "x.exe"), cell = "A1" } })).Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain(".png");
        Planejar(c, Lista(new object[] { new { image = Path.Combine(_pasta, "nao.png"), cell = "A1" } })).Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("não existe");
        Planejar(c, Lista(new object[] { new { image = png, cell = "A1", width = 2 } })).Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("8 a 4000");
    }

    [Fact]
    public void Imagem_DaPastaDeDadosDoAib_Recusa()
    {
        // A imagem é um segundo arquivo lido pelo pedido; da memória ou dos logs ela iria parar
        // num documento que o usuário repassa.
        string dados = Path.Combine(_pasta, "dados");
        string png = Png(Path.Combine(dados, "memory"));
        var tool = new DocTool(raizDeDados: dados);
        string args = JsonSerializer.Serialize(new { action = "edit", path = Planilha(), ops = new object[] { new { image = png, cell = "D1" } } });

        tool.Validar(args).Should().StartWith("ACESSO NEGADO").And.Contain("pasta de dados");
    }
}
