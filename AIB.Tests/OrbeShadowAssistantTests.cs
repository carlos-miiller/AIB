using System.Globalization;
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
    /// A casca do orbe e o morph — shadow-assistant.html §4.2, §4.3 e §6.
    /// </summary>
    public class OrbeShadowAssistantTests
    {
        // ─────────────────────────────────────────────────────────────────────
        // O conversor que dispensa a animação de CornerRadius
        // ─────────────────────────────────────────────────────────────────────

        private static CornerRadius Raio(double altura) =>
            (CornerRadius)new AlturaParaRaioConverter()
                .Convert(altura, typeof(CornerRadius), null!, CultureInfo.InvariantCulture);

        [Theory]
        [InlineData(56, 28)]     // orbe: §2 orbRadius
        [InlineData(52, 26)]     // barra: §2 barRadius
        [InlineData(53, 26.5)]   // casca interna do orbe (56 - 2 x 1,5 de borda)
        [InlineData(49, 24.5)]   // casca interna da barra (52 - 2 x 1,5)
        public void OsRaiosDaSpecSaoMetadeDaAltura(double altura, double esperado)
        {
            // É por isso que o morph não precisa animar CornerRadius: os quatro raios que a
            // spec cita em número já são altura/2. Se algum dia um deles deixar de ser, este
            // ensaio quebra e o conversor deixa de servir.
            Raio(altura).TopLeft.Should().Be(esperado);
        }

        [Fact]
        public void AlturaAindaNaoMedida_NaoLancaNemDaRaioNegativo()
        {
            // ActualHeight é 0 até o primeiro layout, e CornerRadius negativo lança.
            Raio(0).Should().Be(new CornerRadius(0));
            Raio(double.NaN).Should().Be(new CornerRadius(0));
        }

        // ─────────────────────────────────────────────────────────────────────
        // A janela
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AJanela_NaoRoubaFocoNemApareceNaBarraDeTarefas()
        {
            // §0 O8 e §8 A3. Um orbe que rouba o foco enquanto o usuário digita em outro app
            // é motivo para desinstalar o programa.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                janela.ShowActivated.Should().BeFalse();
                janela.ShowInTaskbar.Should().BeFalse();
                janela.Topmost.Should().BeTrue();
                janela.ResizeMode.Should().Be(ResizeMode.NoResize);
                janela.WindowStyle.Should().Be(WindowStyle.None);
                janela.AllowsTransparency.Should().BeTrue();

                janela.Close();
            });
        }

        [Fact]
        public void AAreaTransparente_NaoBloqueiaCliqueNoDesktop()
        {
            // §8 A2. Background=Transparent PARTICIPA do hit-test; {x:Null} não. Com
            // Transparent na raiz, o usuário não conseguiria clicar em nada atrás dos 600px de
            // janela — e não teria como saber por quê.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                ((Grid)janela.FindName("Raiz")).Background.Should().BeNull();
                ((Grid)janela.FindName("Palco")).Background.Should().BeNull();

                janela.Close();
            });
        }

        [Fact]
        public void EmRepouso_ACascaEhOCirculoDeCinquentaESeis()
        {
            // §5.1 IDLE: círculo 56, glyph ✦ de 20px, borda neon. Nada mais.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                var casca = (Border)janela.FindName("Casca");
                casca.Width.Should().Be(56);
                casca.Height.Should().Be(56);
                casca.Padding.Should().Be(new Thickness(1.5), "§2 orb.stroke");

                var glyph = (TextBlock)janela.FindName("Glyph");
                glyph.Text.Should().Be("✦");
                glyph.FontSize.Should().Be(20);

                janela.EmModoBarra.Should().BeFalse();

                janela.Close();
            });
        }

        [Fact]
        public void ABordaNeon_EhFundoDeDoisBorders_NaoBorderBrush()
        {
            // §8 A4: gradiente em BorderBrush com CornerRadius renderiza canto sujo no WPF. O
            // gradiente é o FUNDO do Border externo e o Padding de 1,5 faz o papel da borda.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                var casca = (Border)janela.FindName("Casca");
                casca.Background.Should().BeOfType<LinearGradientBrush>();
                casca.BorderBrush.Should().BeNull("a borda é o fundo do Border de fora");

                var interna = (Border)janela.FindName("CascaInterna");
                interna.Background.Should().BeOfType<SolidColorBrush>(
                    "§0 O6 — o vidro é cor sólida a 90%, sem blur nativo");

                janela.Close();
            });
        }

        [Fact]
        public void Morph_LevaACascaDeCirculoAPilulaEDeVolta()
        {
            // §6. O ensaio afirma o ESTADO, não os quadros: o VisualStateManager anima, e
            // esperar a animação num teste o tornaria lento e instável.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                janela.AbrirBarra();
                janela.EmModoBarra.Should().BeTrue();

                janela.FecharBarra();
                janela.EmModoBarra.Should().BeFalse();

                janela.Close();
            });
        }

        [Fact]
        public void AbrirDuasVezes_NaoReinicaAAnimacao()
        {
            // Clicar de novo numa barra já aberta não pode fazer o elemento piscar de volta ao
            // círculo e crescer outra vez.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                janela.AbrirBarra();
                janela.AbrirBarra();

                janela.EmModoBarra.Should().BeTrue();

                janela.Close();
            });
        }
    }
}
