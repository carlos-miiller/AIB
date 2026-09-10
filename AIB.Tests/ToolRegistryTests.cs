using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIB.Services;
using FluentAssertions;
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

            natives.Should().HaveCount(7);
            dynamics.Should().BeEmpty("no lazy loading as skills não entram no registry");
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
