using System.Reflection;
using System.Windows.Controls;
using System.Windows.Shapes;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O botão primário do chat: aviãozinho, quadrado de parar, e de volta ao aviãozinho.
    /// <para>
    /// Existe por causa de um defeito que só aparecia DEPOIS do primeiro envio: durante o turno
    /// o botão recebia o caractere "■" pintado com um vermelho escrito à mão no código, e no fim
    /// recebia "➔" — uma seta de texto no lugar do desenho do XAML. O aviãozinho sumia da tela
    /// para o resto da sessão, e ninguém que olhasse só o primeiro turno veria isso.
    /// </para>
    /// <para>
    /// O turno inteiro não roda em ensaio: ele depende do modelo, da rede e do relógio. O que se
    /// trava aqui é o que o turno faz COM O BOTÃO — entrar no estado de parar e sair dele —,
    /// que é exatamente onde o defeito morava.
    /// </para>
    /// </summary>
    public class BotaoDeEnviarTests
    {
        private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

        private static Button Botao(ChatWindow janela) => (Button)janela.FindName("SendButton");

        private static Path IconeDoXaml(ChatWindow janela) => (Path)janela.FindName("SendIcon");

        private static void Parar(ChatWindow janela, string dica) =>
            typeof(ChatWindow).GetMethod("AplicarIconeDeParar", Privados)!
                .Invoke(janela, new object?[] { dica });

        private static void Restaurar(ChatWindow janela) =>
            typeof(ChatWindow).GetMethod("RestaurarIconeDeEnviar", Privados)!
                .Invoke(janela, null);

        [Fact]
        public void DepoisDoTurno_OBotaoVoltaAoAviaozinhoDoXaml()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                var botao = Botao(janela);
                object original = botao.Content;
                original.Should().BeSameAs(IconeDoXaml(janela), "o botão nasce com o desenho do XAML");

                // O turno, na parte que toca o botão: começa e termina.
                Parar(janela, "Parar a resposta");
                botao.Content.Should().NotBeSameAs(original, "durante o turno o botão é PARAR");

                Restaurar(janela);

                botao.Content.Should().BeSameAs(original,
                    "no fim do turno volta o MESMO desenho, e não uma seta de texto no lugar dele");
                botao.ToolTip.Should().Be("Enviar");

                janela.Close();
            });
        }

        [Fact]
        public void DoisTurnosSeguidos_NaoDeixamRestoDoAnterior()
        {
            // O defeito era cumulativo: o segundo turno já começava de um botão errado. Se o
            // primeiro fecha direito, o segundo encontra o mesmo estado inicial.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                var botao = Botao(janela);
                object original = botao.Content;

                for (int turno = 0; turno < 2; turno++)
                {
                    Parar(janela, "Parar a resposta");
                    Restaurar(janela);
                }

                botao.Content.Should().BeSameAs(original);

                janela.Close();
            });
        }

        [Fact]
        public void OQuadradoDeParar_SaiDoTokenDeCorDestrutiva()
        {
            // A cor de parar era um hex escrito no código (FF5555) que não era nenhum dos
            // vermelhos da paleta: trocar o tema deixava o botão para trás.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Parar(janela, "Parar a resposta");

                var quadrado = Botao(janela).Content.Should().BeOfType<Border>().Subject;
                quadrado.Background.Should().BeSameAs(janela.FindResource("DangerBrush"),
                    "a mesma cor que pinta toda ação destrutiva da interface");

                janela.Close();
            });
        }

        [Fact]
        public void AGravacao_UsaOMesmoParEEspecificaODeQueEstaParando()
        {
            // Gravação e turno compartilham o desenho, mas não a frase: "parar" sozinho não diz
            // se o que morre é a escuta ou a resposta.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                var gravando = typeof(ChatWindow).GetMethod("AplicarEstadoDeGravacao", Privados);
                gravando.Should().NotBeNull();

                var botao = Botao(janela);
                object original = botao.Content;

                gravando!.Invoke(janela, new object?[] { true });
                botao.Content.Should().BeOfType<Border>();
                botao.ToolTip.Should().Be("Parar de gravar");

                gravando.Invoke(janela, new object?[] { false });
                botao.Content.Should().BeSameAs(original);
                botao.ToolTip.Should().Be("Enviar");

                janela.Close();
            });
        }
    }
}
