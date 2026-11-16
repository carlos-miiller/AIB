using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Documentos;
using AIB.Services.Tools;
using ClosedXML.Excel;
using FluentAssertions;

namespace AIB.Tests
{
    /// <summary>
    /// O portão da ferramenta doc (pedido do usuário, 19/10/2026): ler não pergunta, gravar
    /// sempre pergunta, e o que se grava é exatamente o que o cartão mostrou.
    /// </summary>
    public class DocToolTests : IDisposable
    {
        private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-doc-" + Guid.NewGuid().ToString("N"));
        private readonly string _dados;
        private readonly DocTool _tool;

        public DocToolTests()
        {
            Directory.CreateDirectory(_pasta);
            _dados = Path.Combine(_pasta, ".AIB");
            Directory.CreateDirectory(_dados);
            _tool = new DocTool(_dados);
        }

        public void Dispose()
        {
            try { Directory.Delete(_pasta, true); } catch { }
        }

        private string Planilha(string nome = "Custos.xlsx", string? pasta = null)
        {
            string c = Path.Combine(pasta ?? _pasta, nome);
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Outubro");
            ws.Cell("A1").Value = "Item"; ws.Cell("B1").Value = "Valor";
            ws.Cell("A2").Value = "VipPhone"; ws.Cell("B2").Value = 1180;
            wb.SaveAs(c);
            return c;
        }

        private static string Args(object o) => JsonSerializer.Serialize(o);

        private static string Editar(string caminho, string celula = "B2", object? valor = null) =>
            Args(new { action = "edit", path = caminho, ops = new[] { new { cell = celula, value = valor ?? 1290 } } });

        [Fact]
        public void Ler_NaoPergunta_EGravar_SemprePergunta()
        {
            string c = Planilha();

            _tool.PedeConfirmacao(Args(new { action = "info", path = c })).Should().BeFalse();
            _tool.PedeConfirmacao(Editar(c)).Should().BeTrue();
            ((ITool)_tool).PedeFalaDoUsuario(Editar(c)).Should().BeTrue();
        }

        [Fact]
        public void OCartao_LevaOPlano_ESemSempre()
        {
            string c = Planilha();
            var ctx = _tool.BuildConfirmationContext(Editar(c), 3)!;

            ctx.Command.Should().Be("EDITAR " + c);
            ctx.Plano.Should().NotBeNull();
            ctx.Plano!.Mudancas.Should().ContainSingle(m => m.Onde == "B2" && m.Antes == "1180" && m.Depois == "1290");
            ctx.SemSempre.Should().BeTrue("cada edição de documento é uma pergunta");
            ctx.ScriptBody.Should().Contain("B2: 1180 → 1290");
        }

        [Fact]
        public async Task SemCartao_NadaSeGrava()
        {
            string c = Planilha();
            var antes = File.ReadAllBytes(c);

            string r = await _tool.ExecutarAutorizadoAsync(Editar(c), 3, null);

            r.Should().StartWith("ACESSO NEGADO");
            File.ReadAllBytes(c).Should().Equal(antes);
        }

        [Fact]
        public async Task ComOCartao_Grava_EGuardaACopia()
        {
            string c = Planilha();
            var ctx = _tool.BuildConfirmationContext(Editar(c), 3);

            string r = await _tool.ExecutarAutorizadoAsync(Editar(c), 3, ctx);

            var copias = _tool.Copias.Copias(c);
            copias.Should().ContainSingle();
            string copia = copias[0];
            copia.Should().StartWith(Path.Combine(_dados, "doc-backups"));

            r.Should().StartWith("SUCESSO").And.Contain(copia);
            LeitorDePlanilha.Abrir(c).Aba(null)![2, 2].Texto.Should().Be("1290");
            LeitorDePlanilha.Abrir(copia).Aba(null)![2, 2].Texto.Should().Be("1180");
        }

        [Fact]
        public async Task SeOArquivoMudouDepoisDaPergunta_NadaSeGrava()
        {
            // O usuário autorizou ESTE antes e depois. Salvo no Excel entre a pergunta e o
            // "Permitir", o arquivo já é outro.
            string c = Planilha();
            var ctx = _tool.BuildConfirmationContext(Editar(c), 3);
            using (var wb = new XLWorkbook(c))
            {
                wb.Worksheet(1).Cell("A2").Value = "Outro";
                wb.Save();
            }

            string r = await _tool.ExecutarAutorizadoAsync(Editar(c), 3, ctx);

            r.Should().StartWith("ERRO").And.Contain("mudou depois da pergunta");
            LeitorDePlanilha.Abrir(c).Aba(null)![2, 2].Texto.Should().Be("1180");
            _tool.Copias.Copias(c).Should().BeEmpty();
        }

        [Fact]
        public async Task DepoisDeGravar_DisparaOEventoGravou()
        {
            string c = Planilha();
            var ctx = _tool.BuildConfirmationContext(Editar(c), 3);
            DocTool.DocumentoGravado? recebido = null;
            Action<DocTool.DocumentoGravado> tratador = g => recebido = g;
            DocTool.Gravou += tratador;

            try
            {
                await _tool.ExecutarAutorizadoAsync(Editar(c), 3, ctx);

                recebido.Should().NotBeNull();
                recebido!.Arquivo.Should().Be(c);
                recebido.Copia.Should().NotBeNull();
                File.Exists(recebido.Copia).Should().BeTrue();
                recebido.Impressao.Should().Be(Gravacao.Impressao(c));
                recebido.Criado.Should().BeFalse();
            }
            finally
            {
                DocTool.Gravou -= tratador;
            }
        }

        [Fact]
        public void NaPastaDeDadosDoAIB_NaoGrava()
        {
            string c = Planilha("memoria.xlsx", _dados);

            _tool.Validar(Editar(c)).Should().StartWith("ACESSO NEGADO");
        }

        [Fact]
        public void AbertoNoExcel_RecusaAntesDoCartao()
        {
            // O Excel abre com escrita exclusiva e deixa os outros só lerem.
            string c = Planilha();
            using var excel = new FileStream(c, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            _tool.Validar(Editar(c)).Should().Contain("aberto em outro programa");
        }

        [Fact]
        public async Task AbertoNoExcel_AindaSeLe()
        {
            string c = Planilha();
            using var excel = new FileStream(c, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            string r = await _tool.ExecuteAsync(Args(new { action = "info", path = c }));

            r.Should().Contain("\"Outubro\": A1:B2");
        }

        [Fact]
        public async Task OFormato_VemDoConteudo_ENaoDaExtensao()
        {
            // Um .xls que é xlsx por dentro dava erro genérico na ler-planilha.
            string c = Path.ChangeExtension(Planilha("enganado.xlsx"), ".xls");
            File.Move(Path.ChangeExtension(c, ".xlsx"), c);

            string r = await _tool.ExecuteAsync(Args(new { action = "read", path = c }));

            r.Should().Contain("2\tVipPhone\t1180");
        }

        [Fact]
        public void NoNivel1_GravarECortadoPeloPiso()
        {
            string c = Planilha();
            var ctx = _tool.BuildConfirmationContext(Editar(c), 1)!;

            _tool.PisoTipado(ctx, 1).Should().NotBeNull();
            _tool.PisoTipado(ctx, 2).Should().BeNull();
        }

        [Fact]
        public void OpCelulaInvalida_RecusaAntesDoCartao()
        {
            _tool.Validar(Editar(Planilha(), "ZZZZ9")).Should().StartWith("ERRO");
        }

        [Fact]
        public async Task ORead_MandaPlanilhaParaADoc()
        {
            // Lida como texto, a planilha virava uma página de lixo binário paga em prefill.
            string c = Planilha();

            string r = await new ReadFileTool().ExecuteAsync(Args(new { path = c }));

            r.Should().StartWith("ERRO").And.Contain("'doc'");
        }

        // ─────────────────────────────────────────────────────────────────
        // Arquivo travado pelo Excel — a janela de escolha (pedido do usuário, 19/10/2026).
        // AoTravar é estático: estes testes ficam nesta classe, que o xUnit roda em série.
        // ─────────────────────────────────────────────────────────────────

        private async Task<(string Resultado, int Perguntas)> GravarTravado(string c, Func<int, DocTool.NaTrava> escolha,
                                                                             Action<int>? depois = null)
        {
            int perguntas = 0;
            DocTool.AoTravar = _ => { perguntas++; var e = escolha(perguntas); depois?.Invoke(perguntas); return Task.FromResult(e); };
            try
            {
                var ctx = _tool.BuildConfirmationContext(Editar(c), 3);
                ctx.Should().NotBeNull("com a janela ligada, o travado não é recusado antes do cartão");
                return (await _tool.ExecutarAutorizadoAsync(Editar(c), 3, ctx), perguntas);
            }
            finally
            {
                DocTool.AoTravar = null;
            }
        }

        [Fact]
        public async Task Travado_JaFecheiTentarDeNovo_GravaQuandoOExcelSolta()
        {
            string c = Planilha();
            var excel = new FileStream(c, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            var (r, perguntas) = await GravarTravado(c, _ => DocTool.NaTrava.TentarDeNovo, n => { if (n == 2) excel.Dispose(); });

            perguntas.Should().Be(2, "na primeira tentativa o Excel ainda estava aberto");
            r.Should().StartWith("SUCESSO");
            LeitorDePlanilha.Abrir(c).Aba(null)![2, 2].Texto.Should().Be("1290");
        }

        [Fact]
        public async Task Travado_SalvarComoCopia_GravaAoLado_ENaoTocaOOriginal()
        {
            string c = Planilha();
            using var excel = new FileStream(c, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            var (r, _) = await GravarTravado(c, _ => DocTool.NaTrava.SalvarCopia);

            r.Should().StartWith("SUCESSO").And.Contain("COMO CÓPIA");
            string copia = Path.Combine(_pasta, "Custos (AIB).xlsx");
            LeitorDePlanilha.Abrir(copia).Aba(null)![2, 2].Texto.Should().Be("1290");
            _tool.Copias.Copias(c).Should().BeEmpty("o original não foi tocado, então não há o que guardar");
        }

        [Fact]
        public async Task Travado_Desistir_NadaSeGrava()
        {
            string c = Planilha();
            var antes = File.ReadAllBytes(c);
            using (var excel = new FileStream(c, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                var (r, _) = await GravarTravado(c, _ => DocTool.NaTrava.Desistir);
                r.Should().StartWith("ERRO").And.Contain("desistiu");
            }

            File.ReadAllBytes(c).Should().Equal(antes);
        }

        [Fact]
        public void ADescricao_TemOrcamentoProprio()
        {
            // Vai ao modelo em toda requisição e é paga. Crescer pede decisão, não acidente.
            const int Orcamento = 460;
            _tool.Description.Length.Should().BeLessThanOrEqualTo(Orcamento);
        }
    }
}
