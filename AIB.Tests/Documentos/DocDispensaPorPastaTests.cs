using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Documentos;
using AIB.Services.Tools;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Dispensa por pasta na ferramenta <c>doc</c>: dentro das pastas sem confirmação, gravar
    /// planilha ou Word executa direto pelo <see cref="ToolRegistry"/> sem mostrar cartão, mas
    /// ainda montando o plano, checando a impressão na gravação e guardando a cópia de segurança.
    /// <para>
    /// Caso real: a Shadow/Ellen edita muitas planilhas e cada gravação virava um cartão, mesmo
    /// em pastas que o usuário já havia liberado para <c>write</c> e <c>edit</c>.
    /// </para>
    /// </summary>
    [Collection("Escrita")]
    public class DocDispensaPorPastaTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _confiavel;
        private readonly string _fora;
        private readonly string _dados;

        public DocDispensaPorPastaTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aib-doc-dispensa-" + Guid.NewGuid().ToString("N"));
            _confiavel = Path.Combine(_dir, "confiavel");
            _fora = Path.Combine(_dir, "fora");
            _dados = Path.Combine(_dir, "dados");
            Directory.CreateDirectory(_confiavel);
            Directory.CreateDirectory(_fora);
            Directory.CreateDirectory(_dados);
            PastasSemConfirmacao.Configurar("");
            AlwaysAllowSession.Clear();
        }

        public void Dispose()
        {
            PastasSemConfirmacao.Configurar("");
            AlwaysAllowSession.Clear();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private sealed class PromptQueConta : IConfirmationPrompt
        {
            public int Perguntas { get; private set; }

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                Perguntas++;
                return Task.FromResult((true, false));
            }
        }

        private ToolRegistry CriarRegistry(PromptQueConta prompt, Func<bool>? comEmail = null)
        {
            var registry = new ToolRegistry(prompt) { ConteudoDeEmailNoContexto = comEmail };
            registry.Registrar(new DocTool(_dados));
            return registry;
        }

        private static string Planilha(string pasta, string nome = "Custos.xlsx")
        {
            string c = Path.Combine(pasta, nome);
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Dados");
            ws.Cell("A1").Value = "Item";
            ws.Cell("B1").Value = "Valor";
            ws.Cell("A2").Value = "Frete";
            ws.Cell("B2").Value = 100;
            wb.SaveAs(c);
            return c;
        }

        private static string Editar(string caminho, int valor = 250) => JsonSerializer.Serialize(new
        {
            action = "edit",
            path = caminho,
            ops = new[] { new { cell = "B2", value = valor } }
        });

        [Fact]
        public async Task NaPastaDispensada_PlanilhaGravadaPelaDoc_PassaSemCartao_EGravaComCopia()
        {
            // Caso real: a Shadow/Ellen editava planilhas em pastas já liberadas para
            // write/edit, mas cada chamada da doc abria cartão por não implementar dispensa.
            var prompt = new PromptQueConta();
            var registry = CriarRegistry(prompt);
            string alvo = Planilha(_confiavel);
            string? decisao = null;

            PastasSemConfirmacao.Configurar(_confiavel);
            string r = await registry.ExecuteToolAsync("doc", Editar(alvo, 320), 2, null, d => decisao = d);

            r.Should().StartWith("SUCESSO");
            prompt.Perguntas.Should().Be(0);
            decisao.Should().Be("pasta_dispensada");
            LeitorDePlanilha.Abrir(alvo).Aba(null)![2, 2].Texto.Should().Be("320");
            new CopiasDeDocumento(_dados).Copias(alvo).Should().ContainSingle();
        }

        [Fact]
        public async Task NaPastaDispensada_CriarPlanilha_PassaSemCartao_EGrava()
        {
            var prompt = new PromptQueConta();
            var registry = CriarRegistry(prompt);
            string alvo = Path.Combine(_confiavel, "Nova.xlsx");
            string args = JsonSerializer.Serialize(new
            {
                action = "create",
                path = alvo,
                content = new { sheets = new[] { new { name = "Resumo", rows = new[] { new object[] { "Col", "Val" }, new object[] { "A", 10 } } } } }
            });
            string? decisao = null;

            PastasSemConfirmacao.Configurar(_confiavel);
            string r = await registry.ExecuteToolAsync("doc", args, 2, null, d => decisao = d);

            r.Should().StartWith("SUCESSO");
            prompt.Perguntas.Should().Be(0);
            decisao.Should().Be("pasta_dispensada");
            File.Exists(alvo).Should().BeTrue();
            LeitorDePlanilha.Abrir(alvo).Aba(null)![2, 2].Texto.Should().Be("10");
        }

        [Fact]
        public async Task CriarComModeloForaDaPasta_DestinoDentro_DispensaCartao()
        {
            // O modelo de origem ('from') só é lido; basta o destino estar na pasta de confiança.
            string modelo = Planilha(_fora, "Modelo.xlsx");
            string destino = Path.Combine(_confiavel, "Copia.xlsx");
            var prompt = new PromptQueConta();
            var registry = CriarRegistry(prompt);
            string args = JsonSerializer.Serialize(new
            {
                action = "create",
                path = destino,
                from = modelo
            });
            string? decisao = null;

            PastasSemConfirmacao.Configurar(_confiavel);
            string r = await registry.ExecuteToolAsync("doc", args, 2, null, d => decisao = d);

            r.Should().StartWith("SUCESSO");
            prompt.Perguntas.Should().Be(0);
            decisao.Should().Be("pasta_dispensada");
            File.Exists(destino).Should().BeTrue();
            LeitorDePlanilha.Abrir(destino).Aba(null)![2, 2].Texto.Should().Be("100");
        }

        [Fact]
        public async Task ForaDaPastaDispensada_PlanilhaGravadaPelaDoc_PedeCartao()
        {
            var prompt = new PromptQueConta();
            var registry = CriarRegistry(prompt);
            string alvo = Planilha(_fora);
            string? decisao = null;

            PastasSemConfirmacao.Configurar(_confiavel);
            string r = await registry.ExecuteToolAsync("doc", Editar(alvo, 450), 2, null, d => decisao = d);

            r.Should().StartWith("SUCESSO");
            prompt.Perguntas.Should().Be(1);
            decisao.Should().Be("permitida");
            LeitorDePlanilha.Abrir(alvo).Aba(null)![2, 2].Texto.Should().Be("450");
        }

        [Fact]
        public async Task Leitura_NaoMudaDeComportamento_ComPastaDispensada()
        {
            var prompt = new PromptQueConta();
            var registry = CriarRegistry(prompt);
            var tool = new DocTool(_dados);

            string foraDosDados = Planilha(_confiavel);
            string dentroDosDados = Planilha(_dados, "Segredo.xlsx");
            string lerFora = JsonSerializer.Serialize(new { action = "read", path = foraDosDados });
            string lerDados = JsonSerializer.Serialize(new { action = "read", path = dentroDosDados });

            // Mesmo com a raiz inteira liberada para gravação, leitura não dispensa dentro dos
            // dados do AIB e fora deles segue automática como antes.
            PastasSemConfirmacao.Configurar(_dir);

            tool.DispensaConfirmacao(lerFora).Should().BeFalse();
            tool.DispensaConfirmacao(lerDados).Should().BeFalse();

            string? decFora = null;
            string rFora = await registry.ExecuteToolAsync("doc", lerFora, 2, null, d => decFora = d);
            rFora.Should().Contain("Frete");
            decFora.Should().Be("automatica");
            prompt.Perguntas.Should().Be(0);

            string? decDados = null;
            string rDados = await registry.ExecuteToolAsync("doc", lerDados, 2, null, d => decDados = d);
            rDados.Should().Contain("Frete");
            decDados.Should().Be("permitida");
            prompt.Perguntas.Should().Be(1);
        }

        [Fact]
        public async Task ComEmailNoContexto_DispensaDaDocNaoVale()
        {
            var prompt = new PromptQueConta();
            var registry = CriarRegistry(prompt, () => true);
            string alvo = Planilha(_confiavel);

            PastasSemConfirmacao.Configurar(_confiavel);
            string r = await registry.ExecuteToolAsync("doc", Editar(alvo, 500), 2);

            r.Should().StartWith("SUCESSO");
            prompt.Perguntas.Should().Be(1);
        }

        [Fact]
        public void NaPastaDeDadosDoAib_OuArgumentoIlegivel_NuncaDispensa()
        {
            var tool = new DocTool(_dados);
            string emDataDir = Path.Combine(DirectoryService.DataDir, "planilha.xlsx");
            string emDadosTeste = Path.Combine(_dados, "planilha.xlsx");

            PastasSemConfirmacao.Configurar(DirectoryService.DataDir + Environment.NewLine + _dir);

            tool.DispensaConfirmacao(Editar(emDataDir)).Should().BeFalse();
            tool.DispensaConfirmacao(Editar(emDadosTeste)).Should().BeFalse();
            tool.DispensaConfirmacao("{json-quebrado").Should().BeFalse();
            tool.DispensaConfirmacao("{}").Should().BeFalse();
            tool.DispensaConfirmacao(JsonSerializer.Serialize(new { action = "edit", path = "", ops = new[] { new { cell = "A1", value = 1 } } })).Should().BeFalse();
        }

        [Fact]
        public void CaminhoUnc_NaListaDePastas_NuncaDispensa()
        {
            // Um alias UNC (ex.: \\localhost\C$\Users\...) pode apontar para a pasta local do
            // usuário e esconder ~/.AIB na comparação com DirectoryService.DataDir.
            string raiz = Path.GetPathRoot(_dir) ?? @"C:\";
            char letra = char.ToUpperInvariant(raiz[0]);
            string semLetra = _dir[raiz.Length..];
            string pastaUnc = $@"\\localhost\{letra}$\{semLetra}";
            string alvoUnc = Path.Combine(pastaUnc, "planilha.xlsx");
            string alvoSlash = $"//localhost/{letra}$/{semLetra.Replace('\\', '/')}/planilha.xlsx";
            string alvoExtendedUnc = $@"\\?\UNC\localhost\{letra}$\{semLetra}\planilha.xlsx";

            var tool = new DocTool(_dados);
            PastasSemConfirmacao.Configurar(pastaUnc + Environment.NewLine + $@"\\?\UNC\localhost\{letra}$\{semLetra}");

            tool.DispensaConfirmacao(Editar(alvoUnc)).Should().BeFalse();
            tool.DispensaConfirmacao(Editar(alvoSlash)).Should().BeFalse();
            tool.DispensaConfirmacao(Editar(alvoExtendedUnc)).Should().BeFalse();
        }
    }
}
