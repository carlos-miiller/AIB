using System;
using System.Collections.Generic;
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
/// O manual da doc (action='help'). Visto em 26/10/2026: para montar um painel numa planilha, a
/// Ellen abriu o código-fonte da formatação umas 15 vezes, atrás de nome de cor, atalho de
/// number_format e forma do 'conditional'. O manual responde isso; estes testes garantem que ele
/// não ensina nada que a ferramenta recuse — cada exemplo roda num arquivo de verdade.
/// </summary>
public class ManualDoDocTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-manual-" + Guid.NewGuid().ToString("N"));

    public ManualDoDocTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static ManualDoDoc.Exemplo Unico(ManualDoDoc.Uso uso) =>
        ManualDoDoc.Exemplos.Single(e => e.Exemplo.Uso == uso).Exemplo;

    /// <summary>A planilha do exemplo de 'create', com uma barra em C2:C6 para o clear_conditional ter o que tirar.</summary>
    private string Planilha()
    {
        string c = Path.Combine(_pasta, "base.xlsx");
        if (File.Exists(c)) return c;
        EditorDePlanilha.PlanejarCriacao(c, Json(Unico(ManualDoDoc.Uso.CriarPlanilha).Json)).Gravar();
        EditorDePlanilha.Planejar(c, Json("""[{"range":"C2:C6","conditional":{"bar":"azul"}}]"""), null).Gravar();
        return c;
    }

    private string Word()
    {
        string c = Path.Combine(_pasta, "base.docx");
        if (File.Exists(c)) return c;
        EditorDeDocx.PlanejarCriacao(c, Json(Unico(ManualDoDoc.Uso.CriarWord).Json)).Gravar();
        return c;
    }

    private string Copia(string origem)
    {
        string c = Path.Combine(_pasta, Guid.NewGuid().ToString("N") + Path.GetExtension(origem));
        File.Copy(origem, c);
        return c;
    }

    public static IEnumerable<object[]> ExemplosDeEdicao() =>
        ManualDoDoc.Exemplos
            .Where(e => e.Exemplo.Uso is ManualDoDoc.Uso.Ops or ManualDoDoc.Uso.OpsDoWord or ManualDoDoc.Uso.Consulta)
            .Select(e => new object[] { e.Assunto, e.Exemplo.Legenda });

    [Theory]
    [MemberData(nameof(ExemplosDeEdicao))]
    public async Task CadaExemploDoManual_FuncionaNumArquivoDeVerdade(string assunto, string legenda)
    {
        var e = ManualDoDoc.Exemplos.Single(x => x.Assunto == assunto && x.Exemplo.Legenda == legenda).Exemplo;

        switch (e.Uso)
        {
            case ManualDoDoc.Uso.Ops:
            {
                string c = Copia(Planilha());
                EditorDePlanilha.Planejar(c, Json(e.Json), null).Gravar();
                break;
            }
            case ManualDoDoc.Uso.OpsDoWord:
            {
                string c = Copia(Word());
                EditorDeDocx.Planejar(c, Json(e.Json), "Ellen").Gravar();
                break;
            }
            case ManualDoDoc.Uso.Consulta:
            {
                var tool = new DocTool(raizDeDados: Path.Combine(_pasta, "dados"));
                string args = JsonSerializer.Serialize(new { action = "query", path = Planilha(), sheet = "Vendas", query = Json(e.Json) });
                string r = await tool.ExecuteAsync(args);
                r.Should().NotStartWith("ERRO", because: legenda);
                break;
            }
        }
    }

    [Fact]
    public void Estilo_ListaTodasAsCoresEAtalhosQueOCodigoAceita()
    {
        string t = ManualDoDoc.Texto("estilo");
        foreach (var cor in EstiloDePlanilha.Cores.Keys) t.Should().Contain(cor);
        foreach (var atalho in EstiloDePlanilha.AtalhosDeFormato) t.Should().Contain($"'{atalho.Key}' = {atalho.Value}");
        foreach (var chave in EstiloDePlanilha.Chaves) t.Should().Contain(chave);
    }

    [Fact]
    public void Condicional_ListaEscalasEIcones()
    {
        string t = ManualDoDoc.Texto("condicional");
        foreach (var e in CondicionalDePlanilha.Escalas) t.Should().Contain(e);
        foreach (var i in CondicionalDePlanilha.Icones) t.Should().Contain(i);
    }

    [Fact]
    public void Layout_ExplicaCadaChave()
    {
        string t = ManualDoDoc.Texto("layout");
        foreach (var k in LayoutDePlanilha.Chaves) t.Should().Contain(k + ":");
        t.Should().Contain("validation:").And.Contain("indent");
    }

    [Fact]
    public void CadaBordaDoManual_EAceita()
    {
        // A lista de bordas do manual é escrita à mão; se o código deixar de aceitar uma, isto quebra.
        string c = Copia(Planilha());
        foreach (var b in EstiloDePlanilha.TiposDeBorda)
        {
            var ops = Json($$$"""[{"range":"A1:B2","style":{"border":"{{{b}}}"}}]""");
            var planejar = () => EditorDePlanilha.Planejar(c, ops, null);
            planejar.Should().NotThrow(b);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xyz")]
    public void SemAssuntoConhecido_DevolveOIndice(string? assunto)
    {
        string t = ManualDoDoc.Texto(assunto);
        foreach (var a in ManualDoDoc.Assuntos) t.Should().Contain("- " + a + ":");
        if (assunto == "xyz") t.Should().StartWith("Não há o assunto \"xyz\"");
    }

    [Theory]
    [InlineData("style", "## estilo")]
    [InlineData("Conditional", "## condicional")]
    [InlineData("freeze", "## abas")]
    [InlineData("query", "## consulta")]
    [InlineData("docx", "## word")]
    public void Apelidos_LevamAoAssunto(string pedido, string inicio)
    {
        ManualDoDoc.Texto(pedido).Should().StartWith(inicio);
    }

    [Fact]
    public void Tudo_TrazTodosOsAssuntos()
    {
        string t = ManualDoDoc.Texto("tudo");
        foreach (var a in ManualDoDoc.Assuntos) t.Should().Contain("## " + a + " —");
    }

    [Fact]
    public async Task Help_SemPath_NaoPerguntaEDevolveOManual()
    {
        var tool = new DocTool(raizDeDados: Path.Combine(_pasta, "dados"));
        string args = """{"action":"help","topic":"estilo"}""";

        tool.Validar(args).Should().BeNull();
        tool.PedeConfirmacao(args).Should().BeFalse();
        tool.PedeFalaDoUsuario(args).Should().BeFalse();
        (await tool.ExecuteAsync(args)).Should().StartWith("## estilo").And.Contain("azul escuro");
    }

    [Fact]
    public void ADescricao_ApontaParaOHelp()
    {
        var tool = new DocTool(raizDeDados: Path.Combine(_pasta, "dados"));
        tool.Description.Should().Contain("action='help'");
    }
}
