using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A barra de digitação da conversa e a janela fixa.
    /// <para>
    /// A caixa crescia até 110 px e parava: um texto colado de cinquenta linhas mostrava cinco.
    /// E os itens da barra (✦, câmera, microfone, enviar) eram centrados na vertical — com a
    /// caixa alta, subiam para o meio dela.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class BarraDeDigitacaoTests
    {
        private static void Montar(ChatWindow chat) => chat.UpdateLayout();

        private static double BaseDe(FrameworkElement item, FrameworkElement barra) =>
            item.TransformToAncestor(barra).Transform(new Point(0, item.ActualHeight)).Y;

        [Fact]
        public void OTeto_EhMetadeDaJanela()
        {
            ChatWindow.TetoDaDigitacao(520).Should().Be(260);
            ChatWindow.TetoDaDigitacao(0).Should().Be(32, "nunca menor que uma linha");
        }

        [Fact]
        public void ACaixa_CresceAteMetadeDaJanela_EOsItensFicamEmbaixo()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var chat = JanelaDeEnsaio.Nova();
                var caixa = (TextBox)chat.FindName("InputBox");
                var barra = (FrameworkElement)chat.FindName("BarraDeInput");
                var enviar = (FrameworkElement)chat.FindName("SendButton");
                var quadro = (FrameworkElement)chat.FindName("MainAreaGrid");

                // Só mostrada a janela ganha tamanho de verdade.
                chat.Show();
                Montar(chat);
                double folgaComUmaLinha = barra.ActualHeight - BaseDe(enviar, barra);
                double alturaComUmaLinha = barra.ActualHeight;

                caixa.Text = string.Join("\n", Enumerable.Range(1, 80).Select(i => "linha " + i));
                Montar(chat);

                double teto = quadro.ActualHeight * ChatWindow.FracaoDaDigitacao;
                caixa.MaxHeight.Should().BeApproximately(teto, 0.5);
                caixa.ActualHeight.Should().BeApproximately(teto, 1, "oitenta linhas enchem o teto");
                caixa.ActualHeight.Should().BeGreaterThan(110, "o teto antigo");

                barra.ActualHeight.Should().BeGreaterThan(alturaComUmaLinha);
                (barra.ActualHeight - BaseDe(enviar, barra)).Should().BeApproximately(folgaComUmaLinha, 0.5,
                    "o botão fica à mesma distância da borda de baixo, com uma linha ou com oitenta");

                chat.Close();
            });
        }

        [Fact]
        public void AJanela_NaoSeArrasta()
        {
            // O cabeçalho tinha MouseDown → DragMove e o cursor de mover.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "AIBWindows"))) dir = dir.Parent;
            string views = Path.Combine(dir!.FullName, "AIBWindows", "Views");

            File.ReadAllText(Path.Combine(views, "ChatWindow.xaml.cs")).Should().NotContain("DragMove");
            File.ReadAllText(Path.Combine(views, "ChatWindow.xaml"))
                .Should().NotContain("Window_MouseDown").And.NotContain("SizeAll");

            // O cabeçalho do painel lateral também movia a conversa (Owner.Left/Top).
            File.ReadAllText(Path.Combine(views, "SidePanelWindow.xaml.cs"))
                .Should().NotContain("DragMove").And.NotContain("Owner.Left");
        }
    }
}
