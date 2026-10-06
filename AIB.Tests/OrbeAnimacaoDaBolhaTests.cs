using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIB.Ui;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A entrada da bolha — §4.6, fade + deslocamento de 8px.
    /// <para>
    /// Existe por causa de uma armadilha do WPF que nenhum outro ensaio daqui pega: o framework
    /// CONGELA os Freezable declarados dentro de um template sempre que pode, e animar um
    /// Freezable congelado é exceção em tempo de execução. A translação da bolha é animada por
    /// um gatilho Loaded do próprio DataTemplate, e Loaded só dispara com a janela na tela —
    /// os outros ensaios do orbe nunca chamam Show(), então todos passariam com a animação
    /// quebrada e o usuário veria a exceção.
    /// </para>
    /// <para>
    /// A verificação é feita instanciando o template, e não mostrando a janela: pôr uma janela
    /// Topmost na thread STA compartilhada desestabiliza os ensaios das outras classes, e o que
    /// interessa aqui — se o transform saiu congelado — já está decidido na instanciação.
    /// </para>
    /// </summary>
    public class OrbeAnimacaoDaBolhaTests
    {
        [Theory]
        [InlineData(typeof(FalaDaIA))]
        [InlineData(typeof(FalaDoUsuario))]
        public void OTransformDaBolha_NaoSaiCongeladoDoTemplate(System.Type tipoDaFala)
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                var pilha = (ItemsControl)janela.FindName("PilhaDeFalas");
                var template = (DataTemplate)pilha.Resources[new DataTemplateKey(tipoDaFala)];

                var bolha = (Border)template.LoadContent();
                var deslocamento = (TranslateTransform)bolha.RenderTransform;

                deslocamento.IsFrozen.Should().BeFalse(
                    "o gatilho Loaded do template anima este transform; congelado, ele explode");
                deslocamento.Y.Should().Be(8, "a bolha entra 8px abaixo e sobe — §4.6");

                janela.Close();
            });
        }
    }
}
