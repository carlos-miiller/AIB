using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A espessura das barras de rolagem.
    /// <para>
    /// Existe por causa de uma armadilha do WPF que não dá erro nenhum: um estilo implícito
    /// não substitui o estilo de tema inteiro. As propriedades que ele NÃO define continuam
    /// vindo do tema — e o tema traz <c>MinWidth</c>/<c>MinHeight</c> de
    /// <c>SystemParameters</c>, que no Windows valem 17. A barra da trilha de ícones estava
    /// com <c>Height=5</c> no estilo e 17 na tela.
    /// </para>
    /// <para>
    /// Por isso o ensaio mede a ALTURA REAL depois do arranjo, e não o valor da propriedade:
    /// era exatamente a diferença entre os dois que escondia o defeito.
    /// </para>
    /// </summary>
    public class ScrollBarSizeTests
    {
        [Fact]
        public void BarraDaTrilhaDeIcones_TemCincoPixels()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var cadeia = new ToolChainView();

                // Nove ações para a trilha passar dos 142px e a barra aparecer.
                for (int i = 0; i < 9; i++)
                {
                    cadeia.Iniciar("id" + i, "read", "arquivo" + i + ".cs");
                    cadeia.Concluir("id" + i, false, false, null, null);
                }

                typeof(ToolChainView).GetMethod("DrenarParaEnsaio",
                        System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance)!
                    .Invoke(cadeia, null);

                var hospedeira = new Window { Width = 700, Height = 400, Content = cadeia };
                hospedeira.Show();
                hospedeira.UpdateLayout();

                var trilha = (ScrollViewer)cadeia.FindName("TrilhaScroll");
                trilha.UpdateLayout();

                var barra = Descendentes(trilha).OfType<ScrollBar>()
                    .SingleOrDefault(b => b.Orientation == Orientation.Horizontal
                                          && b.Visibility == Visibility.Visible);

                barra.Should().NotBeNull("com 9 ações a trilha transborda e a barra aparece");
                barra!.ActualHeight.Should().Be(5);

                hospedeira.Close();
            });
        }

        private static IEnumerable<DependencyObject> Descendentes(DependencyObject raiz)
        {
            int n = VisualTreeHelper.GetChildrenCount(raiz);
            for (int i = 0; i < n; i++)
            {
                var filho = VisualTreeHelper.GetChild(raiz, i);
                yield return filho;
                foreach (var neto in Descendentes(filho)) yield return neto;
            }
        }
    }
}
