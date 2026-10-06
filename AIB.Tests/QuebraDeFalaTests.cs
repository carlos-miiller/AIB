using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A resposta em mais de um balão. Pedido: "se eu falo para ela fazer algo e solto uma piada
    /// junto, ela reage e responde na mesma mensagem".
    /// </summary>
    public class QuebraDeFalaTests
    {
        [Fact]
        public void AMarca_DivideEmFalas_SemAMarcaNemVazias()
        {
            QuebraDeFala.Dividir("Ha— prometo não julgar.\n⁂\nPronto: 112 arquivos.\n⁂\n")
                .Should().Equal("Ha— prometo não julgar.", "Pronto: 112 arquivos.");
        }

        [Fact]
        public void SemMarca_EhUmaFalaSo()
        {
            QuebraDeFala.Dividir("Uma linha.\n\n---\n\nOutra, no mesmo balão.")
                .Should().ContainSingle("--- e linha em branco são Markdown, não quebra");
        }

        [Fact]
        public void NoStreaming_SoFechaOQueVeioAntesDaUltimaMarca()
        {
            var (prontas, resto) = QuebraDeFala.Separar("Ha— prometo.\n⁂\nPronto: 11");

            prontas.Should().Equal("Ha— prometo.");
            resto.Trim().Should().Be("Pronto: 11", "ainda está sendo escrito");

            QuebraDeFala.Separar("Sem marca ainda").Prontas.Should().BeEmpty();
        }

        [Fact]
        public void OrbeENotificacao_RecebemOTextoSemAMarca()
        {
            QuebraDeFala.Limpar("Ha.\n⁂\nPronto.").Should().Be("Ha.\n\nPronto.");
            QuebraDeFala.Limpar("Nada a limpar.").Should().Be("Nada a limpar.");
        }
    }
}
