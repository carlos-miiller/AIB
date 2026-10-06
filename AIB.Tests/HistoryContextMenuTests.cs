using System;
using System.Linq;
using System.Windows.Controls;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O menu do botão direito no histórico de conversas.
    /// <para>
    /// As três opções não são variações da mesma coisa. Recuperar SOMA a conversa antiga à
    /// que está na tela; abrir SUBSTITUI a que está na tela; excluir apaga o registro e não
    /// toca na tela. O ensaio trava a existência das três e a ligação de cada uma com o seu
    /// callback — trocar dois deles de lugar seria invisível de outra forma.
    /// </para>
    /// </summary>
    public class HistoryContextMenuTests
    {
        private static ContextMenu Menu(SidePanelWindow painel, ChatSession sessao) =>
            (ContextMenu)typeof(SidePanelWindow)
                .GetMethod("MenuDaConversa", System.Reflection.BindingFlags.NonPublic
                                           | System.Reflection.BindingFlags.Instance)!
                .Invoke(painel, new object[] { sessao })!;

        [Fact]
        public void OMenu_TemAsTresOpcoesNaOrdem()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var painel = new SidePanelWindow();
                var itens = Menu(painel, new ChatSession { Title = "x" })
                    .Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList();

                itens.Should().Equal(
                    "Recuperar contexto na conversa atual",
                    "Abrir conversa",
                    "Excluir");

                painel.Close();
            });
        }

        [Fact]
        public void CadaOpcao_ChamaOSeuProprioCallback()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                ChatSession? recuperado = null, aberto = null, excluido = null;

                var painel = new SidePanelWindow(
                    s => recuperado = s,
                    s => aberto = s,
                    s => excluido = s);

                var sessao = new ChatSession { Title = "conversa de ontem" };
                var itens = Menu(painel, sessao).Items.OfType<MenuItem>().ToList();

                itens[0].RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                recuperado.Should().BeSameAs(sessao);
                aberto.Should().BeNull("recuperar não pode trocar a conversa da tela");
                excluido.Should().BeNull();

                itens[1].RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                aberto.Should().BeSameAs(sessao);
                excluido.Should().BeNull();

                itens[2].RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                excluido.Should().BeSameAs(sessao);

                painel.Close();
            });
        }

        [Fact]
        public void ExcluirEstaMarcadoComoPerigo()
        {
            // A marca é o que pinta o item de vermelho no hover. Sem ela o item que apaga fica
            // com a mesma aparência dos que não apagam.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var painel = new SidePanelWindow();
                var itens = Menu(painel, new ChatSession()).Items.OfType<MenuItem>().ToList();

                itens[2].Tag.Should().Be("perigo");
                itens[0].Tag.Should().BeNull();
                itens[1].Tag.Should().BeNull();

                painel.Close();
            });
        }
    }
}
