using System.Windows;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Onde o orbe do Shadow Assistant para na tela — shadow-assistant.html §1.
    /// <para>
    /// A spec marca esta seção como "leia inteira antes de codar", e a armadilha A1 é usar a
    /// altura da tela em vez da área de trabalho. Estes ensaios existem para exercitar as
    /// configurações que uma máquina só tem uma de cada vez: barra de tarefas embaixo, em cima,
    /// à esquerda, oculta, e monitor que não começa em zero.
    /// </para>
    /// </summary>
    public class ScreenAnchorTests
    {
        /// <summary>Tela de 1920x1080 com barra de tarefas de 48px embaixo.</summary>
        private static readonly Rect BarraEmbaixo = new(0, 0, 1920, 1032);

        [Fact]
        public void CentralizaNaHorizontal()
        {
            var canto = ScreenAnchorService.Calcular(BarraEmbaixo, largura: 600, altura: 136);

            canto.X.Should().Be((1920 - 600) / 2.0);
        }

        [Fact]
        public void ABaseFicaAQuarentaECincoDaBarra()
        {
            // Sem margem de sombra, a base da janela É a base do desenho.
            var canto = ScreenAnchorService.Calcular(BarraEmbaixo, largura: 600, altura: 136);

            (canto.Y + 136).Should().Be(1032 - 45);
        }

        [Fact]
        public void AMargemDaSombraNaoEmpurraOOrbeParaCima()
        {
            // A janela é maior que o desenho porque a DropShadow precisa de área. Sem descontar
            // essa margem, a folga de 45px seria medida a partir do fim da sombra e o orbe
            // subiria 40px sem ninguém pedir.
            var comMargem = ScreenAnchorService.Calcular(
                BarraEmbaixo, largura: 600, altura: 136, margemInferior: 40);

            double baseDoDesenho = comMargem.Y + 136 - 40;

            baseDoDesenho.Should().Be(1032 - 45);
        }

        [Fact]
        public void BarraDeTarefasEmCima_OOrbeDesce()
        {
            // Área de trabalho começa em Y=48 e termina em 1080: a barra está no topo.
            var canto = ScreenAnchorService.Calcular(
                new Rect(0, 48, 1920, 1032), largura: 600, altura: 136);

            (canto.Y + 136).Should().Be(1080 - 45, "a base agora é a borda de baixo da tela");
        }

        [Fact]
        public void BarraDeTarefasAEsquerda_OCentroAcompanha()
        {
            // A1: com a barra à esquerda, a área de trabalho começa em X=64. Centralizar pela
            // largura da TELA jogaria o orbe 32px para a esquerda do centro visível.
            var canto = ScreenAnchorService.Calcular(
                new Rect(64, 0, 1856, 1080), largura: 600, altura: 136);

            canto.X.Should().Be(64 + (1856 - 600) / 2.0);
        }

        [Fact]
        public void BarraOcultaAutomaticamente_UsaATelaInteira()
        {
            // Com a barra oculta, WorkArea passa a ser a tela toda — e o orbe desce junto,
            // de graça, sem nenhum código sobre auto-hide.
            var canto = ScreenAnchorService.Calcular(
                new Rect(0, 0, 1920, 1080), largura: 600, altura: 136);

            (canto.Y + 136).Should().Be(1080 - 45);
        }

        [Fact]
        public void MonitorSecundarioComOrigemNegativa()
        {
            // Monitor à esquerda do primário tem X negativo no Windows. A conta é a mesma, mas
            // uma implementação que assumisse origem em zero devolveria um orbe fora da tela.
            var canto = ScreenAnchorService.Calcular(
                new Rect(-1920, 0, 1920, 1032), largura: 600, altura: 136);

            canto.X.Should().Be(-1920 + (1920 - 600) / 2.0);
        }

        [Fact]
        public void OMorphMudaAAltura_EABaseNaoSeMexe()
        {
            // §6: o morph muda a altura da janela (SizeToContent), e SizeChanged reposiciona.
            // Se a base se mexesse, a barra apareceria deslocada da posição do orbe — e o
            // usuário veria o elemento "pular" no meio da animação.
            var orbe  = ScreenAnchorService.Calcular(BarraEmbaixo, 600, altura: 136, margemInferior: 40);
            var barra = ScreenAnchorService.Calcular(BarraEmbaixo, 600, altura: 132, margemInferior: 40);

            (orbe.Y + 136 - 40).Should().Be(barra.Y + 132 - 40);
        }
    }
}
