using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A caixa de digitação das ComboBox editáveis — os dois campos de modelo LLM.
    /// <para>
    /// O template não tinha <c>PART_EditableTextBox</c>, que é o nome exato que o WPF procura
    /// numa ComboBox editável. Sem ele o campo não tem onde desenhar nem onde receber texto: o
    /// que aparecia era o desenho que o sistema faz por conta própria, na cor dele — escura
    /// sobre um fundo quase preto. O menu suspenso nunca teve o problema porque os itens dele
    /// definem a própria cor.
    /// </para>
    /// </summary>
    public class ComboEditavelTests
    {
        private static ComboBox Montar(bool editavel, string estilo = "ControlComboBoxMono")
        {
            var combo = new ComboBox
            {
                IsEditable = editavel,
                Style = (Style)System.Windows.Application.Current.Resources[estilo],
                ItemsSource = new[] { "gemma3:4b", "qwen2.5:7b" },
                Width = 220
            };

            // Sem medir, o template não é aplicado e não há parte nenhuma para achar. Medir
            // basta: mostrar a janela desestabiliza a suíte inteira, por causa do STA
            // compartilhado.
            combo.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            combo.ApplyTemplate();
            return combo;
        }

        private static TextBox? CaixaDeDigitacao(ComboBox combo) =>
            combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;

        [Theory]
        [InlineData("ControlComboBox")]
        [InlineData("ControlComboBoxMono")]
        public void ComboEditavel_TEM_ACaixaComONomeQueOWpfProcura(string estilo)
        {
            // "PART_EditableTextBox" não é convenção: é o nome literal que o ComboBox busca no
            // OnApplyTemplate. Qualquer outro e a parte simplesmente não existe para ele.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                CaixaDeDigitacao(Montar(editavel: true, estilo))
                    .Should().NotBeNull(estilo + " precisa da caixa de digitação");
            });
        }

        [Fact]
        public void OTextoDigitado_SAI_NaCorDoTema()
        {
            // O defeito relatado. O fundo do controle é quase preto; texto na cor padrão do
            // sistema fica ilegível.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var caixa = CaixaDeDigitacao(Montar(editavel: true))!;
                var esperado = (SolidColorBrush)System.Windows.Application.Current.Resources["TextNormalBrush"];

                ((SolidColorBrush)caixa.Foreground).Color.Should().Be(esperado.Color);
            });
        }

        [Fact]
        public void OCursor_TAMBEM_SAI_Claro()
        {
            // O cursor de texto padrão é preto. Num campo escuro ele some, e o usuário não
            // enxerga onde está digitando mesmo com o texto legível.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var caixa = CaixaDeDigitacao(Montar(editavel: true))!;
                var esperado = (SolidColorBrush)System.Windows.Application.Current.Resources["TextNormalBrush"];

                ((SolidColorBrush)caixa.CaretBrush).Color.Should().Be(esperado.Color);
            });
        }

        [Fact]
        public void ComboEditavel_MOSTRA_ACaixa_EESCONDE_OItemSelecionado()
        {
            // Os dois visíveis ao mesmo tempo sobrepõem o texto: ele sai dobrado, meio borrado,
            // e ninguém entende de onde vem.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var combo = Montar(editavel: true);

                CaixaDeDigitacao(combo)!.Visibility.Should().Be(Visibility.Visible);
                ((ContentPresenter)combo.Template.FindName("Selecionado", combo))
                    .Visibility.Should().Be(Visibility.Collapsed);
            });
        }

        [Fact]
        public void ComboFIXA_ContinuaMostrando_OItemSelecionado()
        {
            // As ComboBox não editáveis da tela — personagem, provedor, keep-alive — não podem
            // ter regredido: elas nunca passam pela caixa de digitação.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var combo = Montar(editavel: false);

                ((ContentPresenter)combo.Template.FindName("Selecionado", combo))
                    .Visibility.Should().Be(Visibility.Visible);
                CaixaDeDigitacao(combo)!.Visibility.Should().Be(Visibility.Collapsed);
            });
        }
    }
}
