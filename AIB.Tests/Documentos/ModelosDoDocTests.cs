using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services.Documentos;
using AIB.Services.Tools;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// Os modelos de documento. Pedido do usuário, 26/10/2026, depois de comparar o painel que a
/// Ellen montou (uma grade de células iguais) com um modelo de rubrica bem acabado: dar a ela
/// referências — as que vêm com o AIB e as que o usuário põe em ~/.AIB/modelos — e deixar começar
/// um arquivo a partir delas.
/// </summary>
public class ModelosDoDocTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-modelos-" + Guid.NewGuid().ToString("N"));
    private string Dados => Path.Combine(_pasta, "dados");
    private string DoUsuario => Path.Combine(Dados, "modelos");
    private string DoPrograma => Path.Combine(_pasta, "programa");

    public ModelosDoDocTests()
    {
        Directory.CreateDirectory(DoUsuario);
        Directory.CreateDirectory(DoPrograma);
    }

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private DocTool Tool() => new(raizDeDados: Dados, modelosDoPrograma: DoPrograma);

    private static string Planilha(string caminho)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Painel");
        ws.Cell("B1").Value = "TÍTULO";
        ws.Cell("B1").Style.Font.Bold = true;
        ws.Column(2).Width = 30;
        ws.Cell("B2").Value = "valor";
        wb.SaveAs(caminho);
        return caminho;
    }

    private static string Hash(string c) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(c)));

    private static string RaizDoProjeto()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "AIBWindows"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void Lista_OsDoUsuarioPrimeiro_ComADescricaoDoTxt()
    {
        Planilha(Path.Combine(DoPrograma, "painel.xlsx"));
        File.WriteAllText(Path.Combine(DoPrograma, "painel.txt"), "Painel com indicadores.");
        Planilha(Path.Combine(DoUsuario, "rubrica.xlsx"));
        File.WriteAllText(Path.Combine(DoUsuario, "~$rubrica.xlsx"), "trava do Excel");
        File.WriteAllText(Path.Combine(DoUsuario, "notas.md"), "não é modelo");

        var lista = new ModelosDoDoc(DoPrograma, DoUsuario).Listar();

        lista.Select(m => m.Nome).Should().Equal("rubrica", "painel");
        lista[0].DoUsuario.Should().BeTrue();
        lista[1].Descricao.Should().Be("Painel com indicadores.");
    }

    [Fact]
    public async Task Models_SemPath_NaoPerguntaEDizComoUsar()
    {
        Planilha(Path.Combine(DoPrograma, "painel.xlsx"));
        var tool = Tool();
        string args = """{"action":"models"}""";

        tool.Validar(args).Should().BeNull();
        tool.PedeConfirmacao(args).Should().BeFalse();
        string r = await tool.ExecuteAsync(args);
        r.Should().Contain("painel (do AIB)").And.Contain(Path.Combine(DoPrograma, "painel.xlsx"))
            .And.Contain("action='layout'").And.Contain("from=").And.Contain(DoUsuario);
    }

    [Fact]
    public async Task CriarDoModelo_ComOps_UmCartaoSo_EOModeloNaoMuda()
    {
        string modelo = Planilha(Path.Combine(DoUsuario, "painel.xlsx"));
        string antes = Hash(modelo);
        string novo = Path.Combine(_pasta, "saida", "painel-outubro.xlsx");
        var tool = Tool();
        string args = JsonSerializer.Serialize(new
        {
            action = "create", path = novo, from = modelo,
            ops = new object[] { new { cell = "B1", value = "OUTUBRO" } }
        });

        tool.Validar(args).Should().BeNull();
        var cartao = tool.BuildConfirmationContext(args, 2)!;
        var plano = cartao.Plano!;
        cartao.Command.Should().StartWith("CRIAR ");
        plano.Criar.Should().BeTrue();
        plano.Titulo.Should().Be("Criar esta planilha a partir do modelo?");
        plano.Consequencia.Should().Contain("'painel.xlsx'").And.Contain("O modelo não muda");
        plano.Mudancas.Should().Contain(m => m.Onde == "B1" && m.Depois == "OUTUBRO");

        (await tool.ExecutarAutorizadoAsync(args, 2, cartao)).Should().StartWith("SUCESSO: planilha criada a partir do modelo 'painel.xlsx'");
        using (var wb = new XLWorkbook(novo))
        {
            var ws = wb.Worksheet("Painel");
            ws.Cell("B1").GetString().Should().Be("OUTUBRO");
            ws.Cell("B1").Style.Font.Bold.Should().BeTrue("o visual vem do modelo");
            ws.Column(2).Width.Should().Be(30);
        }
        Hash(modelo).Should().Be(antes);
    }

    [Fact]
    public async Task CriarDoModelo_Word_ECopiaExata()
    {
        string modelo = Path.Combine(DoPrograma, "ata.docx");
        EditorDeDocx.PlanejarCriacao(modelo, JsonDocument.Parse("""{"blocks":[{"style":"Heading1","text":"Ata"}]}""").RootElement).Gravar();
        string novo = Path.Combine(_pasta, "ata-hoje.docx");
        var tool = Tool();
        string args = JsonSerializer.Serialize(new { action = "create", path = novo, from = modelo });

        tool.Validar(args).Should().BeNull();
        var cartao = tool.BuildConfirmationContext(args, 2)!;
        cartao.Plano!.Titulo.Should().Be("Criar este documento a partir do modelo?");
        (await tool.ExecutarAutorizadoAsync(args, 2, cartao)).Should().StartWith("SUCESSO");
        Hash(novo).Should().Be(Hash(modelo));
    }

    [Theory]
    [InlineData("memoria", "ACESSO NEGADO")]
    [InlineData("conteudo", "OU 'content'")]
    [InlineData("extensao", "extensão do modelo")]
    [InlineData("nao-existe", "não existe")]
    [InlineData("word-com-ops", "sem 'ops'")]
    public void CriarDoModelo_Recusas(string caso, string trecho)
    {
        string modelo = Planilha(Path.Combine(DoUsuario, "painel.xlsx"));
        string novo = Path.Combine(_pasta, "novo.xlsx");
        object args = caso switch
        {
            "memoria" => new { action = "create", path = novo, from = Planilha(Path.Combine(Directory.CreateDirectory(Path.Combine(Dados, "memory")).FullName, "x.xlsx")) },
            "conteudo" => new { action = "create", path = novo, from = modelo, content = new { sheets = new[] { new { name = "A" } } } },
            "extensao" => new { action = "create", path = Path.Combine(_pasta, "novo.csv"), from = modelo },
            "nao-existe" => new { action = "create", path = novo, from = Path.Combine(DoUsuario, "nada.xlsx") },
            _ => new { action = "create", path = Path.Combine(_pasta, "a.docx"), from = Word(), ops = new object[] { new { replace = "a", with = "b" } } }
        };

        Tool().Validar(JsonSerializer.Serialize(args)).Should().Contain(trecho);

        string Word()
        {
            string c = Path.Combine(DoUsuario, "ata.docx");
            EditorDeDocx.PlanejarCriacao(c, JsonDocument.Parse("""{"blocks":[{"text":"a"}]}""").RootElement).Gravar();
            return c;
        }
    }

    [Fact]
    public void ModelosDoAib_AbremEEnsinam()
    {
        // Os que vêm com o AIB: cada planilha tem receita (action='layout') e serve de 'from'.
        string pasta = Path.Combine(RaizDoProjeto(), "AIBWindows", "modelos");
        var lista = new ModelosDoDoc(pasta, Path.Combine(_pasta, "vazio")).Listar();

        lista.Select(m => m.Nome).Should().Contain(new[] { "painel-de-indicadores", "rubrica-de-avaliacao", "ata-de-reuniao" });
        lista.Should().OnlyContain(m => m.Descricao != null, "cada modelo tem o .txt que o descreve");

        foreach (var m in lista.Where(m => m.Caminho.EndsWith(".xlsx")))
        {
            ReceitaDePlanilha.Descrever(m.Caminho, null, null, 60000).Should().Contain("\"merge\"", m.Nome);
            string novo = Path.Combine(_pasta, m.Nome + ".xlsx");
            EditorDePlanilha.Planejar(novo, JsonDocument.Parse("[]").RootElement, null, m.Caminho).Gravar();
            File.Exists(novo).Should().BeTrue();
        }
    }
}
