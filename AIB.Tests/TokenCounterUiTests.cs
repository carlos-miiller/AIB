using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O contador de tokens do rodapé — §3.8.
    /// <para>
    /// O que ele mede mudou: era a fatia do prompt que o cache do Ollama não precisou
    /// reprocessar — uma medida do servidor, que ia a zero quando outra completação despejava a
    /// fatia de KV e não dizia nada sobre a AIB. Agora mede o sistema de capítulos e atos:
    /// quanto a conversa pesaria inteira contra quanto ela pesa depois de resumida.
    /// </para>
    /// </summary>
    public class TokenCounterUiTests
    {
        private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

        private static void Atualizar(ChatWindow janela, TokenReport relatorio) =>
            typeof(ChatWindow).GetMethod("UpdateTokenCounterUI", Privados)!
                .Invoke(janela, new object?[] { relatorio });

        private static string Texto(ChatWindow janela) =>
            ((TextBlock)janela.FindName("TokenCounterText")).Text;

        private static Brush Cor(ChatWindow janela) =>
            ((TextBlock)janela.FindName("TokenCounterText")).Foreground;

        [Fact]
        public void ComEconomia_MostraOsDoisNumerosEAPorcentagem()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(Total: 12000, Contexto: 3000, Max: 8704));

                Texto(janela).Should().Contain(">", "a seta separa o que a conversa pesaria do que ela pesa");
                Texto(janela).Should().Contain("(-75%)");

                janela.Close();
            });
        }

        [Fact]
        public void SemCompactacao_NaoRepeteONumeroNemInventaPorcentagem()
        {
            // Antes do primeiro capítulo os dois números são o mesmo. Escrever "1.204 > 1.204
            // (-0%)" ocuparia o rodapé para não dizer nada, e o "-0%" ainda acusaria o sistema
            // de não estar economizando quando ele só não teve o que fazer.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(Total: 1204, Contexto: 1204, Max: 8704));

                Texto(janela).Should().NotContain(">");
                Texto(janela).Should().NotContain("%");
                Texto(janela).Should().Contain("tokens");

                janela.Close();
            });
        }

        [Fact]
        public void SemEconomia_ACorEhNeutraENaoAlarme()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(Total: 12000, Contexto: 3000, Max: 8704));
                var comEconomia = Cor(janela);

                Atualizar(janela, new TokenReport(Total: 1204, Contexto: 1204, Max: 8704));

                Cor(janela).Should().NotBeSameAs(comEconomia);
                Cor(janela).Should().BeSameAs(janela.FindResource("TextSecondaryBrush"));

                janela.Close();
            });
        }

        [Fact]
        public void EconomiaAlta_PintaDeVerde()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(Total: 10000, Contexto: 2000, Max: 8704));

                Cor(janela).Should().BeSameAs(janela.FindResource("SuccessBrush"));

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // A conta, sem interface
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(1000, 250, 75)]
        [InlineData(1000, 900, 10)]
        [InlineData(3, 1, 67)]
        public void EconomiaPct_EhAFracaoPoupada(int total, int contexto, int esperado) =>
            new TokenReport(total, contexto, 8704).EconomiaPct.Should().Be(esperado);

        [Theory]
        [InlineData(1000, 1000)]
        [InlineData(0, 0)]
        [InlineData(500, 900)]
        public void SemGanho_NaoHaPorcentagem(int total, int contexto) =>
            new TokenReport(total, contexto, 8704).EconomiaPct.Should().BeNull(
                "null diz 'ainda não houve compactação'; zero afirmaria que ela rodou e falhou");
    }
}
