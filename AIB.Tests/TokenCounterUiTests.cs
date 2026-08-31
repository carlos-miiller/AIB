using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O contador de tokens do rodapé — §3.8.
    /// <para>
    /// Ele pinta por ECONOMIA de cache, não por ocupação: verde quer dizer "o cache está
    /// trabalhando", magenta quer dizer "cada turno está sendo reenviado inteiro". Por isso a
    /// porcentagem precisa estar certa: a cor mente junto com ela.
    /// </para>
    /// </summary>
    public class TokenCounterUiTests
    {
        private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

        private static void Atualizar(ChatWindow janela, int atual, int max, int? cache) =>
            typeof(ChatWindow).GetMethod("UpdateTokenCounterUI", Privados)!
                .Invoke(janela, new object?[] { atual, max, cache });

        private static string Texto(ChatWindow janela) =>
            ((TextBlock)janela.FindName("TokenCounterText")).Text;

        private static Brush Cor(ChatWindow janela) =>
            ((TextBlock)janela.FindName("TokenCounterText")).Foreground;

        [Fact]
        public void SemMedicaoNova_APorcentagemAnteriorEMantida()
        {
            // O defeito: a notificação do fim do turno não traz medição de cache, e o contador
            // dividia a medição ANTIGA pelo total NOVO — que acabara de crescer com a resposta
            // inteira. A conta desabava e o texto ficava magenta bem quando a resposta chegava.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, atual: 1000, max: 8704, cache: 910);
                Texto(janela).Should().Contain("(-91%)");
                var verde = Cor(janela);

                // Fim do turno: total maior, nenhuma medição de cache.
                Atualizar(janela, atual: 2600, max: 8704, cache: null);

                Texto(janela).Should().Contain("2600/8704");
                Texto(janela).Should().Contain("(-91%)", "sem medição nova, repete a última");
                Cor(janela).Should().BeSameAs(verde, "a cor não pode virar alarme por falta de dado");

                janela.Close();
            });
        }

        [Fact]
        public void MedicaoNova_SubstituiAAnterior()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, atual: 1000, max: 8704, cache: 910);
                Atualizar(janela, atual: 1000, max: 8704, cache: 100);

                Texto(janela).Should().Contain("(-10%)");

                janela.Close();
            });
        }

        [Fact]
        public void SemNenhumaMedicao_NaoInventaPorcentagem()
        {
            // Antes da primeira medição não há economia a mostrar. Escrever "(-0%)" afirmaria
            // que o cache não está funcionando, quando o certo é "ainda não se sabe".
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, atual: 500, max: 8704, cache: null);

                Texto(janela).Should().Be("500/8704 tokens");

                janela.Close();
            });
        }
    }
}
