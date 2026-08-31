using System.Reflection;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A limpeza da saída de erro do PowerShell.
    /// <para>
    /// Com stdout e stderr redirecionados, o PowerShell serializa em CLIXML tudo que não é
    /// texto e despeja no stderr. Um <c>Get-ChildItem -Recurse</c> devolvia meio kilobyte de
    /// <c>&lt;Obj S="progress"&gt;</c> junto com três linhas de resultado útil — e isso ia
    /// inteiro para o histórico e para o resumo do capítulo.
    /// </para>
    /// </summary>
    public class RunCommandOutputTests
    {
        private static string Limpar(string? stderr) =>
            (string)typeof(RunCommandTool)
                .GetMethod("SemClixml", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object?[] { stderr })!;

        private const string Ruido =
            "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
            + "<Obj S=\"progress\" RefId=\"0\"><MS><AV>Preparando módulos para primeiro uso.</AV></MS></Obj></Objs>";

        [Fact]
        public void BlocoDeProgresso_SaiInteiro()
        {
            Limpar(Ruido).Should().BeEmpty();
        }

        [Fact]
        public void ErroDeVerdade_AntesDoBloco_EPreservado()
        {
            // Erro real e CLIXML saem pelo mesmo cano. Cortar do começo ao fim engoliria o erro
            // junto com o ruído, e o modelo ficaria cego para a falha que precisa corrigir.
            Limpar("Get-ChildItem: acesso negado ao caminho X.\r\n" + Ruido)
                .Should().Contain("acesso negado");
        }

        [Fact]
        public void SemBloco_NadaMuda()
        {
            Limpar("erro comum").Should().Be("erro comum");
            Limpar("").Should().BeEmpty();
            Limpar(null).Should().BeEmpty();
        }
    }
}
