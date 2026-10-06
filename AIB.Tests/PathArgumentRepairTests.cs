using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Reparo de caminhos com escape JSON mal emitido pelo modelo.
    /// Medido com qwen3.5:4b: pedido "C:\temp\ola.txt" chegou à ferramenta como "C:\emp\ola.txt",
    /// porque o modelo escreveu \t cru no JSON e o parser leu como TAB.
    /// </summary>
    public class PathArgumentRepairTests
    {
        [Theory]
        // O caso real medido: TAB no meio do caminho.
        [InlineData("C:\temp\\ola.txt", @"C:\temp\ola.txt")]
        // Toda pasta que comece com as letras de escape sofre o mesmo.
        [InlineData("C:\novo\\a.txt", @"C:\novo\a.txt")]
        [InlineData("C:\reports\\a.txt", @"C:\reports\a.txt")]
        [InlineData("C:\backup\\a.txt", @"C:\backup\a.txt")]
        [InlineData("C:\fotos\\a.txt", @"C:\fotos\a.txt")]
        public void CaracterDeControle_VoltaAoEscapeLiteral(string recebido, string esperado)
        {
            PathArgumentRepair.Normalize(recebido, out bool reparado).Should().Be(esperado);
            reparado.Should().BeTrue();
        }

        [Theory]
        [InlineData(@"C:\dados\ola.txt")]
        [InlineData(@"C:\Users\Carlo\Documents\arquivo.md")]
        [InlineData("relativo/sem/barra.txt")]
        [InlineData(@"C:\pasta com espaço\x.txt")]
        // 'd' não é letra de escape: \dados chega intacto e não deve ser tocado.
        [InlineData(@"C:\dev\projeto\src\main.cs")]
        public void CaminhoIntacto_NaoEhAlterado(string caminho)
        {
            PathArgumentRepair.Normalize(caminho, out bool reparado).Should().Be(caminho);
            reparado.Should().BeFalse("mexer num caminho já correto é que seria o bug");
        }

        [Fact]
        public void MultiplosEscapes_NoMesmoCaminho_SaoTodosRevertidos()
        {
            PathArgumentRepair.Normalize("C:\temp\notas\relatorio.txt", out bool reparado)
                .Should().Be(@"C:\temp\notas\relatorio.txt");
            reparado.Should().BeTrue();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EntradaVazia_NaoLanca(string? entrada)
        {
            PathArgumentRepair.Normalize(entrada, out bool reparado).Should().BeEmpty();
            reparado.Should().BeFalse();
        }
    }
}
