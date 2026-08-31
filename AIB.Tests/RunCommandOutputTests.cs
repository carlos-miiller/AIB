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
        public void ErroSerializadoDentroDoBloco_ChegaAoModelo()
        {
            // A primeira versão disto jogava o bloco inteiro fora, e com isso cegava o modelo:
            // um cmdlet que falhasse devolvia "sem saída", e ele seguia adiante achando que não
            // havia o que corrigir. O erro do PowerShell viaja DENTRO do CLIXML.
            string comErro =
                "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
                + "<S S=\"Error\">Import-Csv : Não é possível processar o arquivo.</S></Objs>";

            Limpar(comErro).Should().Contain("Import-Csv")
                .And.Contain("Não é possível processar o arquivo");
        }

        [Fact]
        public void ProgressoNaoContamina_OTextoDoErro()
        {
            string misturado =
                "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
                + "<Obj S=\"progress\" RefId=\"0\"><MS><AV>Preparando módulos para primeiro uso.</AV></MS></Obj>"
                + "<S S=\"Error\">Falha de verdade.</S></Objs>";

            string limpo = Limpar(misturado);

            limpo.Should().Contain("Falha de verdade");
            limpo.Should().NotContain("Preparando módulos");
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
