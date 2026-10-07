using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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
        public void FalasRestauradas_FicamEscondidasAteABarraAbrir_ENaoDuplicam()
        {
            // Bug: reiniciado o AIB, a barra do orbe abria vazia, embora a conversa estivesse
            // inteira em memory/shadow. As falas voltam à pilha no arranque, sem balão solto
            // sobre o desktop: só aparecem quando a barra abre.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();
                var falas = new[] { (true, "meu servidor caiu"), (false, "Qual deles?"), (true, "  ") };

                janela.RestaurarFalas(falas);

                janela.Falas.Select(f => f.Texto).Should().Equal("meu servidor caiu", "Qual deles?");
                janela.Falas[0].Should().BeOfType<AIB.Ui.FalaDoUsuario>();
                janela.Falas[1].Should().BeOfType<AIB.Ui.FalaDaIA>();
                janela.BalaoVisivel.Should().BeFalse("a barra está fechada");
                janela.Pulsando.Should().BeFalse("fala antiga não é aviso novo");

                janela.AbrirBarra();
                janela.BalaoVisivel.Should().BeTrue();

                janela.RestaurarFalas(falas);
                janela.Falas.Should().HaveCount(2, "religar o orbe não duplica a pilha");

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

        [Theory]
        [InlineData(13, 20)]      // um "✦" estreito
        [InlineData(19, 17)]      // um mais largo: a margem encolhe junto
        [InlineData(53, 0)]       // do tamanho do orbe: encosta na borda
        [InlineData(80, 0)]       // maior que o orbe: nunca margem negativa
        public void AMargemDoGlyph_SaiDaLarguraMedida(double largura, double esperada)
        {
            // O defeito: a margem era 20 fixo, chutada a partir de uma estimativa da largura
            // do "✦". O chute errou e o simbolo ficava alguns pixels a direita do centro.
            // Largura de glyph depende da fonte, do tamanho e do DPI — nao e constante, e nao
            // deve estar escrita a mao em lugar nenhum.
            ShadowAssistantWindow.MargemQueCentraliza(largura).Should().Be(esperada);
        }

        [Fact]
        public void ACelulaDoGlyph_FicaSempreAEsquerda_ParaNaoSaltarNoMeioDoMorph()
        {
            // O defeito: o alinhamento do glyph era animado por keyframe discreto (Center ->
            // Left). Alinhamento e LAYOUT, nao transformacao — ele nao interpola. O glyph
            // ficava parado enquanto a casca crescia e so aparecia no lugar certo no fim.
            // Agora o alinhamento e fixo e quem anima e a margem, que interpola.
            //
            // Quem carrega esse alinhamento passou a ser a CELULA: o glyph, o icone de inbox
            // e o anel sao centrados dentro dela, e e ela que anda.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                var celula = (Grid)janela.FindName("CelulaDoGlyph");
                celula.HorizontalAlignment.Should().Be(HorizontalAlignment.Left,
                    "alinhamento e layout e nao interpola: quem anima e a margem");

                janela.Close();
            });
        }

        [Fact]
        public void ConteudoDaBarra_NasceColapsado_ENaoSoTransparente()
        {
            // Opacity zero nao basta: as colunas Auto do microfone (34) e do enviar (38) somam
            // 72px e continuariam ocupando espaco dentro dos 53px internos do orbe, empurrando
            // o glyph para fora da casca.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                var conteudo = (Grid)janela.FindName("ConteudoDaBarra");
                conteudo.Visibility.Should().Be(Visibility.Collapsed);
                conteudo.Opacity.Should().Be(0);

                janela.Close();
            });
        }

        [Fact]
        public void ABarra_TemCampoMicrofoneEEnviar()
        {
            // §4.7. O segundo defeito relatado era este: a barra abria vazia, porque o
            // conteudo dela simplesmente nao existia ainda.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                ((TextBox)janela.FindName("Campo")).Should().NotBeNull();
                ((Button)janela.FindName("BotaoMic")).Width.Should().Be(34, "§2 iconButton");
                ((Button)janela.FindName("BotaoEnviar")).Width.Should().Be(38, "§2 sendBtn");

                janela.Close();
            });
        }

        [Fact]
        public void OPlaceholder_LevaONomeDoPersonagemESomeComTexto()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow { NomeDoAgente = "Ayano" };

                var dica = (TextBlock)janela.FindName("Dica");
                var campo = (TextBox)janela.FindName("Campo");

                dica.Text.Should().Contain("Ayano");
                dica.Visibility.Should().Be(Visibility.Visible);

                campo.Text = "oi";
                dica.Visibility.Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

        [Fact]
        public void Enviar_MandaOTextoParaForaEVoltaAoOrbe()
        {
            // §5.3 — a barra e porta de entrada, nao um segundo chat: ela entrega o texto e
            // se fecha. A conversa continua na janela de chat.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();
                string? recebido = null;
                janela.MensagemEnviada += t => recebido = t;

                janela.AbrirBarra();
                ((TextBox)janela.FindName("Campo")).Text = "  procura o ramal do Fernando  ";
                janela.Enviar();

                recebido.Should().Be("procura o ramal do Fernando");
                janela.EmModoBarra.Should().BeTrue("a barra fica aberta esperando a resposta");

                janela.Close();
            });
        }

        [Fact]
        public void EnviarVazio_NaoDisparaNada()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();
                bool disparou = false;
                janela.MensagemEnviada += _ => disparou = true;

                janela.AbrirBarra();
                ((TextBox)janela.FindName("Campo")).Text = "   ";
                janela.Enviar();

                disparou.Should().BeFalse();
                janela.EmModoBarra.Should().BeTrue("nada foi enviado, a barra continua aberta");

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // §4.6 / §5.4  Balao de fala
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OBalaoNaoApareceSozinho()
        {
            // §0 O3 e §8 A5: a fala NUNCA aparece por conta propria. So o pulso sinaliza, e o
            // balao entra quando ha o que mostrar.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                janela.BalaoVisivel.Should().BeFalse();
                janela.AbrirBarra();
                janela.BalaoVisivel.Should().BeFalse("abrir a barra nao e ter algo a dizer");

                janela.Close();
            });
        }

        [Fact]
        public void MostrarFala_PoeOTextoNoBalao()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                janela.MostrarFala("  o ramal do Fernando e 4275  ");

                janela.BalaoVisivel.Should().BeTrue();
                janela.TextoDaFala.Should().Be("o ramal do Fernando e 4275");

                janela.Close();
            });
        }

        [Fact]
        public void MostrarFalaVazia_NaoAbreBalaoOco()
        {
            // Turno que so executou ferramenta pode terminar sem texto. Um balao vazio sobre o
            // desktop nao diz nada e ainda cobre o que esta atras.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                janela.ComecarATrabalhar();
                janela.MostrarFala("   ");

                janela.BalaoVisivel.Should().BeFalse();
                janela.Trabalhando.Should().BeFalse("a resposta chegou, mesmo sem texto");

                janela.Close();
            });
        }

        [Fact]
        public void Dispensar_TiraOBalaoEDeixaABarraAberta()
        {
            // §5.4 — quem fechou a fala nao necessariamente terminou de falar.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                janela.AbrirBarra();
                janela.MostrarFala("pronto");
                janela.DispensarFala();

                janela.BalaoVisivel.Should().BeFalse();
                janela.EmModoBarra.Should().BeTrue();

                janela.Close();
            });
        }

        [Fact]
        public void FecharABarra_LevaOBalaoJunto()
        {
            // O balao e ancorado na barra: sem ela ficaria flutuando sozinho, apontando para
            // nada.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                janela.AbrirBarra();
                janela.MostrarFala("pronto");
                janela.FecharBarra();

                janela.BalaoVisivel.Should().BeFalse();

                janela.Close();
            });
        }

        [Fact]
        public void Enviar_MantemABarraAbertaEMostraQueEstaTrabalhando()
        {
            // Antes a barra fechava ao enviar. Isso obrigava o usuario a clicar de novo para
            // ver a resposta que ele mesmo acabou de pedir — e §5.4 diz que ele le e responde
            // no mesmo lugar.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                janela.AbrirBarra();
                ((TextBox)janela.FindName("Campo")).Text = "oi";
                janela.Enviar();

                janela.EmModoBarra.Should().BeTrue();
                janela.Trabalhando.Should().BeTrue();
                ((Path)janela.FindName("AnelDeProgresso")).Visibility
                    .Should().Be(Visibility.Visible, "§5.6 — o anel gira enquanto o turno roda");

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
