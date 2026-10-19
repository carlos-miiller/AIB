using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIB.Services.Documentos;
using ClosedXML.Excel;
using FluentAssertions;

namespace AIB.Tests
{
    /// <summary>
    /// A leitura e a edição de .xlsx da ferramenta doc. Os casos de leitura são os que a
    /// habilidade ler-planilha errava (teste de 19/10/2026 com planilhas de ensaio).
    /// </summary>
    public class PlanilhaTests : IDisposable
    {
        private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-doc-" + Guid.NewGuid().ToString("N"));

        public PlanilhaTests() => Directory.CreateDirectory(_pasta);

        public void Dispose()
        {
            try { Directory.Delete(_pasta, true); } catch { }
        }

        private string Caminho(string nome) => Path.Combine(_pasta, nome);

        /// <summary>Ramais: B2 vazia no meio da linha, texto com espaço na frente, data e fórmula.</summary>
        private string Ramais()
        {
            string c = Caminho("Ramais.xlsx");
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Ramais");
            ws.Cell("A1").Value = "Nome"; ws.Cell("B1").Value = "Ramal"; ws.Cell("C1").Value = "Setor"; ws.Cell("D1").Value = "Admissão";
            ws.Cell("A2").Value = " Ana Lima"; ws.Cell("C2").Value = "TI"; ws.Cell("D2").Value = new DateTime(2024, 10, 15);
            ws.Cell("D2").Style.NumberFormat.NumberFormatId = 14;
            ws.Cell("A3").Value = "Bruno"; ws.Cell("B3").Value = 2041; ws.Cell("C3").Value = "TI";
            ws.Cell("A5").Value = "Total"; ws.Cell("B5").FormulaA1 = "SUM(B2:B3)";
            var dez = wb.AddWorksheet("Aba dez");
            dez.Cell("A1").Value = "ABA DEZ";
            wb.SaveAs(c);
            return c;
        }

        // ─────────────────────────────────────────────────────────────────
        // Leitura
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ACelulaVazia_FicaNoLugarDela_EOTextoComEspaco_NaoViraXmlElement()
        {
            // A ler-planilha punha "TI" debaixo de "Ramal" (o XML não grava célula vazia) e
            // devolvia " Ana Lima" como "System.Xml.XmlElement".
            var aba = LeitorDePlanilha.Abrir(Ramais()).Aba(null)!;

            aba[2, 1].Texto.Should().Be(" Ana Lima");
            aba[2, 2].EstaVazia.Should().BeTrue();
            aba[2, 3].Texto.Should().Be("TI");
        }

        [Fact]
        public void AData_SaiComoNoBrasil_ENaoComoNumeroDeSerie()
        {
            // A ler-planilha mostrava 15/10/2024 como 45580; o ClosedXML, como "10/15/2024".
            var aba = LeitorDePlanilha.Abrir(Ramais()).Aba("Ramais")!;

            aba[2, 4].Texto.Should().Be("15/10/2024");
            aba[2, 4].Valor.Should().Be(new DateTime(2024, 10, 15));
        }

        [Fact]
        public void TodasAsAbas_NaOrdem_EAFormulaSaiComOValor()
        {
            var pasta = LeitorDePlanilha.Abrir(Ramais());

            pasta.Abas.Select(a => a.Nome).Should().Equal("Ramais", "Aba dez");
            var total = pasta.Aba("Ramais")![5, 2];
            total.Formula.Should().Be("SUM(B2:B3)");
            total.Texto.Should().Be("2041", "o arquivo do ClosedXML vem sem valor em cache, e aí a leitura calcula");
        }

        [Fact]
        public void ARead_NumeraLinhas_ELetraColunas_ECitaAFormula()
        {
            var saida = Apresentacao.Ler(LeitorDePlanilha.Abrir(Ramais()), "Ramais", "A1:D5", 10_000);

            saida.Should().Contain("#\tA\tB\tC\tD");
            saida.Should().Contain("2\t Ana Lima\t\tTI\t15/10/2024");
            saida.Should().Contain("5\tTotal\t2041 ⟨=SUM(B2:B3)⟩");
            saida.Should().NotContain("\n4\t", "linha vazia não ocupa a saída, mas a numeração continua a do Excel");
        }

        [Fact]
        public void OFind_AchaSemAcento_EDizACelula()
        {
            var saida = Apresentacao.Buscar(LeitorDePlanilha.Abrir(Ramais()), "admissao", 10_000);

            saida.Should().Contain("Ramais!D1: Admissão");
        }

        [Fact]
        public void OInfo_ListaAsAbas_ComOCabecalho()
        {
            string c = Ramais();
            var saida = Apresentacao.Info(LeitorDePlanilha.Abrir(c), c);

            saida.Should().Contain("\"Ramais\": A1:D5");
            saida.Should().Contain("cabeçalho (linha 1): A Nome | B Ramal | C Setor | D Admissão");
            saida.Should().Contain("'Aba dez'".Replace("'", "\""));
        }

        [Fact]
        public void AbaQueNaoExiste_DizQuaisExistem()
        {
            var acao = () => Apresentacao.Ler(LeitorDePlanilha.Abrir(Ramais()), "Junho", null, 1000);

            acao.Should().Throw<ErroDeDocumento>().WithMessage("*\"Ramais\", \"Aba dez\"*");
        }

        // ─────────────────────────────────────────────────────────────────
        // Edição
        // ─────────────────────────────────────────────────────────────────

        private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

        [Fact]
        public void OPlano_MostraAntesEDepois_EOEfeitoNoTotal()
        {
            // Mockup "Edição de documentos do AIB": o cartão diz o que muda e o total que mexe.
            string c = Ramais();
            var g = EditorDePlanilha.Planejar(c, Json("""[{"cell":"B3","value":2050},{"cell":"B2","value":2001}]"""), null);
            var p = g.Plano;

            p.Criar.Should().BeFalse();
            p.Titulo.Should().Be("Gravar 2 células nesta planilha?");
            p.Mudancas.Should().ContainEquivalentOf(new Mudanca(TipoDeMudanca.Valor, "Ramais!B3", "2041", "2050"));
            p.Mudancas.Should().ContainEquivalentOf(new Mudanca(TipoDeMudanca.Valor, "Ramais!B2", null, "2001"));
            p.Efeitos.Should().ContainSingle().Which.Should().Contain("(Total)").And.Contain("→ 4051");
            p.Copia.Should().Be(CopiasDeDocumento.Descricao(c));
            p.Impressao.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void PlanejarDuasVezes_DaOMesmoPlano()
        {
            // A execução refaz o plano e compara com o que o cartão mostrou.
            string c = Ramais();
            string ops = """[{"range":"E2:E3","value":"=B{row}*2"},{"delete_rows":"4"}]""";

            EditorDePlanilha.Planejar(c, Json(ops), null).Plano.EmTexto()
                .Should().Be(EditorDePlanilha.Planejar(c, Json(ops), null).Plano.EmTexto());
        }

        [Fact]
        public void AFaixaComFormula_TrocaORow_EResumeEmGrupo()
        {
            string c = Ramais();
            var p = EditorDePlanilha.Planejar(c, Json("""[{"range":"E2:E20","value":"=B{row}*2"}]"""), null).Plano;

            p.Contagens.Should().Contain(x => x.StartsWith("E2:E20 · 19 células · fórmula"));
            p.Mudancas.Should().Contain(m => m.Tipo == TipoDeMudanca.Grupo);
            p.Mudancas.Should().Contain(m => m.Onde == "Ramais!E3" && m.Depois == "=B3*2 → 4082");
            p.Mudancas.Should().Contain(m => m.Tipo == TipoDeMudanca.Mais && m.Onde.StartsWith("+ 16 células"));
        }

        [Fact]
        public void ApagarLinha_MostraALinhaInteira_EAvisaDoRef()
        {
            string c = Ramais();
            using (var wb = new XLWorkbook(c))
            {
                wb.Worksheet("Aba dez").Cell("B1").FormulaA1 = "Ramais!B3";
                wb.Save();
            }

            var p = EditorDePlanilha.Planejar(c, Json("""[{"delete_rows":"3:3"}]"""), "Ramais").Plano;

            p.Mudancas.Should().ContainSingle(m => m.Tipo == TipoDeMudanca.Sai).Which.Antes.Should().Be("Bruno | 2041 | TI");
            p.Avisos.Should().ContainSingle().Which.Should().Contain("#REF!");

            // E a fórmula não passa a ler a linha de baixo, como o ClosedXML faria sozinho.
            var g = EditorDePlanilha.Planejar(c, Json("""[{"delete_rows":"3:3"}]"""), "Ramais");
            g.Gravar();
            LeitorDePlanilha.Abrir(c).Aba("Aba dez")![1, 2].Formula.Should().Contain("#REF!");
            p.Titulo.Should().Be("Apagar 1 linha nesta planilha?");
        }

        [Fact]
        public void Gravar_MudaOArquivo_ESoOGravarToca_ODisco()
        {
            string c = Ramais();
            var antes = File.ReadAllBytes(c);
            var g = EditorDePlanilha.Planejar(c, Json("""[{"cell":"C3","value":"Financeiro"}]"""), null);

            File.ReadAllBytes(c).Should().Equal(antes, "planejar não grava");
            g.Gravar();

            LeitorDePlanilha.Abrir(c).Aba("Ramais")![3, 3].Texto.Should().Be("Financeiro");
            Directory.GetFiles(_pasta, "~aib-*").Should().BeEmpty("o temporário some");
        }

        [Fact]
        public void EdicaoMassiva_AvisaQueEPesadaELenta()
        {
            // Decisão do usuário (19/10/2026): o efeito nos totais é sempre calculado, e a
            // edição grande diz em destaque que é massiva e lenta.
            var p = EditorDePlanilha.Planejar(Ramais(), Json("""[{"range":"F1:F10001","value":1}]"""), null).Plano;

            p.Avisos.Should().Contain(a => a.StartsWith("EDIÇÃO PESADA E LENTA (10.001 células de uma vez)"));
            p.Consequencia.Should().StartWith("Edição massiva");
        }

        [Fact]
        public void PlanilhaComGrafico_NaoSeEdita()
        {
            // O ClosedXML abre, mas regrava SEM o gráfico: a edição de uma célula o apagaria.
            string c = Ramais();
            using (var z = System.IO.Compression.ZipFile.Open(c, System.IO.Compression.ZipArchiveMode.Update))
                z.CreateEntry("xl/charts/chart1.xml");

            var acao = () => EditorDePlanilha.Planejar(c, Json("""[{"cell":"A1","value":"x"}]"""), null);

            acao.Should().Throw<ErroDeDocumento>().WithMessage("*gráficos*apagaria*");
        }

        [Fact]
        public void Criar_PreviaComAsAbas_ETextoComIgual_ViraFormula()
        {
            string c = Caminho("novo.xlsx");
            var g = EditorDePlanilha.PlanejarCriacao(c, Json("""
                {"sheets":[{"name":"Chamados","rows":[["Nº","Horas"],[4821,3.5],[4807,1],["Total","=SUM(B2:B3)"]]},{"name":"Resumo","rows":[]}]}
                """));

            g.Plano.Criar.Should().BeTrue();
            g.Plano.Copia.Should().BeNull();
            g.Plano.Previa!.Partes.Should().Equal("Chamados", "Resumo");
            g.Plano.Previa.Linhas.Last().Should().Contain(x => x.Contains("=SUM(B2:B3) → 4,5"));

            g.Gravar();
            LeitorDePlanilha.Abrir(c).Aba("Chamados")![4, 2].Formula.Should().Be("SUM(B2:B3)");
        }

        [Fact]
        public void OperacaoDesconhecida_ExplicaAsQueExistem()
        {
            var acao = () => EditorDePlanilha.Planejar(Ramais(), Json("""[{"pintar":"A1"}]"""), null);

            acao.Should().Throw<ErroDeDocumento>().WithMessage("operação 1 desconhecida*cell, range*");
        }
    }
}
