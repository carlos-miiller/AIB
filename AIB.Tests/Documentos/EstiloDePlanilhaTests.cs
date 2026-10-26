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
/// Testes da nova formatação de planilhas da ferramenta doc (mockup "Formatação de planilhas do AIB", 19/10/2026).
/// Cobre estilo, largura de colunas, congelamento de painel, formatação condicional e SoAparencia.
/// </summary>
public class EstiloDePlanilhaTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-doc-" + Guid.NewGuid().ToString("N"));

    public EstiloDePlanilhaTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private string Caminho(string nome) => Path.Combine(_pasta, nome);

    private static JsonElement Ops(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void NegritoEFundoNumaFaixa_PlanoSoAparencia_GravaEReabreComEstilos()
    {
        string c = Caminho("Cores.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Dados");
            for (int col = 1; col <= 6; col++)
                ws.Cell(1, col).Value = "Item " + col;
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""range"": ""A1:F1"", ""style"": { ""bold"": true, ""fill"": ""amarelo claro"" } }]");
        var gravacao = EditorDePlanilha.Planejar(c, ops, null);
        var plano = gravacao.Plano;

        plano.SoAparencia.Should().BeTrue();
        plano.Mudancas.Should().HaveCount(1);
        var m = plano.Mudancas[0];
        m.Onde.Should().Contain("A1:F1");
        m.Depois.Should().Contain("negrito").And.Contain("amarelo claro");
        m.AmostraDepois.Should().NotBeNull();
        m.AmostraDepois!.Negrito.Should().BeTrue();
        m.AmostraDepois.Fundo.Should().Be("#FFF2CC");

        gravacao.Gravar();

        using (var wbReaberto = new XLWorkbook(c))
        {
            var ws = wbReaberto.Worksheet("Dados");
            for (int col = 1; col <= 6; col++)
            {
                var cel = ws.Cell(1, col);
                cel.Style.Font.Bold.Should().BeTrue();
                cel.GetString().Should().Be("Item " + col, "os valores devem permanecer intactos");
            }
        }
    }

    [Fact]
    public void NumberFormat_MoedaEm1180_AmostraDepoisTextoFormatado()
    {
        string c = Caminho("Moeda.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Planilha1");
            ws.Cell("A1").Value = 1180;
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""cell"": ""A1"", ""style"": { ""number_format"": ""moeda"" } }]");
        var gravacao = EditorDePlanilha.Planejar(c, ops, null);

        gravacao.Plano.Mudancas.Should().HaveCount(1);
        gravacao.Plano.Mudancas[0].AmostraDepois.Should().NotBeNull();
        gravacao.Plano.Mudancas[0].AmostraDepois!.Texto.Should().Be("R$ 1.180,00");
    }

    [Fact]
    public void ValueMaisStyle_NaMesmaOp_SoAparenciaFalse_ECabecalhosValoresEFormatacao()
    {
        string c = Caminho("Misto.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Planilha1");
            ws.Cell("F42").Value = 0;
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""cell"": ""F42"", ""value"": ""=SOMA(F2:F41)"", ""style"": { ""bold"": true } }]");
        var gravacao = EditorDePlanilha.Planejar(c, ops, null);
        var plano = gravacao.Plano;

        plano.SoAparencia.Should().BeFalse();
        var grupos = plano.Mudancas.Where(m => m.Tipo == TipoDeMudanca.Grupo).Select(m => m.Onde).ToList();
        grupos.Should().Contain("Valores");
        grupos.Should().Contain("Formatação");
    }

    [Fact]
    public void CorInvalida_LancaErroDeDocumentoComNaoExiste()
    {
        string c = Caminho("CorInvalida.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Planilha1").Cell("A1").Value = "Teste";
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""cell"": ""A1"", ""style"": { ""fill"": ""turquesa-abacate"" } }]");
        var act = () => EditorDePlanilha.Planejar(c, ops, null);

        act.Should().Throw<ErroDeDocumento>().WithMessage("*não existe*");
    }

    [Fact]
    public void ChaveDeStyleDesconhecida_LancaErroDeDocumento()
    {
        string c = Caminho("ChaveDesconhecida.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Planilha1").Cell("A1").Value = "Teste";
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""cell"": ""A1"", ""style"": { ""sombra"": true } }]");
        var act = () => EditorDePlanilha.Planejar(c, ops, null);

        act.Should().Throw<ErroDeDocumento>().WithMessage("*desconhecida*");
    }

    [Fact]
    public void ColumnsAuto_ComTextoDe100Caracteres_Largura60EWrap_EDepoisMencionaQuebra()
    {
        string c = Caminho("Colunas.xlsx");
        string textoLongo = new string('A', 100);
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Planilha1");
            ws.Cell("A1").Value = textoLongo;
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""columns"": ""A"", ""width"": ""auto"" }]");
        var gravacao = EditorDePlanilha.Planejar(c, ops, null);
        var m = gravacao.Plano.Mudancas.First();

        m.Depois.Should().Contain("60,0");
        m.Depois.Should().Contain("quebra");

        gravacao.Gravar();

        using (var wbReaberto = new XLWorkbook(c))
        {
            var ws = wbReaberto.Worksheet("Planilha1");
            ws.Column("A").Width.Should().Be(60);
            ws.Column("A").Style.Alignment.WrapText.Should().BeTrue();
        }
    }

    [Fact]
    public void Freeze_B2_SheetViewCongeladoEm1LinhaE1Coluna_EAntesNadaCongelado()
    {
        string c = Caminho("Freeze.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Planilha1").Cell("A1").Value = "Cabeçalho";
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""freeze"": ""B2"" }]");
        var gravacao = EditorDePlanilha.Planejar(c, ops, null);
        var m = gravacao.Plano.Mudancas.First();

        m.Antes.Should().Be("nada congelado");
        m.Depois.Should().Be("linha 1 e coluna A");

        gravacao.Gravar();

        using (var wbReaberto = new XLWorkbook(c))
        {
            var ws = wbReaberto.Worksheet("Planilha1");
            ws.SheetView.SplitRow.Should().Be(1);
            ws.SheetView.SplitColumn.Should().Be(1);
        }
    }

    /// <summary>
    /// Caso real (19/10/2026, inventario-sistemas.xlsx): a aba tinha sido salva rolada até a
    /// coluna L (<c>sheetView topLeftCell="L1"</c>). O <c>freeze: "B2"</c> trocou o painel e
    /// manteve esse canto; no Excel, o painel congelado passou a ser a coluna L, e o usuário
    /// perguntou se a Ellen tinha ocultado as colunas A a K.
    /// </summary>
    [Fact]
    public void Freeze_EmAbaSalvaRolada_VoltaOCantoParaA1()
    {
        string c = Caminho("FreezeRolada.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Inventario");
            ws.Cell("A1").Value = "Sistema";
            ws.SheetView.TopLeftCellAddress = ws.Cell("L1").Address;
            wb.SaveAs(c);
        }

        EditorDePlanilha.Planejar(c, Ops(@"[{ ""freeze"": ""B2"" }]"), null).Gravar();

        using var reaberto = new XLWorkbook(c);
        var v = reaberto.Worksheet("Inventario").SheetView;
        v.SplitColumn.Should().Be(1);
        // A1 não é gravado (é o padrão do Excel); relido, vem como endereço vazio.
        v.TopLeftCellAddress.ColumnNumber.Should().BeLessThanOrEqualTo(1);
        v.TopLeftCellAddress.RowNumber.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Conditional_WhenMenorQueZero_FaixaCom3Negativos_DepoisContemHojeAtinge3Celulas_EReabreComRegra()
    {
        string c = Caminho("Conditional.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Planilha1");
            ws.Cell("C1").Value = 10;
            ws.Cell("C2").Value = -5;
            ws.Cell("C3").Value = 0;
            ws.Cell("C4").Value = -12;
            ws.Cell("C5").Value = -1;
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""range"": ""C1:C5"", ""conditional"": { ""when"": ""<"", ""value"": 0, ""style"": { ""color"": ""vermelho"" } } }]");
        var gravacao = EditorDePlanilha.Planejar(c, ops, null);
        var m = gravacao.Plano.Mudancas.First();

        m.Depois.Should().Contain("hoje atinge 3 células");

        gravacao.Gravar();

        using (var wbReaberto = new XLWorkbook(c))
        {
            var ws = wbReaberto.Worksheet("Planilha1");
            ws.ConditionalFormats.Should().HaveCount(1);
        }
    }

    [Fact]
    public void Scale_AmostraDepoisEscalaCom2Ou3Cores()
    {
        string c = Caminho("Scale.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Planilha1");
            for (int i = 1; i <= 5; i++) ws.Cell(i, 1).Value = i * 10;
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""range"": ""A1:A5"", ""conditional"": { ""scale"": ""verde-amarelo-vermelho"" } }]");
        var gravacao = EditorDePlanilha.Planejar(c, ops, null);
        var m = gravacao.Plano.Mudancas.First();

        m.AmostraDepois.Should().NotBeNull();
        m.AmostraDepois!.Escala.Should().NotBeNull();
        m.AmostraDepois.Escala.Should().HaveCount(3);
    }

    [Fact]
    public void Icons_Setas_GravaIconSet()
    {
        string c = Caminho("Icons.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Planilha1");
            for (int i = 1; i <= 5; i++) ws.Cell(i, 1).Value = i;
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""range"": ""A1:A5"", ""conditional"": { ""icons"": ""setas"" } }]");
        var gravacao = EditorDePlanilha.Planejar(c, ops, null);
        gravacao.Gravar();

        using (var wbReaberto = new XLWorkbook(c))
        {
            var ws = wbReaberto.Worksheet("Planilha1");
            ws.ConditionalFormats.Should().HaveCount(1);
            ws.ConditionalFormats.First().ConditionalFormatType.Should().Be(XLConditionalFormatType.IconSet);
        }
    }

    [Fact]
    public void ClearConditional_RemoveRegraExistenteEGerarLinhaSai_EFaixaSemRegraDaErro()
    {
        string c = Caminho("ClearConditional.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Planilha1");
            for (int i = 1; i <= 10; i++) ws.Cell(i, 4).Value = i;
            ws.Range("D2:D10").AddConditionalFormat().DataBar(XLColor.Blue).LowestValue().HighestValue();
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""clear_conditional"": ""D2:D10"" }]");
        var gravacao = EditorDePlanilha.Planejar(c, ops, null);

        var sai = gravacao.Plano.Mudancas.FirstOrDefault(m => m.Tipo == TipoDeMudanca.Sai);
        sai.Should().NotBeNull();
        sai!.Onde.Should().Contain("D2:D10");
        sai.Antes.Should().Contain("barra de dados");

        gravacao.Gravar();

        using (var wbReaberto = new XLWorkbook(c))
        {
            var ws = wbReaberto.Worksheet("Planilha1");
            ws.ConditionalFormats.Should().BeEmpty();
        }

        // Tentar apagar onde não há regra
        var opsSemRegra = Ops(@"[{ ""clear_conditional"": ""D2:D10"" }]");
        var act = () => EditorDePlanilha.Planejar(c, opsSemRegra, null);
        act.Should().Throw<ErroDeDocumento>().WithMessage("*nenhuma regra*");
    }

    [Fact]
    public void CsvComStyle_LancaErroDeDocumento_CsvNaoGuardaFormatacao()
    {
        string c = Caminho("Tabela.csv");
        File.WriteAllText(c, "Nome;Idade\r\nAna;25\r\n");

        var ops = Ops(@"[{ ""cell"": ""A1"", ""style"": { ""bold"": true } }]");
        var act = () => EditorDePlanilha.Planejar(c, ops, null);

        act.Should().Throw<ErroDeDocumento>().WithMessage("*CSV não guarda formatação*");
    }

    [Fact]
    public void EmTextoDeterministico_ParaEditSoDeEstilo()
    {
        string c = Caminho("Deterministico.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Planilha1");
            ws.Cell("A1").Value = "Produto";
            ws.Cell("B1").Value = "Preço";
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""range"": ""A1:B1"", ""style"": { ""bold"": true, ""fill"": ""amarelo claro"", ""align"": ""center"" } }]");
        var p1 = EditorDePlanilha.Planejar(c, ops, null).Plano.EmTexto();
        var p2 = EditorDePlanilha.Planejar(c, ops, null).Plano.EmTexto();

        p1.Should().Be(p2);
    }

    [Fact]
    public void SoAparencia_NaoCalculaEfeitos_MesmoComFormulaSobreFaixaFormatada()
    {
        string c = Caminho("EfeitosSoAparencia.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Planilha1");
            ws.Cell("A1").Value = 10;
            ws.Cell("A2").Value = 20;
            ws.Cell("A3").FormulaA1 = "SUM(A1:A2)";
            wb.SaveAs(c);
        }

        var ops = Ops(@"[{ ""range"": ""A1:A2"", ""style"": { ""bold"": true } }]");
        var gravacao = EditorDePlanilha.Planejar(c, ops, null);

        gravacao.Plano.SoAparencia.Should().BeTrue();
        gravacao.Plano.Efeitos.Should().BeEmpty();
    }

    [Fact]
    public void DuasRegrasNaMesmaFaixa_NoMesmoPedido_FicamAsDuas()
    {
        // 26/10/2026: a Ellen mandou "verde se < 0" e "vermelho se > 0" em D15:D18 num edit só; a
        // segunda apagou a primeira, e ela precisou de outro pedido com clear_conditional.
        string c = Caminho("DuasRegras.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Dados");
            for (int r = 1; r <= 4; r++) ws.Cell(r, 1).Value = r * 500 - 1000;
            wb.SaveAs(c);
        }

        EditorDePlanilha.Planejar(c, Ops("""
            [{"range":"A1:A4","conditional":{"when":"<","value":0,"style":{"fill":"verde claro"}}},
             {"range":"A1:A4","conditional":{"when":">","value":0,"style":{"fill":"vermelho claro"}}}]
            """), null).Gravar();

        using var lido = new XLWorkbook(c);
        lido.Worksheet("Dados").ConditionalFormats.Should().HaveCount(2);
    }

    [Fact]
    public void RegraNova_AindaSubstituiARegraQueOArquivoJaTinha()
    {
        string c = Caminho("Substitui.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Dados");
            for (int r = 1; r <= 4; r++) ws.Cell(r, 1).Value = r;
            wb.SaveAs(c);
        }
        EditorDePlanilha.Planejar(c, Ops("""[{"range":"A1:A4","conditional":{"bar":"azul"}}]"""), null).Gravar();
        EditorDePlanilha.Planejar(c, Ops("""[{"range":"A1:A4","conditional":{"scale":"verde-vermelho"}}]"""), null).Gravar();

        using var lido = new XLWorkbook(c);
        lido.Worksheet("Dados").ConditionalFormats.Should().ContainSingle()
            .Which.ConditionalFormatType.Should().Be(XLConditionalFormatType.ColorScale);
    }
}
