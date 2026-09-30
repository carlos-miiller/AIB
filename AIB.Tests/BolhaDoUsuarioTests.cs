using System.Windows;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>O texto da bolha do usuário se seleciona e copia, mas não se edita.</summary>
    public class BolhaDoUsuarioTests
    {
        [Fact]
        public void OTexto_EhSelecionavel_ESomenteLeitura()
        {
            WpfHost.EmSta(() =>
            {
                var caixa = ChatWindow.TextoSelecionavel("C:/Users/Carlo/CPAPS");

                caixa.Text.Should().Be("C:/Users/Carlo/CPAPS");
                caixa.IsReadOnly.Should().BeTrue("selecionar sim, editar a pergunta enviada não");
                caixa.IsReadOnlyCaretVisible.Should().BeFalse();
                caixa.TextWrapping.Should().Be(TextWrapping.Wrap);
                caixa.BorderThickness.Should().Be(new Thickness(0));

                caixa.SelectAll();
                caixa.SelectedText.Should().Be("C:/Users/Carlo/CPAPS");
            });
        }
    }
}
