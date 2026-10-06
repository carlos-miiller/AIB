using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O indicador de "digitando" e a linha que o hospeda.
    /// <para>
    /// A bolha mora dentro de um Grid de linha, e não direto no painel de mensagens. Chamar
    /// <c>MessagesPanel.Children.Remove(bolha)</c> falha EM SILÊNCIO — a bolha não é filha do
    /// painel — e o indicador fica na tela para sempre. Foi assim que o /capitulo e o /ato
    /// terminavam com dois balões: o aviso de conclusão embaixo dos três pontinhos que nunca
    /// saíram.
    /// </para>
    /// <para>
    /// O <c>RemoverLinha</c> existe justamente para isso, e este ensaio existe para que a
    /// próxima chamada a <c>Children.Remove</c> apareça em vermelho em vez de na tela do
    /// usuário.
    /// </para>
    /// </summary>
    public class IndicadorDeDigitacaoTests
    {
        private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

        private static (FrameworkElement bubble, System.Windows.Threading.DispatcherTimer timer)
            Adicionar(ChatWindow janela) =>
            ((FrameworkElement, System.Windows.Threading.DispatcherTimer))
                typeof(ChatWindow).GetMethod("AddTypingIndicator", Privados)!.Invoke(janela, null)!;

        private static void Remover(ChatWindow janela, FrameworkElement bolha) =>
            typeof(ChatWindow).GetMethod("RemoverLinha", Privados)!
                .Invoke(janela, new object?[] { bolha });

        private static StackPanel Painel(ChatWindow janela) =>
            (StackPanel)janela.FindName("MessagesPanel");

        [Fact]
        public void ABolhaNaoEhFilhaDoPainel()
        {
            // A raiz do defeito, escrita como fato: quem tenta remover a bolha direto do painel
            // não remove nada, e não recebe erro nenhum por isso.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();
                var painel = Painel(janela);

                int antes = painel.Children.Count;
                var indicador = Adicionar(janela);

                painel.Children.Count.Should().Be(antes + 1, "a LINHA entrou no painel");
                painel.Children.Contains(indicador.bubble).Should().BeFalse(
                    "a bolha mora dentro do Grid da linha");

                painel.Children.Remove(indicador.bubble);
                painel.Children.Count.Should().Be(antes + 1, "remover a bolha direto não faz nada");

                indicador.timer.Stop();
                janela.Close();
            });
        }

        [Fact]
        public void RemoverLinha_TiraOIndicadorDaTela()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();
                var painel = Painel(janela);

                int antes = painel.Children.Count;
                var indicador = Adicionar(janela);
                indicador.timer.Stop();

                Remover(janela, indicador.bubble);

                painel.Children.Count.Should().Be(antes);

                janela.Close();
            });
        }
    }
}
