using System.Collections.Generic;
using System.Threading.Tasks;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O portão humano das ferramentas destrutivas.
    /// <para>
    /// Estes testes existem porque a ausência deles foi a causa raiz: a janela de confirmação
    /// estava completa e correta, e ficou com ZERO chamadores em toda a árvore sem que nada
    /// acusasse. O registry chamava WPF direto, então não havia como exercitar o portão — e um
    /// gate que não é testado é um gate que some em silêncio.
    /// </para>
    /// <para>
    /// Eles não sabem QUEM pergunta, e é isso que os manteve válidos quando a janela modal deu
    /// lugar ao card dentro da conversa: o que está travado aqui é o contrato do portão, não a
    /// forma da tela.
    /// </para>
    /// </summary>
    public class ConfirmationGateTests
    {
        private sealed class PromptFalso : IConfirmationPrompt
        {
            private readonly bool _permitir;
            private readonly bool _sempre;

            public PromptFalso(bool permitir, bool sempre = false)
            {
                _permitir = permitir;
                _sempre = sempre;
            }

            public List<CommandConfirmationContext> Perguntas { get; } = new();

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                Perguntas.Add(context);
                return Task.FromResult((_permitir, _sempre));
            }
        }

        private static string ComandoInofensivo(string cmd) => "{\"command\":\"" + cmd + "\"}";

        public ConfirmationGateTests() => AlwaysAllowSession.Clear();

        [Fact]
        public async Task SemPromptDisponivel_FerramentaDestrutivaEhRecusada()
        {
            // Sem UI para autorizar, o padrão seguro é não executar. Era exatamente este caminho
            // que rodava PowerShell arbitrário sem perguntar nada.
            var registry = new ToolRegistry(confirmationPrompt: null);

            string r = await registry.ExecuteToolAsync("shell", ComandoInofensivo("echo oi"), userLevel: 9);

            r.Should().StartWith("ACESSO NEGADO");
            r.Should().Contain("não há interface disponível");
        }

        [Theory]
        [InlineData(false, "recusada")]
        [InlineData(null, "negada_sem_interface")]
        public async Task ODesfechoDoPortao_ChegaAQuemRegistra(bool? permitir, string esperada)
        {
            // Vai para o raw.jsonl com o resultado. Sem a decisão, "você recusou" e "não havia
            // tela para perguntar" são o mesmo texto de erro no registro.
            var registry = new ToolRegistry(permitir == null ? null : new PromptFalso(permitir.Value));
            string? decisao = null;

            await registry.ExecuteToolAsync("shell", ComandoInofensivo("echo oi"), 9, null, d => decisao = d);

            decisao.Should().Be(esperada);
        }

        [Fact]
        public async Task FerramentaQueNaoExiste_TambemDizODesfecho()
        {
            var registry = new ToolRegistry(confirmationPrompt: null);
            string? decisao = null;

            await registry.ExecuteToolAsync("nao_existe", "{}", 9, null, d => decisao = d);

            decisao.Should().Be("ferramenta_desconhecida");
        }

        [Fact]
        public async Task UsuarioRecusa_ComandoNaoRoda_EOModeloRecebeTextoTratavel()
        {
            var prompt = new PromptFalso(permitir: false);
            var registry = new ToolRegistry(prompt);

            string r = await registry.ExecuteToolAsync("shell", ComandoInofensivo("echo oi"), userLevel: 9);

            prompt.Perguntas.Should().HaveCount(1, "o modal tem de ser consultado");
            r.Should().Be("Ação Rejeitada pelo Usuário.",
                "o retorno alimenta o loop ReAct: o modelo se desculpa ou propõe alternativa em vez de quebrar");
        }

        [Fact]
        public async Task OModalRecebeOComandoExato_QueVaiExecutar()
        {
            var prompt = new PromptFalso(permitir: false);
            var registry = new ToolRegistry(prompt);

            await registry.ExecuteToolAsync("shell", ComandoInofensivo("Get-Process"), userLevel: 9);

            prompt.Perguntas[0].Command.Should().Be("Get-Process");
            prompt.Perguntas[0].Tool.Should().Be("shell");
            prompt.Perguntas[0].Level.Should().Be(9);
        }

        [Fact]
        public async Task WriteFile_MostraOCaminhoAbsolutoResolvido()
        {
            // Autorizar "config.json" e autorizar a gravação real em Startup\config.json são
            // decisões diferentes. O modal precisa mostrar a segunda.
            var prompt = new PromptFalso(permitir: false);
            var registry = new ToolRegistry(prompt);

            await registry.ExecuteToolAsync(
                "write", "{\"path\":\"arquivo.txt\",\"content\":\"oi\"}", userLevel: 9);

            prompt.Perguntas.Should().HaveCount(1);
            prompt.Perguntas[0].Command.Should().MatchRegex(@"^(CRIAR|SOBRESCREVER) [A-Za-z]:\\",
                "o caminho tem de estar resolvido em absoluto, não como o modelo escreveu");
        }

        [Fact]
        public async Task SemprePermitir_NaoPerguntaDeNovoParaOMesmoComando()
        {
            var prompt = new PromptFalso(permitir: true, sempre: true);
            var registry = new ToolRegistry(prompt);

            await registry.ExecuteToolAsync("shell", ComandoInofensivo("echo um"), userLevel: 9);
            await registry.ExecuteToolAsync("shell", ComandoInofensivo("echo um"), userLevel: 9);

            prompt.Perguntas.Should().HaveCount(1, "o segundo uso do MESMO comando vem da allowlist de sessão");
        }

        [Fact]
        public async Task SemprePermitir_NaoVazaParaOutroComando()
        {
            var prompt = new PromptFalso(permitir: true, sempre: true);
            var registry = new ToolRegistry(prompt);

            await registry.ExecuteToolAsync("shell", ComandoInofensivo("echo um"), userLevel: 9);
            await registry.ExecuteToolAsync("shell", ComandoInofensivo("echo dois"), userLevel: 9);

            prompt.Perguntas.Should().HaveCount(2, "a allowlist casa byte a byte, não por prefixo nem por ferramenta");
        }

        [Fact]
        public async Task LeituraDeArquivo_NaoPassaPeloPortao()
        {
            // read não altera a máquina: exigir confirmação a cada leitura treinaria o
            // usuário a clicar "permitir" sem ler, esvaziando o portão onde ele importa.
            var prompt = new PromptFalso(permitir: false);
            var registry = new ToolRegistry(prompt);

            await registry.ExecuteToolAsync(
                "read", "{\"path\":\"C:\\\\naoexiste\\\\arquivo.txt\"}", userLevel: 9);

            prompt.Perguntas.Should().BeEmpty();
        }

        [Fact]
        public async Task ArgumentosIlegiveis_NaoViramAutorizacao()
        {
            var prompt = new PromptFalso(permitir: true);
            var registry = new ToolRegistry(prompt);

            string r = await registry.ExecuteToolAsync("shell", "{isso nao e json", userLevel: 9);

            prompt.Perguntas.Should().BeEmpty("não dá para autorizar o que não se consegue descrever");
            r.Should().StartWith("ACESSO NEGADO");
        }

        [Fact]
        public async Task NivelInsuficiente_BarraAntesDeAbrirOModal()
        {
            var prompt = new PromptFalso(permitir: true);
            var registry = new ToolRegistry(prompt);

            string r = await registry.ExecuteToolAsync("shell", ComandoInofensivo("echo oi"), userLevel: 1);

            r.Should().StartWith("ACESSO NEGADO");
            prompt.Perguntas.Should().BeEmpty("a trava de nível vem antes de incomodar o usuário");
        }

        [Fact]
        public async Task FloorList_RefutaDestrutivo_SemPerguntar()
        {
            // O floor rodava DEPOIS do card: o usuário via "Motivo do bloqueio", clicava
            // Permitir, e era recusado do mesmo jeito. Uma pergunta cuja resposta "sim" não vale
            // nada não pode ser feita — o piso agora barra antes do card.
            var prompt = new PromptFalso(permitir: true);
            var registry = new ToolRegistry(prompt);
            string? decisao = null;

            string r = await registry.ExecuteToolAsync(
                "shell", ComandoInofensivo("Remove-Item -Recurse C:\\\\dados"), 6, null, d => decisao = d);

            prompt.Perguntas.Should().BeEmpty("o que o piso barra não vira pergunta");
            r.Should().StartWith("ACESSO NEGADO (FLOOR)");
            decisao.Should().Be("barrada_pelo_piso");
        }

        [Fact]
        public async Task FloorList_NoNivel7_OCardContinuaPerguntando()
        {
            // A exceção por nível é a de sempre: em L>=7 o piso não barra, e o card é a
            // autoridade única — ele PERGUNTA, não libera sozinho.
            var prompt = new PromptFalso(permitir: false);
            var registry = new ToolRegistry(prompt);

            await registry.ExecuteToolAsync(
                "shell", ComandoInofensivo("Remove-Item -Recurse C:\\\\dados"), userLevel: 7);

            prompt.Perguntas.Should().HaveCount(1);
        }

        [Fact]
        public async Task FloorList_NaoLeOCaminhoDoWrite()
        {
            // O Command do write é "CRIAR <caminho>". Um caminho com "logoff" no nome era
            // recusado como "desligamento/reboot/logoff" — o piso é para linha de comando.
            var prompt = new PromptFalso(permitir: false);
            var registry = new ToolRegistry(prompt);
            string alvo = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "aib-logoff-shutdown-" + System.Guid.NewGuid().ToString("N"), "format.txt");

            string r = await registry.ExecuteToolAsync(
                "write", System.Text.Json.JsonSerializer.Serialize(new { path = alvo, content = "x" }), userLevel: 2);

            prompt.Perguntas.Should().HaveCount(1, "o card pergunta; o piso não tem o que dizer sobre um caminho");
            r.Should().Be("Ação Rejeitada pelo Usuário.");
        }

        [Fact]
        public async Task FloorList_InativoNoNivel7()
        {
            var prompt = new PromptFalso(permitir: false);
            var registry = new ToolRegistry(prompt);

            string r = await registry.ExecuteToolAsync(
                "shell", ComandoInofensivo("Remove-Item -Recurse C:\\\\dados"), userLevel: 7);

            // Recusado pelo usuário, não pelo floor: em L>=7 o modal é a autoridade única.
            r.Should().Be("Ação Rejeitada pelo Usuário.");
        }
    }
}
