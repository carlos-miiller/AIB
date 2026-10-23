using System;
using System.IO;
using System.Text.Json;
using AIB.Services.Documentos;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// Testes de suporte a autofiltro do Excel e linhas ocultas (por filtro ou à mão).
/// Caso real: "como a Ellen vê uma planilha com filtro na primeira linha?".
/// </summary>
public sealed class FiltroDePlanilhaTests : IDisposable
{
    private readonly string _pastaTemp;

    public FiltroDePlanilhaTests()
    {
        _pastaTemp = Path.Combine(Path.GetTempPath(), "aib-doc-" + Guid.NewGuid());
        Directory.CreateDirectory(_pastaTemp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_pastaTemp, recursive: true); }
        catch { }
    }

    private string CriarPlanilhaComFiltroStatus(out int totalGeral, out int totalVisiveis)
    {
        string caminho = Path.Combine(_pastaTemp, "tarefas.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Tarefas");

        ws.Cell("A1").Value = "ID";
        ws.Cell("B1").Value = "Status";
        ws.Cell("C1").Value = "Horas";

        // Linha 2: Aberto (visível) - 10h
        ws.Cell("A2").Value = 1;
        ws.Cell("B2").Value = "Aberto";
        ws.Cell("C2").Value = 10;

        // Linha 3: Fechado (oculto pelo filtro) - 20h
        ws.Cell("A3").Value = 2;
        ws.Cell("B3").Value = "Fechado";
        ws.Cell("C3").Value = 20;

        // Linha 4: Aberto (visível) - 30h
        ws.Cell("A4").Value = 3;
        ws.Cell("B4").Value = "Aberto";
        ws.Cell("C4").Value = 30;

        // Linha 5: Cancelado (oculto pelo filtro) - 40h
        ws.Cell("A5").Value = 4;
        ws.Cell("B5").Value = "Cancelado";
        ws.Cell("C5").Value = 40;

        var af = ws.Range("A1:C5").SetAutoFilter();
        af.Column(2).AddFilter("Aberto");

        totalGeral = 10 + 20 + 30 + 40; // 100
        totalVisiveis = 10 + 30;         // 40

        wb.SaveAs(caminho);
        return caminho;
    }

    private string CriarPlanilhaSemFiltro()
    {
        string caminho = Path.Combine(_pastaTemp, "limpa.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Dados");

        ws.Cell("A1").Value = "Item";
        ws.Cell("B1").Value = "Valor";
        ws.Cell("A2").Value = "Notebook";
        ws.Cell("B2").Value = 1000;
        ws.Cell("A3").Value = "Mouse";
        ws.Cell("B3").Value = 50;

        wb.SaveAs(caminho);
        return caminho;
    }

    private string CriarPlanilhaComLinhaOcultaAMao()
    {
        string caminho = Path.Combine(_pastaTemp, "oculta_mao.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Vendas");

        ws.Cell("A1").Value = "Vendedor";
        ws.Cell("B1").Value = "Total";
        ws.Cell("A2").Value = "Carlos";
        ws.Cell("B2").Value = 500;
        ws.Cell("A3").Value = "Daniela";
        ws.Cell("B3").Value = 300;
        ws.Cell("A4").Value = "Eduardo";
        ws.Cell("B4").Value = 200;

        // Linha 3 oculta manualmente, sem autofiltro
        ws.Row(3).Hide();

        wb.SaveAs(caminho);
        return caminho;
    }

    private static string Q(Pasta p, string json, string? aba = null)
    {
        using var doc = JsonDocument.Parse(json);
        return ConsultaDePlanilha.Executar(p, aba, doc.RootElement, 20_000);
    }

    [Fact]
    public void Info_ComFiltroAtivo_MostraColunaValorEContagemDeOcultas()
    {
        string caminho = CriarPlanilhaComFiltroStatus(out _, out _);
        var pasta = LeitorDePlanilha.Abrir(caminho);

        string info = Apresentacao.Info(pasta, caminho);

        // Deve identificar o autofiltro ativo, a coluna e o valor filtrado, além das linhas ocultas
        info.Should().Contain("filtro ativo em A1:C5 (Status = Aberto)");
        info.Should().Contain("2 linhas ocultas");
    }

    [Fact]
    public void Read_ComLinhasOcultas_MarcaLinhasETrazRodape()
    {
        string caminho = CriarPlanilhaComFiltroStatus(out _, out _);
        var pasta = LeitorDePlanilha.Abrir(caminho);

        string leitura = Apresentacao.Ler(pasta, "Tarefas", "A1:C5", 10_000);

        // Linhas 3 e 5 estão ocultas pelo filtro Status = Aberto
        leitura.Should().Contain("3 (oculta)");
        leitura.Should().Contain("5 (oculta)");
        // Linhas 2 e 4 estão visíveis, não devem conter a marca
        leitura.Should().NotContain("2 (oculta)");
        leitura.Should().NotContain("4 (oculta)");
        // Rodapé informando a contagem de linhas ocultas no trecho
        leitura.Should().Contain("2 linhas deste trecho estão ocultas (filtro ou ocultadas à mão).");
    }

    [Fact]
    public void Query_SumSemVisible_SomaTudoEAvisaOcultas()
    {
        string caminho = CriarPlanilhaComFiltroStatus(out int totalGeral, out _);
        var pasta = LeitorDePlanilha.Abrir(caminho);

        string r = Q(pasta, """{"agg":[{"fn":"sum","col":"Horas"}]}""");

        // Sem visible, a soma deve abranger todas as linhas (100)
        r.Should().Contain($"sum(Horas)\t{totalGeral}");
        // E trazer o aviso explícito informando sobre as linhas ocultas e o filtro
        r.Should().Contain("Atenção: 2 linhas ocultas (filtro: Status = Aberto) entraram na conta. Para contar só as visíveis, como o SUBTOTAL do Excel, repita com visible:true.");
    }

    [Fact]
    public void Query_SumComVisibleTrue_SomaSoVisiveisSemAviso()
    {
        string caminho = CriarPlanilhaComFiltroStatus(out _, out int totalVisiveis);
        var pasta = LeitorDePlanilha.Abrir(caminho);

        string r = Q(pasta, """{"agg":[{"fn":"sum","col":"Horas"}],"visible":true}""");

        // Com visible: true, soma apenas as visíveis (40)
        r.Should().Contain($"sum(Horas)\t{totalVisiveis}");
        // Topo indica que apenas as visíveis foram consideradas e quantas ficaram fora
        r.Should().Contain("só as visíveis (2 ocultas fora)");
        // Não deve conter o aviso de atenção
        r.Should().NotContain("Atenção:");
    }

    [Fact]
    public void PlanilhaSemFiltro_InfoReadQuery_FuncionamSemMarcasNemAvisos()
    {
        string caminho = CriarPlanilhaSemFiltro();
        var pasta = LeitorDePlanilha.Abrir(caminho);

        string info = Apresentacao.Info(pasta, caminho);
        info.Should().NotContain("filtro ativo");
        info.Should().NotContain("linha oculta");
        info.Should().NotContain("linhas ocultas");

        string read = Apresentacao.Ler(pasta, "Dados", "A1:B3", 10_000);
        read.Should().NotContain("(oculta)");
        read.Should().NotContain("deste trecho estão ocultas");

        string query = Q(pasta, """{"agg":[{"fn":"sum","col":"Valor"}]}""");
        query.Should().Contain("sum(Valor)\t1.050");
        query.Should().NotContain("Atenção:");
        query.Should().NotContain("ocultas fora");
    }

    [Fact]
    public void LinhaOcultaAMao_SemFiltro_InfoDizLinhaOcultaSemFiltroAtivo()
    {
        string caminho = CriarPlanilhaComLinhaOcultaAMao();
        var pasta = LeitorDePlanilha.Abrir(caminho);

        string info = Apresentacao.Info(pasta, caminho);
        // Exatamente 1 linha oculta e sem filtro ativo
        info.Should().Contain("1 linha oculta");
        info.Should().NotContain("filtro ativo");

        string read = Apresentacao.Ler(pasta, "Vendas", "A1:B4", 10_000);
        read.Should().Contain("3 (oculta)");
        read.Should().Contain("1 linha deste trecho está oculta (filtro ou ocultadas à mão).");

        // Query sem visible soma tudo (500 + 300 + 200 = 1000) e avisa
        string qSemVisible = Q(pasta, """{"agg":[{"fn":"sum","col":"Total"}]}""");
        qSemVisible.Should().Contain("sum(Total)\t1.000");
        qSemVisible.Should().Contain("Atenção: 1 linha oculta (ocultadas à mão) entrou na conta. Para contar só as visíveis, como o SUBTOTAL do Excel, repita com visible:true.");

        // Query com visible:true soma só as visíveis (500 + 200 = 700)
        string qVisivel = Q(pasta, """{"agg":[{"fn":"sum","col":"Total"}],"visible":true}""");
        qVisivel.Should().Contain("sum(Total)\t700");
        qVisivel.Should().Contain("só as visíveis (1 oculta fora)");
        qVisivel.Should().NotContain("Atenção:");
    }

    [Fact]
    public void Buscar_CélulaEmLinhaOculta_MarcaOculta()
    {
        string caminho = CriarPlanilhaComFiltroStatus(out _, out _);
        var pasta = LeitorDePlanilha.Abrir(caminho);

        // "Fechado" está na linha 3 que está oculta
        string busca = Apresentacao.Buscar(pasta, "Fechado", 5000);
        busca.Should().Contain("linha 3 (oculta)");

        // "Aberto" está nas linhas 2 e 4 que são visíveis
        string buscaVisivel = Apresentacao.Buscar(pasta, "Aberto", 5000);
        buscaVisivel.Should().Contain("linha 2:");
        buscaVisivel.Should().Contain("linha 4:");
        buscaVisivel.Should().NotContain("linha 2 (oculta)");
        buscaVisivel.Should().NotContain("linha 4 (oculta)");
    }

    [Fact]
    public void FiltroPersonalizado_OperadorMaiorQue_DescritoCorretamente()
    {
        string caminho = Path.Combine(_pastaTemp, "custom.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Vendas");
            ws.Cell("A1").Value = "Produto";
            ws.Cell("B1").Value = "Valor";
            ws.Cell("A2").Value = "Teclado";
            ws.Cell("B2").Value = 50;
            ws.Cell("A3").Value = "Monitor";
            ws.Cell("B3").Value = 800;

            var af = ws.Range("A1:B3").SetAutoFilter();
            af.Column(2).GreaterThan(100);
            wb.SaveAs(caminho);
        }

        var pasta = LeitorDePlanilha.Abrir(caminho);
        string info = Apresentacao.Info(pasta, caminho);
        info.Should().Contain("filtro ativo em A1:B3 (Valor > 100)");
    }

    [Fact]
    public void FiltroComMultiplosValores_MostraValoresSeparadosPorVirgula()
    {
        string caminho = Path.Combine(_pastaTemp, "multi.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Demandas");
            ws.Cell("A1").Value = "Status";
            ws.Cell("A2").Value = "Aberto";
            ws.Cell("A3").Value = "Pendente";
            ws.Cell("A4").Value = "Concluído";

            var af = ws.Range("A1:A4").SetAutoFilter();
            af.Column(1).AddFilter("Aberto");
            af.Column(1).AddFilter("Pendente");
            wb.SaveAs(caminho);
        }

        var pasta = LeitorDePlanilha.Abrir(caminho);
        string info = Apresentacao.Info(pasta, caminho);
        info.Should().Contain("filtro ativo em A1:A4 (Status = Aberto, Pendente)");
    }

    [Fact]
    public void FiltroSemCriterio_MostraSemCriterio()
    {
        string caminho = Path.Combine(_pastaTemp, "semcriterio.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Geral");
            ws.Cell("A1").Value = "Nome";
            ws.Cell("A2").Value = "Ana";
            ws.Range("A1:A2").SetAutoFilter();
            wb.SaveAs(caminho);
        }

        var pasta = LeitorDePlanilha.Abrir(caminho);
        string info = Apresentacao.Info(pasta, caminho);
        info.Should().Contain("filtro ativo em A1:A2 (sem critério)");
        info.Should().NotContain("linhas ocultas");
    }
}
