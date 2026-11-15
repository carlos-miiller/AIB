using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIB.Services.Documentos;
using AIB.Services.Tools;
using AIB.Views;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Planilha nova já formatada num cartão só: 'content' e 'ops' no mesmo create. Pedido do
    /// usuário (11/11/2026): a Ellen criava a planilha e depois a formatava em outras chamadas
    /// (dados, aba nova, cores), e cada uma abria o seu cartão. Antes, as 'ops' de um create com
    /// 'content' eram ignoradas sem aviso.
    /// </summary>
    public class CriarComOpsTests : IDisposable
    {
        private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-criar-ops-" + Guid.NewGuid().ToString("N"));
        private readonly DocTool _tool;

        public CriarComOpsTests()
        {
            Directory.CreateDirectory(_pasta);
            _tool = new DocTool(Path.Combine(_pasta, ".AIB"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_pasta, true); } catch { }
        }

        private string Pedido(string arquivo) => JsonSerializer.Serialize(new
        {
            action = "create",
            path = Path.Combine(_pasta, arquivo),
            content = new { sheets = new[] { new { name = "Custos", rows = new[] { new object[] { "Item", "Valor" }, new object[] { "Aluguel", 3200 }, new object[] { "Luz", 410.9 } } } } },
            ops = new object[]
            {
                new { range = "A1:B1", style = new { fill = "azul escuro", color = "branco" } },
                new { range = "B2:B3", style = new { number_format = "moeda" } },
                new { add_sheet = "Resumo" },
                new { sheet = "Resumo", cell = "A1", value = "=SUM(Custos!B2:B3)" },
                new { sheet = "Custos", freeze = "A2" }
            }
        });

        [Fact]
        public async Task CreateComContentEOps_UmCartao_EAPlanilhaNasceFormatada()
        {
            string args = Pedido("custos.xlsx");
            _tool.Validar(args).Should().BeNull();

            var ctx = _tool.BuildConfirmationContext(args, 3)!;
            ctx.Command.Should().StartWith("CRIAR ");
            ctx.Plano!.Criar.Should().BeTrue();
            ctx.Plano.Titulo.Should().Be("Criar esta planilha?");
            ctx.Plano.Previa.Should().NotBeNull("o cartão mostra a planilha nova");
            ctx.Plano.Previa!.Partes.Should().Equal("Custos", "Resumo");
            ctx.Plano.Mudancas.Should().NotBeEmpty("e o que as mudanças fazem nela");
            ctx.Plano.Resumo.Should().StartWith("1 aba(s) · 3 linhas");

            string r = await _tool.ExecutarAutorizadoAsync(args, 3, ctx);
            r.Should().StartWith("SUCESSO: planilha criada");

            using var wb = new XLWorkbook(Path.Combine(_pasta, "custos.xlsx"));
            var ws = wb.Worksheet("Custos");
            ws.Cell("A1").Style.Fill.BackgroundColor.Color.ToArgb().Should().NotBe(XLColor.NoColor.Color.ToArgb());
            ws.Cell("B2").Style.NumberFormat.Format.Should().Contain("R$");
            ws.SheetView.SplitRow.Should().Be(1);
            wb.Worksheet("Resumo").Cell("A1").Value.GetNumber().Should().BeApproximately(3610.9, 0.001);
        }

        [Fact]
        public void CreateDeWordComOps_ERecusado_EmVezDeSumirComAsOps()
        {
            string args = JsonSerializer.Serialize(new
            {
                action = "create",
                path = Path.Combine(_pasta, "proposta.docx"),
                content = new { blocks = new[] { new { text = "Oi" } } },
                ops = new[] { new { replace = "Oi", with = "Olá" } }
            });

            _tool.Validar(args).Should().StartWith("ERRO: Word novo não aceita 'ops'");
        }

        [Fact]
        public void NoCartao_AParecemAsMudancasEAPrevia()
        {
            var ctx = _tool.BuildConfirmationContext(Pedido("cartao.xlsx"), 3)!;

            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var card = new ConfirmCardView();
                card.Preencher(ctx);

                var tabela = (Grid)card.FindName("PlanoTabela");
                var textos = tabela.Children.OfType<UIElement>()
                    .SelectMany(Textos).ToList();

                textos.Should().Contain("ONDE", "a tabela das mudanças");
                textos.Should().Contain(t => t.StartsWith("Abas: Custos"), "a prévia da planilha nova, abaixo");
            });
        }

        private static System.Collections.Generic.IEnumerable<string> Textos(UIElement e)
        {
            if (e is TextBlock t) yield return t.Text;
            if (e is Panel p)
                foreach (UIElement c in p.Children)
                    foreach (string s in Textos(c)) yield return s;
            if (e is Border b && b.Child is UIElement filho)
                foreach (string s in Textos(filho)) yield return s;
        }

        [Fact]
        public void Manual_VariosAssuntosNumaChamada()
        {
            string t = ManualDoDoc.Texto("estilo, abas");
            t.Should().StartWith("## estilo").And.Contain("## abas —");

            ManualDoDoc.Texto("estilo,xyz").Should().StartWith("Não há o(s) assunto(s) \"xyz\"").And.Contain("## estilo —");
            ManualDoDoc.Texto("estilo").Should().Contain("topic='estilo,abas,layout'");
        }

        [Fact]
        public void Manual_PedeParaJuntarAsMudancas_EOExemploDeCriarComOpsFunciona()
        {
            ManualDoDoc.Texto(null).Should().Contain("Junte numa chamada só TUDO o que muda num arquivo");
            ManualDoDoc.Texto("criar").Should().Contain("no MESMO create");

            var e = ManualDoDoc.Exemplos.Single(x => x.Exemplo.Uso == ManualDoDoc.Uso.CriarComOps).Exemplo;
            using var doc = JsonDocument.Parse(e.Json);
            string c = Path.Combine(_pasta, "exemplo.xlsx");
            EditorDePlanilha.PlanejarCriacao(c, doc.RootElement.GetProperty("content"), doc.RootElement.GetProperty("ops")).Gravar();

            using var wb = new XLWorkbook(c);
            wb.Worksheet("Custos").SheetView.SplitRow.Should().Be(1);
        }
    }
}
