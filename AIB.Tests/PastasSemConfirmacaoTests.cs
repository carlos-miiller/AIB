using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A lista de pastas que dispensam o card de confirmação.
    /// <para>
    /// Ela já foi o contrário: confinamento. Uma pasta preenchida BARRAVA o resto do disco, e a
    /// recusa vinha antes do card — pedir uma gravação na pasta ao lado dava erro sem nunca
    /// mostrar a pergunta. O rótulo dizia "pastas onde a gravação é permitida" e quem lia entendia
    /// "pastas que não pedem confirmação". Estes testes travam o significado NOVO: dispensar, e
    /// nunca barrar.
    /// </para>
    /// </summary>
    [Collection("Escrita")]
    public class PastasSemConfirmacaoTests : IDisposable
    {
        private readonly string _dir;

        public PastasSemConfirmacaoTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aib-raiz-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            AlwaysAllowSession.Clear();
        }

        public void Dispose()
        {
            // O estado estático não pode vazar para outra classe de teste: o xUnit roda as
            // classes em paralelo, e uma dispensa esquecida aqui faria um teste de portão em
            // outro arquivo passar sem nunca perguntar nada.
            PastasSemConfirmacao.Configurar("");
            AlwaysAllowSession.Clear();
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // A lista
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ListaVAZIA_NaoDispensaNada()
        {
            // O padrão é perguntar. Dispensar tem de ser escolha escrita, nunca efeito colateral
            // de campo em branco.
            PastasSemConfirmacao.Dispensa(@"C:\qualquer\lugar\a.txt", "").Should().BeFalse();
            PastasSemConfirmacao.Dispensa(@"C:\qualquer\lugar\a.txt", null).Should().BeFalse();
            PastasSemConfirmacao.Dispensa(@"C:\qualquer\lugar\a.txt", "  \n \n ").Should().BeFalse();
        }

        [Fact]
        public void AsLinhas_ViramCaminhos_SemRepeticaoESemLixo()
        {
            var raizes = PastasSemConfirmacao.Analisar(
                "  C:\\um  \n\n\"C:\\dois\"\r\nC:\\um\\\nC:\\UM\n");

            raizes.Should().HaveCount(2, "aspas, espaço, barra final e caixa não fazem pasta nova");
            raizes[0].Should().Be(@"C:\um");
            raizes[1].Should().Be(@"C:\dois");
        }

        // ─────────────────────────────────────────────────────────────────────
        // O alcance da dispensa
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void DentroDaPasta_Dispensa()
        {
            PastasSemConfirmacao.Dispensa(Path.Combine(_dir, "sub", "a.txt"), _dir).Should().BeTrue();
            PastasSemConfirmacao.Dispensa(_dir, _dir).Should().BeTrue("a própria pasta vale");
        }

        [Fact]
        public void ForaDaPasta_PERGUNTA_ENaoRecusa()
        {
            string fora = Path.Combine(Path.GetTempPath(), "outra-arvore", "a.txt");

            PastasSemConfirmacao.Dispensa(fora, _dir).Should().BeFalse();
        }

        [Fact]
        public void PontoPonto_NaoHerdaADispensa()
        {
            // O caminho começa dentro da pasta e sai dela pelo texto. Sem resolver o '..' antes
            // de comparar, o prefixo casaria e a gravação de fora passaria sem perguntar.
            PastasSemConfirmacao.Dispensa(Path.Combine(_dir, "..", "vizinho.txt"), _dir)
                .Should().BeFalse();
        }

        [Fact]
        public void PastaVIZINHA_ComOMesmoComeco_NaoEntra()
        {
            // "C:\dados" e "C:\dados-2": comparação por prefixo cru deixaria a segunda passar.
            string raiz = Path.Combine(_dir, "dados");

            PastasSemConfirmacao.Dispensa(Path.Combine(_dir, "dados-2", "a.txt"), raiz).Should().BeFalse();
            PastasSemConfirmacao.Dispensa(Path.Combine(raiz, "a.txt"), raiz).Should().BeTrue();
        }

        [Fact]
        public void VariasPastas_BastaUmaCasar()
        {
            string outra = Path.Combine(Path.GetTempPath(), "aib-outra-" + Guid.NewGuid().ToString("N"));
            string lista = _dir + Environment.NewLine + outra;

            PastasSemConfirmacao.Dispensa(Path.Combine(outra, "a.txt"), lista).Should().BeTrue();
            PastasSemConfirmacao.Dispensa(Path.Combine(_dir, "a.txt"), lista).Should().BeTrue();
            PastasSemConfirmacao.Dispensa(@"C:\terceiro\a.txt", lista).Should().BeFalse();
        }

        // ─────────────────────────────────────────────────────────────────────
        // As ferramentas
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ForaDaLista_AsDuasFerramentas_NAO_RECUSAM_NoPreVoo()
        {
            // O defeito relatado: com uma pasta preenchida, gravar em qualquer outra dava erro
            // antes mesmo de o card aparecer. A lista não barra mais nada.
            PastasSemConfirmacao.Configurar(Path.Combine(_dir, "so-aqui"));

            string alvo = Path.Combine(_dir, "a.txt");
            File.WriteAllText(alvo, "velho");

            ((ITool)new WriteFileTool())
                .Validar(JsonSerializer.Serialize(new { path = alvo, content = "x" }))
                .Should().BeNull();

            new EditFileTool().Validar(JsonSerializer.Serialize(new
            {
                path = alvo,
                old_string = "velho",
                new_string = "novo"
            })).Should().BeNull();
        }

        // A lista é ESTÁTICA, e qualquer teste que carregue settings a reescreve. Por isso cada
        // ensaio abaixo chama Configurar COLADO na pergunta que ele faz, e nunca duas perguntas
        // sob um Configurar só: entre uma e outra, outra classe já apagou a lista.

        [Fact]
        public void DentroDaLista_OWrite_DispensaOCard()
        {
            string alvo = Path.Combine(_dir, "a.txt");
            string args = JsonSerializer.Serialize(new { path = alvo, content = "x" });

            PastasSemConfirmacao.Configurar(_dir);
            ((ITool)new WriteFileTool()).DispensaConfirmacao(args).Should().BeTrue();
        }

        [Fact]
        public void DentroDaLista_OEdit_DispensaOCard()
        {
            string alvo = Path.Combine(_dir, "a.txt");
            File.WriteAllText(alvo, "velho");
            string args = JsonSerializer.Serialize(new
            {
                path = alvo,
                old_string = "velho",
                new_string = "novo"
            });

            PastasSemConfirmacao.Configurar(_dir);
            ((ITool)new EditFileTool()).DispensaConfirmacao(args).Should().BeTrue();
        }

        [Fact]
        public void ForaDaLista_OWrite_PedeOCard()
        {
            string args = JsonSerializer.Serialize(new
            {
                path = Path.Combine(_dir, "a.txt"),
                content = "x"
            });

            PastasSemConfirmacao.Configurar(Path.Combine(_dir, "so-aqui"));
            ((ITool)new WriteFileTool()).DispensaConfirmacao(args).Should().BeFalse();
        }

        [Fact]
        public void ForaDaLista_OEdit_PedeOCard()
        {
            string alvo = Path.Combine(_dir, "a.txt");
            File.WriteAllText(alvo, "velho");
            string args = JsonSerializer.Serialize(new
            {
                path = alvo,
                old_string = "velho",
                new_string = "novo"
            });

            PastasSemConfirmacao.Configurar(Path.Combine(_dir, "so-aqui"));
            ((ITool)new EditFileTool()).DispensaConfirmacao(args).Should().BeFalse();
        }

        [Fact]
        public void OShell_NUNCA_EhDispensado()
        {
            // Um comando não declara alvo: ele descobre o que vai tocar enquanto roda. Dispensar
            // shell por causa de um caminho citado no texto seria prometer o que não se entrega.
            PastasSemConfirmacao.Configurar(_dir);
            ((ITool)new RunCommandTool())
                .DispensaConfirmacao("{\"command\":\"Set-Content " + _dir.Replace("\\", "\\\\") + "\\\\a.txt x\"}")
                .Should().BeFalse();
        }

        // ─────────────────────────────────────────────────────────────────────
        // O portão
        // ─────────────────────────────────────────────────────────────────────

        private sealed class PromptQueConta : IConfirmationPrompt
        {
            public int Perguntas { get; private set; }

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                Perguntas++;
                return Task.FromResult((true, false));
            }
        }

        [Fact]
        public async Task NaPastaDispensada_OPortaoNaoPergunta_EAGravacaoAcontece()
        {
            var prompt = new PromptQueConta();
            var registry = new ToolRegistry(prompt);
            string alvo = Path.Combine(_dir, "novo.txt");
            string args = JsonSerializer.Serialize(new { path = alvo, content = "oi" });
            string? decisao = null;

            PastasSemConfirmacao.Configurar(_dir);
            string r = await registry.ExecuteToolAsync("write", args, 9, null, d => decisao = d);

            r.Should().StartWith("SUCESSO");
            prompt.Perguntas.Should().Be(0);
            decisao.Should().Be("pasta_dispensada", "o registro tem de distinguir dispensada de autorizada");
            File.ReadAllText(alvo).Should().Be("oi");
        }

        [Fact]
        public async Task ForaDaPastaDispensada_OPortaoPERGUNTA()
        {
            var prompt = new PromptQueConta();
            var registry = new ToolRegistry(prompt);
            string alvo = Path.Combine(_dir, "novo.txt");
            string args = JsonSerializer.Serialize(new { path = alvo, content = "oi" });
            string? decisao = null;

            PastasSemConfirmacao.Configurar(Path.Combine(_dir, "so-aqui"));
            string r = await registry.ExecuteToolAsync("write", args, 9, null, d => decisao = d);

            r.Should().StartWith("SUCESSO");
            prompt.Perguntas.Should().Be(1, "fora da lista o card aparece — mas nada é recusado");
            decisao.Should().Be("permitida");
        }

        [Fact]
        public async Task ComEmailNoContexto_ADispensaNaoVale()
        {
            // Uma pasta marcada de confiança foi marcada contra os enganos do modelo, não contra
            // um pedido escrito por quem mandou o e-mail.
            var prompt = new PromptQueConta();
            var registry = new ToolRegistry(prompt) { ConteudoDeEmailNoContexto = () => true };
            string args = JsonSerializer.Serialize(new { path = Path.Combine(_dir, "a.txt"), content = "oi" });

            PastasSemConfirmacao.Configurar(_dir);
            await registry.ExecuteToolAsync("write", args, 9);

            prompt.Perguntas.Should().Be(1);
        }

        // ─────────────────────────────────────────────────────────────────────
        // O aviso do shell
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OComandoQueEscreveFora_AVISA_ENaoBarra()
        {
            string aviso = EscritaNoComando.Aviso(
                "New-Item -ItemType Directory -Path \"C:\\Users\\Carlo\\Estoque_TI\"", _dir)!;

            aviso.Should().NotBeNull();
            aviso.Should().Contain(@"C:\Users\Carlo\Estoque_TI");
            aviso.Should().Contain("não é limitado");
        }

        [Fact]
        public void OComandoQueEscreveDENTRO_NaoAvisa()
        {
            EscritaNoComando.Aviso($"Set-Content \"{Path.Combine(_dir, "a.txt")}\" -Value x", _dir)
                .Should().BeNull();
        }

        [Fact]
        public void ComandoQueSoLE_NaoAvisa()
        {
            EscritaNoComando.Aviso(@"Get-Content C:\Windows\System32\drivers\etc\hosts", _dir)
                .Should().BeNull();
        }

        [Fact]
        public void SemListaConfigurada_NaoAvisaNUNCA()
        {
            // Sem pasta de confiança escolhida, todo caminho estaria "fora" e o aviso apareceria
            // em cima de todo comando. Um alerta que aparece sempre é um alerta que ninguém lê.
            EscritaNoComando.Aviso(@"Remove-Item C:\qualquer\coisa", "").Should().BeNull();
        }

        [Fact]
        public void ORedirecionamento_TambemConta()
        {
            // '> arquivo' grava sem verbo nenhum.
            EscritaNoComando.Aviso(@"Get-Process > C:\fora\lista.txt", _dir)
                .Should().NotBeNull();
        }

        [Fact]
        public void OAviso_ChegaAoCardDoShell()
        {
            PastasSemConfirmacao.Configurar(_dir);
            var ctx = new RunCommandTool().BuildConfirmationContext(
                "{\"command\":\"Set-Content C:\\\\fora\\\\a.txt -Value x\"}", 9);

            ctx.Should().NotBeNull();
            ctx!.Aviso.Should().Contain(@"C:\fora\a.txt");
        }

        // ─────────────────────────────────────────────────────────────────────
        // A lista de "sempre permitir"
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AsAutorizacoesDaSessao_PodemSerVistas_EEsquecidas()
        {
            // Uma allowlist que o usuário não consegue VER é uma decisão de segurança tomada por
            // ele e depois escondida dele.
            AlwaysAllowSession.Clear();
            AlwaysAllowSession.Add(("shell", "git status", null));
            AlwaysAllowSession.Add(("skill", "montar-planilha", "abc123"));

            AlwaysAllowSession.Quantos.Should().Be(2);
            AlwaysAllowSession.Listar().Should().Contain(x => x.Cmd == "git status");

            AlwaysAllowSession.Clear();
            AlwaysAllowSession.Quantos.Should().Be(0);
            AlwaysAllowSession.Listar().Should().BeEmpty();
        }
    }
}
