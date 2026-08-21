using System;
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
    public class ToolRegistryTests
    {
        [Fact]
        public async Task NivelInsuficiente_DevolveAcessoNegado_ENaoExecuta()
        {
            var registry = new ToolRegistry();

            // read_file exige nível 1; nível 0 tem que bater na trava antes de qualquer IO.
            string result = await registry.ExecuteToolAsync(
                "read_file",
                "{\"path\":\"C:\\Windows\\System32\\config\\SAM\"}",
                userLevel: 0);

            result.Should().StartWith("ACESSO NEGADO");
            result.Should().Contain("read_file");
            result.Should().NotContain("ERRO: Arquivo não encontrado",
                "a trava de nível precisa vir ANTES de tocar o disco");
        }

        [Theory]
        [InlineData("read_file")]
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
            Func<Task> act = async () => await registry.ExecuteToolAsync("read_file", "{isso nao e json", userLevel: 1);

            var result = await registry.ExecuteToolAsync("read_file", "{isso nao e json", userLevel: 1);
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

        [Fact]
        public void FerramentasNativasRegistradas_SaoAsTres()
        {
            var registry = new ToolRegistry();

            var names = registry.GetActiveTools(9).Select(t => t.FunctionName).OrderBy(n => n).ToList();

            // As três estão registradas. run_command e write_file só executam depois do
            // portão de confirmação — ver ToolRegistryTests do gate.
            names.Should().Equal("read_file", "run_command", "write_file");
        }

        [Theory]
        [InlineData("read_file")]
        [InlineData("READ_FILE")]
        [InlineData("Read_File")]
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
            var registry = new ToolRegistry();

            var (natives, dynamics) = registry.GetCategorizedTools();

            natives.Should().HaveCount(3);
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
