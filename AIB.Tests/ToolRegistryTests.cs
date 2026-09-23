using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIB.Services;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O registry é o único portão entre o modelo e o sistema do usuário: o gating por
    /// RequiredLevel e a recusa de nome desconhecido são a superfície de segurança inteira.
    /// </summary>
    [Collection("Skills")]
    public class ToolRegistryTests
    {
        [Fact]
        public async Task NivelInsuficiente_DevolveAcessoNegado_ENaoExecuta()
        {
            var registry = new ToolRegistry();

            // read exige nível 1; nível 0 tem que bater na trava antes de qualquer IO.
            string result = await registry.ExecuteToolAsync(
                "read",
                "{\"path\":\"C:\\Windows\\System32\\config\\SAM\"}",
                userLevel: 0);

            result.Should().StartWith("ACESSO NEGADO");
            result.Should().Contain("read");
            result.Should().NotContain("ERRO: Arquivo não encontrado",
                "a trava de nível precisa vir ANTES de tocar o disco");
        }

        [Theory]
        [InlineData("read")]
        public async Task GatingDeNivel_ValeParaTodaFerramentaNativa(string toolName)
        {
            var registry = new ToolRegistry();

            string result = await registry.ExecuteToolAsync(toolName, "{}", userLevel: 0);

            result.Should().StartWith("ACESSO NEGADO");
        }

        [Fact]
        public void OOrcamentoDasDescricoes_NaoCRESCE()
        {
            // A Description de TODA ferramenta registrada é reenviada ao modelo em CADA
            // requisição, e é paga em prefill — o custo dominante nesta máquina. Por isso a
            // redação das nove tem orçamento fechado: o que o 'write' e o 'shell' cresceram
            // para dizer o que fazem sozinhos e o que NÃO é com eles foi pago encurtando
            // 'mail' e 'mail_read', que somavam 30% do total para 3 chamadas em 49 sessões.
            const int OrcamentoAnterior = 2056;

            var nove = new ITool[]
            {
                new AIB.Services.Tools.ReadFileTool(),
                new AIB.Services.Tools.WriteFileTool(),
                new AIB.Services.Tools.EditFileTool(),
                new AIB.Services.Tools.GlobTool(),
                new AIB.Services.Tools.GrepTool(),
                new AIB.Services.Tools.RunCommandTool(),
                new AIB.Services.Tools.ExecuteSkillTool(),
                new AIB.Services.Tools.ConsultarEmailsTool(),
                new AIB.Services.Tools.LerEmailTool(
                    () => "",
                    () => new System.Collections.Generic.List<MailAccountSettings>(),
                    _ => null,
                    () => null!)
            };

            nove.Sum(t => t.Description.Length).Should().BeLessThanOrEqualTo(OrcamentoAnterior);
        }

        [Fact]
        public async Task FerramentaDesconhecida_NaoLanca_EDevolveErroDescritivo()
        {
            var registry = new ToolRegistry();

            string result = await registry.ExecuteToolAsync("ferramenta_que_nao_existe", "{}", userLevel: 9);

            result.Should().StartWith("ERRO:");
            result.Should().Contain("ferramenta_que_nao_existe");
            result.Should().Contain("não encontrada no registry");
        }

        [Fact]
        public async Task ArgumentosMalformados_ViramErroDeTexto_NuncaExcecao()
        {
            var registry = new ToolRegistry();

            // JSON quebrado vindo do modelo não pode derrubar o turno.
            Func<Task> act = async () => await registry.ExecuteToolAsync("read", "{isso nao e json", userLevel: 1);

            var result = await registry.ExecuteToolAsync("read", "{isso nao e json", userLevel: 1);
            await act.Should().NotThrowAsync();
            result.Should().StartWith("ERRO");
        }

        [Fact]
        public void GetActiveTools_FiltraPorNivel()
        {
            var registry = new ToolRegistry();

            registry.GetActiveTools(0).Should().BeEmpty("nenhuma nativa é liberada abaixo do nível 1");
            registry.GetActiveTools(1).Should().NotBeEmpty();
        }

        [Fact]
        public void GetActiveTools_NaoDevolveNomesDuplicados()
        {
            var registry = new ToolRegistry();

            var names = registry.GetActiveTools(9).Select(t => t.FunctionName).ToList();

            names.Should().OnlyHaveUniqueItems("nome duplicado corrompe a gramática de tools do modelo");
        }

        /// <summary>
        /// Aponta as skills para uma pasta vazia enquanto o bloco durar.
        /// <para>
        /// Sem isto, o registry passa a depender do que o usuario tem instalado em
        /// ~/.AIB/skills: a skill so e registrada quando ha alguma skill, e o ensaio
        /// que conta ferramentas mudaria de resultado conforme a maquina.
        /// </para>
        /// </summary>
        private sealed class SemSkills : IDisposable
        {
            private readonly string? _anterior = SkillService.SkillsDirectoryOverride;
            private readonly string _vazia;

            public SemSkills()
            {
                _vazia = Path.Combine(Path.GetTempPath(), "aib-sem-skills-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_vazia);
                SkillService.SkillsDirectoryOverride = _vazia;
            }

            public void Dispose()
            {
                SkillService.SkillsDirectoryOverride = _anterior;
                try { Directory.Delete(_vazia, true); } catch { }
            }
        }

        [Fact]
        public void FerramentasNativasRegistradas_SaoAsSete()
        {
            using var _ = new SemSkills();
            var registry = new ToolRegistry();

            var names = registry.GetActiveTools(9).Select(t => t.FunctionName).OrderBy(n => n).ToList();

            // As sete. shell, write e edit só executam depois do portão de confirmação;
            // read, glob, grep e mail são leitura e passam direto.
            //
            // mail entra mesmo com a triagem desligada, ao contrário da skill: sem ela o modelo
            // não sabe que "tem algo urgente?" tem resposta possível e responde de memória.
            // Desligada, ela responde exatamente isso.
            names.Should().Equal("edit", "glob", "grep", "mail", "read", "shell", "write");
        }

        [Fact]
        public void ComHabilidadeInstalada_AExecuteSkillEntra()
        {
            // O outro lado do lazy loading: o schema da skill é reenviado ao modelo em
            // toda requisição, então numa instalação sem skills ela não deve existir.
            using var _ = new SemSkills();

            new ToolRegistry().Contains("skill")
                .Should().BeFalse("sem skill instalada, a porta de entrada delas não existe");

            string pasta = Path.Combine(SkillService.Raiz, "ensaio");
            Directory.CreateDirectory(pasta);
            File.WriteAllText(Path.Combine(pasta, "SKILL.md"),
                "---\nname: ensaio\ndescription: d\ninterpreter: markdown\n---\ncorpo");

            new ToolRegistry().Contains("skill").Should().BeTrue();
        }

        [Fact]
        public void Refresh_ReavaliaAsSkillsEmDisco()
        {
            // Uma skill pode nascer durante a conversa. Sem o Refresh reavaliar, ela só
            // existiria na próxima abertura do app.
            using var _ = new SemSkills();
            var registry = new ToolRegistry();

            registry.Contains("skill").Should().BeFalse();

            string pasta = Path.Combine(SkillService.Raiz, "nova");
            Directory.CreateDirectory(pasta);
            File.WriteAllText(Path.Combine(pasta, "SKILL.md"),
                "---\nname: nova\ndescription: d\ninterpreter: markdown\n---\ncorpo");

            registry.Refresh();

            registry.Contains("skill").Should().BeTrue();
        }

        [Theory]
        [InlineData("read")]
        [InlineData("READ")]
        [InlineData("Read")]
        public void Contains_IgnoraCaixa(string toolName)
        {
            new ToolRegistry().Contains(toolName).Should().BeTrue();
        }

        [Fact]
        public void Contains_FerramentaInexistente_EhFalso()
        {
            new ToolRegistry().Contains("nao_existe").Should().BeFalse();
        }

        [Fact]
        public void GetCategorizedTools_DevolveAsNativasESemDinamicas()
        {
            using var _ = new SemSkills();
            var registry = new ToolRegistry();

            var (natives, dynamics) = registry.GetCategorizedTools();

            natives.Should().HaveCount(8, "mail_read é registrada sempre, e só oferecida na conversa de um e-mail");
            dynamics.Should().BeEmpty("no lazy loading as skills não entram no registry");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Os caminhos de falha do portão
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ferramenta de ensaio: pede confirmação, e o resto é o ensaio que escolhe. As nativas
        /// não alcançam estes caminhos depois do pré-voo — por isso ela existe.
        /// </summary>
        private sealed class FerramentaDeEnsaio : ITool
        {
            public string Name => "ensaio_portao";
            public string Description => "ensaio";
            public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
                Name, "ensaio", BinaryData.FromString("{\"type\":\"object\",\"properties\":{}}"));
            public int RequiredLevel => 1;
            public bool RequiresConfirmation => true;

            public bool Dispensa { get; init; }
            public bool Piso { get; init; }
            public Func<CommandConfirmationContext?> Contexto { get; init; } = () => null;
            public int Execucoes { get; private set; }

            public bool DispensaConfirmacao(string argumentsJson) => Dispensa;
            public bool PassaPelaFloorList => Piso;
            public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel) => Contexto();

            public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
            {
                Execucoes++;
                return Task.FromResult("SUCESSO: executou");
            }
        }

        private sealed class PromptQuePermite : IConfirmationPrompt
        {
            public int Perguntas { get; private set; }

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                Perguntas++;
                return Task.FromResult((true, false));
            }
        }

        [Fact]
        public async Task Dispensa_SemContexto_NEGA()
        {
            // O defeito: na dispensa, contexto nulo virava comando "" e a chamada PASSAVA. O mesmo
            // caso, pelo caminho com card, era negado. Fail-closed nos dois.
            var ferramenta = new FerramentaDeEnsaio { Dispensa = true, Contexto = () => null };
            var registry = new ToolRegistry(new PromptQuePermite());
            registry.Registrar(ferramenta);
            string? decisao = null;

            string r = await registry.ExecuteToolAsync(ferramenta.Name, "{}", 9, null, d => decisao = d);

            r.Should().StartWith("ACESSO NEGADO");
            decisao.Should().Be("negada_sem_contexto");
            ferramenta.Execucoes.Should().Be(0);
        }

        [Fact]
        public async Task Dispensa_ContextoQueLANCA_NEGA()
        {
            var ferramenta = new FerramentaDeEnsaio
            {
                Dispensa = true,
                Contexto = () => throw new InvalidOperationException("quebrou ao descrever")
            };
            var registry = new ToolRegistry(new PromptQuePermite());
            registry.Registrar(ferramenta);

            string r = await registry.ExecuteToolAsync(ferramenta.Name, "{}", 9);

            r.Should().StartWith("ACESSO NEGADO");
            ferramenta.Execucoes.Should().Be(0);
        }

        [Fact]
        public async Task ComCard_ContextoQueLANCA_NEGA_SemExcecao()
        {
            var ferramenta = new FerramentaDeEnsaio
            {
                Contexto = () => throw new InvalidOperationException("quebrou ao descrever")
            };
            var prompt = new PromptQuePermite();
            var registry = new ToolRegistry(prompt);
            registry.Registrar(ferramenta);

            string r = await registry.ExecuteToolAsync(ferramenta.Name, "{}", 9);

            r.Should().StartWith("ACESSO NEGADO");
            prompt.Perguntas.Should().Be(0);
            ferramenta.Execucoes.Should().Be(0);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task FloorList_SoParaQuemPassaPorEla_NosDoisCaminhos(bool dispensa)
        {
            // write e edit têm Command "CRIAR <caminho>": um caminho com "shutdown" era recusado
            // como desligamento. O piso só lê quem declara linha de comando.
            var semPiso = new FerramentaDeEnsaio
            {
                Dispensa = dispensa,
                Contexto = () => new CommandConfirmationContext { Tool = "ensaio_portao", Command = @"CRIAR C:\shutdown\logoff.txt" }
            };
            var registry = new ToolRegistry(new PromptQuePermite());
            registry.Registrar(semPiso);

            (await registry.ExecuteToolAsync(semPiso.Name, "{}", 2)).Should().StartWith("SUCESSO");

            var comPiso = new FerramentaDeEnsaio
            {
                Dispensa = dispensa,
                Piso = true,
                Contexto = () => new CommandConfirmationContext { Tool = "ensaio_portao", Command = "shutdown /s /t 0" }
            };
            var prompt = new PromptQuePermite();
            var outro = new ToolRegistry(prompt);
            outro.Registrar(comPiso);
            string? decisao = null;

            string r = await outro.ExecuteToolAsync(comPiso.Name, "{}", 2, null, d => decisao = d);

            r.Should().StartWith("ACESSO NEGADO (FLOOR)");
            decisao.Should().Be("barrada_pelo_piso");
            prompt.Perguntas.Should().Be(0, "o que o piso barra não vira pergunta");
            comPiso.Execucoes.Should().Be(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NinguemRespondeu_NegaComoSemInterface_ENaoComoRecusaDoUsuario(bool apresentadorQueLanca)
        {
            // Sem apresentador (ou com o cartão falhando ao aparecer), a auditoria gravava
            // deny_usuario e o modelo ouvia "Ação Rejeitada pelo Usuário." — uma decisão que
            // ninguém tomou, e que muda o plano do modelo.
            var portao = new ChatConfirmationPrompt();
            if (apresentadorQueLanca) portao.Conectar(_ => throw new InvalidOperationException("janela fechando"));

            var ferramenta = new FerramentaDeEnsaio
            {
                Contexto = () => new CommandConfirmationContext { Tool = "ensaio_portao", Command = "algo" }
            };
            var registry = new ToolRegistry(portao);
            registry.Registrar(ferramenta);
            string? decisao = null;

            string r = await registry.ExecuteToolAsync(ferramenta.Name, "{}", 9, null, d => decisao = d);

            r.Should().StartWith("ACESSO NEGADO").And.NotContain(ToolRegistry.RecusaDoUsuario);
            decisao.Should().Be("negada_sem_interface");
            ferramenta.Execucoes.Should().Be(0);
        }

        [Fact]
        public void SkillQueSoLeOManual_NaoPassaPelaFloorList_EScriptPassa()
        {
            // "LER MANUAL <pasta>\SKILL.md" é caminho, não comando: uma pasta de skill com
            // "shutdown" no nome era barrada como desligamento.
            ITool skill = new AIB.Services.Tools.ExecuteSkillTool();

            skill.PassaPelaFloorListCom(new CommandConfirmationContext
            {
                Command = AIB.Services.Tools.ExecuteSkillTool.PrefixoDoManual + @"C:\skills\shutdown-helper\SKILL.md"
            }).Should().BeFalse();

            skill.PassaPelaFloorListCom(new CommandConfirmationContext { Command = "powershell -File x.ps1" })
                .Should().BeTrue();
        }

        [Fact]
        public void GravarNaPastaDeSkills_ReavaliaAsSkills()
        {
            // materialize_skill não existe mais, e era o único gatilho do Refresh. Um SKILL.md
            // escrito pelo modelo só existia na próxima abertura do app.
            using var _ = new SemSkills();
            var registry = new ToolRegistry();
            registry.Contains("skill").Should().BeFalse();

            string pasta = Path.Combine(SkillService.Raiz, "nova");
            Directory.CreateDirectory(pasta);
            string arquivo = Path.Combine(pasta, "SKILL.md");
            File.WriteAllText(arquivo, "---\nname: nova\ndescription: d\ninterpreter: markdown\n---\ncorpo");
            string args = System.Text.Json.JsonSerializer.Serialize(new { path = arquivo, content = "x" });

            registry.ReavaliarSkillsSeTocou("write", args, "ERRO: não gravou");
            registry.Contains("skill").Should().BeFalse("gravação que falhou não mudou nada");

            registry.ReavaliarSkillsSeTocou("write", args, "SUCESSO: Arquivo salvo");
            registry.Contains("skill").Should().BeTrue();
        }

        [Fact]
        public void GravarFORADaPastaDeSkills_NaoReavalia()
        {
            using var _ = new SemSkills();
            var registry = new ToolRegistry();

            string pasta = Path.Combine(SkillService.Raiz, "nova");
            Directory.CreateDirectory(pasta);
            File.WriteAllText(Path.Combine(pasta, "SKILL.md"),
                "---\nname: nova\ndescription: d\ninterpreter: markdown\n---\ncorpo");

            string fora = Path.Combine(Path.GetTempPath(), "aib-fora-" + Guid.NewGuid().ToString("N"), "a.txt");
            registry.ReavaliarSkillsSeTocou("write",
                System.Text.Json.JsonSerializer.Serialize(new { path = fora, content = "x" }), "SUCESSO");

            registry.Contains("skill").Should().BeFalse("nada na pasta de skills foi tocado");
        }

        [Fact]
        public void Refresh_NaoAlteraOConjuntoDeNativas()
        {
            var registry = new ToolRegistry();
            int before = registry.GetActiveTools(9).Count;

            registry.Refresh();

            registry.GetActiveTools(9).Should().HaveCount(before);
        }
    }
}
