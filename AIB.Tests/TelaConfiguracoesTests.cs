using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIB.Services;
using AIB.Services.Mail;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A tela de Configurações reestruturada — refactor-interface/tela-configuracoes (3).html.
    /// <para>
    /// O que se afirma aqui é o que a spec chama de normativo: as quatro páginas com os rótulos
    /// verbatim, o menu que troca a View sem descartar edição (§7 A11), e a lista de caixas de
    /// e-mail com as invariantes de §7 A15 no caminho que a tela realmente percorre.
    /// </para>
    /// </summary>
    public class TelaConfiguracoesTests
    {
        private static string PastaTemporaria()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "aib-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            return tmp;
        }

        /// <summary>
        /// Tela com serviço e cofre DESCARTÁVEIS. Nenhum ensaio pode escrever no profile.dat
        /// nem no cofre reais: são as configurações e as senhas de verdade do usuário.
        /// </summary>
        private static (SettingsWindow janela, MailVault cofre, string pasta) Nova(
            IMailService? servico = null)
        {
            string pasta = PastaTemporaria();
            var servicoDeSettings = new SettingsService(Path.Combine(pasta, "settings.json"));
            var cofre = new MailVault(pasta);

            var janela = new SettingsWindow(
                servicoDeSettings,
                PaginaDeConfiguracoes.Identidade,
                servico ?? new MailServiceStub(),
                cofre,
                new EstadoDasCaixas(pasta));

            return (janela, cofre, pasta);
        }

        private static void Bombear()
        {
            // O ConectarConta_Click é async void. O esqueleto devolve Task já completa, então
            // a continuação roda em linha — mas depender disso deixaria o ensaio preso a um
            // detalhe do TPL.
            for (int i = 0; i < 3; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }

        private static void Digitar(SettingsWindow janela, string endereco, string senha)
        {
            ((TextBox)janela.FindName("NovaContaEndereco")).Text = endereco;
            ((PasswordBox)janela.FindName("NovaContaSenha")).Password = senha;
        }

        private static void Clicar(SettingsWindow janela, string nome) =>
            ((Button)janela.FindName(nome)).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        // ─────────────────────────────────────────────────────────────────────
        // §3.1 / §7 A5  Tamanho
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AJanelaCabeOsDesenho880x560_MAIS_AMargemDaSombra()
        {
            // §7 A5: 880x560 é o desenho (era 620 antes do menu lateral). A janela é maior
            // porque a DropShadow precisa de área e o WPF a recorta no limite. A margem do
            // Border faz a conta, e mexer numa e esquecer a outra corta a sombra em linha reta
            // — foi o que a janela do orbe já pagou.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                double margemH = 40 + 40;
                double margemV = 30 + 60;

                (janela.Width - margemH).Should().Be(880);
                (janela.Height - margemV).Should().Be(560);

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Ícones: o desenho não pode ser recortado pela própria caixa
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void NenhumIcone_DESENHA_ForaDaPropriaCaixa()
        {
            // O defeito: os desenhos vêm de um viewBox de 20x20 e a caixa tem 16. Com
            // Stretch="None" o Path desenha em coordenada nativa e o que passa de 16 é
            // RECORTADO — os ícones apareciam cortados à direita e embaixo, e o da pessoa
            // parecia achatado porque o corpo dele vai até y=16,6.
            //
            // O ensaio verifica a CLASSE do defeito, e não o caso: qualquer Path com
            // Stretch=None precisa caber; quem estica é livre porque o WPF encaixa sozinho.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                var raiz = (FrameworkElement)janela.Content;
                raiz.Measure(new Size(janela.Width, janela.Height));
                raiz.Arrange(new Rect(0, 0, janela.Width, janela.Height));

                foreach (var caminho in TodosOsPaths(raiz))
                {
                    if (caminho.Stretch != System.Windows.Media.Stretch.None) continue;
                    if (caminho.Data == null) continue;
                    if (double.IsNaN(caminho.Width) || double.IsNaN(caminho.Height)) continue;

                    var limites = caminho.Data.Bounds;

                    limites.Right.Should().BeLessThanOrEqualTo(caminho.Width,
                        "um Path com Stretch=None desenha em coordenada nativa e é recortado pela caixa");
                    limites.Bottom.Should().BeLessThanOrEqualTo(caminho.Height,
                        "um Path com Stretch=None desenha em coordenada nativa e é recortado pela caixa");
                }

                janela.Close();
            });
        }

        [Fact]
        public void OGlyphDoCabecalho_FicaNaAlturaDoTitulo()
        {
            // Ele estava alinhado ao topo com margem escolhida a olho para um glyph de 15
            // cair na altura de um título de outro tamanho. Errou, e o símbolo ficava alto.
            // Agora os dois dividem a mesma linha de uma grade, e a centragem é do layout.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                var raiz = (FrameworkElement)janela.Content;
                raiz.Measure(new Size(janela.Width, janela.Height));
                raiz.Arrange(new Rect(0, 0, janela.Width, janela.Height));
                raiz.UpdateLayout();

                var glyph = (TextBlock)janela.FindName("GlyphDoCabecalho");
                var titulo = (TextBlock)janela.FindName("TituloDoCabecalho");

                double centroDoGlyph = glyph.TranslatePoint(
                    new Point(0, glyph.ActualHeight / 2), raiz).Y;
                double centroDoTitulo = titulo.TranslatePoint(
                    new Point(0, titulo.ActualHeight / 2), raiz).Y;

                Math.Abs(centroDoGlyph - centroDoTitulo).Should().BeLessThan(1.0,
                    "o glyph acompanha a linha do título, não o topo do bloco");

                janela.Close();
            });
        }

        private static System.Collections.Generic.IEnumerable<System.Windows.Shapes.Path> TodosOsPaths(
            DependencyObject raiz)
        {
            if (raiz is System.Windows.Shapes.Path p) yield return p;

            int filhos = System.Windows.Media.VisualTreeHelper.GetChildrenCount(raiz);
            for (int i = 0; i < filhos; i++)
                foreach (var achado in TodosOsPaths(System.Windows.Media.VisualTreeHelper.GetChild(raiz, i)))
                    yield return achado;
        }

        // ─────────────────────────────────────────────────────────────────────
        // §3.10  Menu lateral
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OMenuTemQuatroEntradas_ComOsRotulosDaSpec()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                foreach (string nome in new[] { "NavIdentidade", "NavConexao", "NavEmail", "NavAvancado" })
                    janela.FindName(nome).Should().NotBeNull(nome + " precisa existir");

                janela.Close();
            });
        }

        [Fact]
        public void OMenuNaoRola_ENaoUsaListBox()
        {
            // §7 A13 — o ListBox pinta o item selecionado com o azul do sistema e o hover com
            // cinza claro, e reescrever o ItemContainerStyle para apagar as duas coisas é mais
            // código que o problema pede. RadioButton com GroupName dá a mesma semântica e não
            // traz realce nenhum para desfazer.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                var item = (RadioButton)janela.FindName("NavIdentidade");
                item.GroupName.Should().Be("Paginas");

                janela.Close();
            });
        }

        [Fact]
        public void SoAPaginaAtiva_EstaVisivel()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                Visibility Vis(string nome) => ((FrameworkElement)janela.FindName(nome)).Visibility;

                janela.PaginaAtiva.Should().Be(PaginaDeConfiguracoes.Identidade);
                Vis("PaginaIdentidade").Should().Be(Visibility.Visible);
                Vis("PaginaConexao").Should().Be(Visibility.Collapsed);
                Vis("PaginaEmail").Should().Be(Visibility.Collapsed);
                Vis("PaginaAvancado").Should().Be(Visibility.Collapsed);

                janela.IrPara(PaginaDeConfiguracoes.Email);

                janela.PaginaAtiva.Should().Be(PaginaDeConfiguracoes.Email);
                Vis("PaginaIdentidade").Should().Be(Visibility.Collapsed);
                Vis("PaginaEmail").Should().Be(Visibility.Visible);

                janela.Close();
            });
        }

        [Fact]
        public void ARotaDireta_AbreAJanelaJaNaPaginaPedida()
        {
            // §3.10 — a aba de e-mails da janela de chat precisa cair direto em "E-mail", e
            // não na primeira página com o usuário tendo de achar a seção.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();

                var janela = new SettingsWindow(
                    new SettingsService(Path.Combine(pasta, "settings.json")),
                    PaginaDeConfiguracoes.Email,
                    new MailServiceStub(),
                    new MailVault(pasta));

                janela.PaginaAtiva.Should().Be(PaginaDeConfiguracoes.Email);

                janela.Close();
            });
        }

        [Fact]
        public void TrocarDePagina_NAO_DescartaEdicaoNemLimpaOSujo()
        {
            // §7 A11 — o ViewModel é UM só para as quatro páginas e IsDirty é global. Ir em
            // "E-mail", voltar em "Avançado" e salvar tem de gravar as duas coisas.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                var url = (TextBox)janela.FindName("UrlTextBox");
                var salvar = (Button)janela.FindName("SaveButton");

                salvar.IsEnabled.Should().BeFalse("nasce desabilitado — §7 A8");

                url.Text = "http://127.0.0.1:9999/v1";
                salvar.IsEnabled.Should().BeTrue();

                janela.IrPara(PaginaDeConfiguracoes.Email);
                janela.IrPara(PaginaDeConfiguracoes.Avancado);
                janela.IrPara(PaginaDeConfiguracoes.Conexao);

                url.Text.Should().Be("http://127.0.0.1:9999/v1", "navegar não reseta campo");
                salvar.IsEnabled.Should().BeTrue("navegar não limpa o estado sujo");

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // §5  Os onze campos continuam todos lá
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OsNoveCamposDeConfiguracao_SOBREVIVERAM_AoMenuLateral()
        {
            // A reestruturação move campos entre páginas; remover um seria tirar uma
            // preferência do usuário sem que ele pedisse. §5 é normativa.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                foreach (string nome in new[]
                {
                    "CharacterComboBox", "ProviderComboBox", "UrlTextBox", "KeyTextBox",
                    "ModelComboBox", "MaxHistoryTextBox", "KeepAliveComboBox",
                    "SendSystemPromptSwitch", "VerboseLoggingSwitch"
                })
                    janela.FindName(nome).Should().NotBeNull(nome + " saiu da tela");

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // §3.12  Lista de contas de e-mail
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void SemConta_APaginaMostraSOOBotaoDeAdicionar()
        {
            // §3.12 estado vazio: sem ilustração e sem texto de estado vazio — o subtítulo da
            // página já explica o que a IA faz com o e-mail.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                janela.Contas.Count.Should().Be(0);
                ((ItemsControl)janela.FindName("ListaDeContas")).Items.Count.Should().Be(0);
                ((Button)janela.FindName("BotaoAdicionarConta")).Visibility.Should().Be(Visibility.Visible);
                ((Border)janela.FindName("FormularioDeConta")).Visibility.Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

        [Fact]
        public void OFormularioOcupaOLugarDoBotao_UmaContaPorVez()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                Clicar(janela, "BotaoAdicionarConta");

                ((Button)janela.FindName("BotaoAdicionarConta")).Visibility.Should().Be(Visibility.Collapsed);
                ((Border)janela.FindName("FormularioDeConta")).Visibility.Should().Be(Visibility.Visible);

                Clicar(janela, "BotaoAdicionarConta");   // não faz nada: está Collapsed
                ((Border)janela.FindName("FormularioDeConta")).Visibility.Should().Be(Visibility.Visible);

                janela.Close();
            });
        }

        [Fact]
        public void ConectarSoHabilitaComOsDoisCamposPreenchidos()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();
                var conectar = (Button)janela.FindName("BotaoConectarConta");

                Clicar(janela, "BotaoAdicionarConta");
                conectar.IsEnabled.Should().BeFalse();

                Digitar(janela, "ana@gmail.com", "");
                conectar.IsEnabled.Should().BeFalse("falta a senha");

                Digitar(janela, "ana@gmail.com", "abcdefghijklmnop");
                conectar.IsEnabled.Should().BeTrue();

                janela.Close();
            });
        }

        [Fact]
        public void Conectar_GRAVA_ASenhaNoCofreEAContaNaLista()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, cofre, _) = Nova();

                Clicar(janela, "BotaoAdicionarConta");
                Digitar(janela, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(janela, "BotaoConectarConta");
                Bombear();

                janela.Contas.Count.Should().Be(1);
                janela.Contas.Contas[0].Address.Should().Be("ana@gmail.com");
                janela.Contas.Contas[0].ImapHost.Should().Be("imap.gmail.com", "deduzido, não digitado");
                janela.Contas.Contas[0].IsPrimary.Should().BeTrue("a primeira nasce principal");
                janela.Contas.Contas[0].HasPassword.Should().BeTrue();

                cofre.Ler("ana@gmail.com").Should().Be("abcdefghijklmnop");

                // O formulário fecha e o botão volta.
                ((Border)janela.FindName("FormularioDeConta")).Visibility.Should().Be(Visibility.Collapsed);
                ((Button)janela.FindName("BotaoAdicionarConta")).Visibility.Should().Be(Visibility.Visible);

                janela.Close();
            });
        }

        [Fact]
        public void ComOEsqueleto_ALinhaNasceAMBAR_NuncaVerde()
        {
            // §7 A15 pede que só entre conta cujo login passou. Enquanto não há IMAP o desvio
            // fica VISÍVEL: âmbar com "verificação pendente". Uma linha verde aqui seria a tela
            // afirmando uma conexão que ninguém testou.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                Clicar(janela, "BotaoAdicionarConta");
                Digitar(janela, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(janela, "BotaoConectarConta");
                Bombear();

                var conta = janela.Contas.Contas[0];
                conta.Status.Should().Be(MailAccountStatus.Checking);
                conta.StatusText.Should().Contain(MailServiceStub.TextoPendente);

                janela.Close();
            });
        }

        [Fact]
        public void LoginRecusado_MANTEM_OFormularioEGravaNADA()
        {
            // §9 passo 6: o formulário fica aberto, a senha continua no campo, e nada é
            // gravado — nem conta, nem senha.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, cofre, _) = Nova(new ServicoQueRecusa());

                Clicar(janela, "BotaoAdicionarConta");
                Digitar(janela, "ana@gmail.com", "senha-errada");
                Clicar(janela, "BotaoConectarConta");
                Bombear();

                janela.Contas.Count.Should().Be(0, "nada entra na lista");
                cofre.Existe("ana@gmail.com").Should().BeFalse("nada vai para o cofre");

                ((Border)janela.FindName("FormularioDeConta")).Visibility.Should().Be(Visibility.Visible);
                ((PasswordBox)janela.FindName("NovaContaSenha")).Password.Should().Be("senha-errada",
                    "a senha continua no campo: reescrever tudo por causa de um erro é castigo");

                var faixa = (Border)janela.FindName("ErroDaConta");
                faixa.Visibility.Should().Be(Visibility.Visible);
                ((TextBlock)janela.FindName("ErroDaContaTexto")).Text.Should().Contain("senha de app");

                janela.Close();
            });
        }

        [Fact]
        public void ContaRepetida_EhRecusadaAntesDeQualquerConexao()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                Clicar(janela, "BotaoAdicionarConta");
                Digitar(janela, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(janela, "BotaoConectarConta");
                Bombear();

                Clicar(janela, "BotaoAdicionarConta");
                Digitar(janela, "ANA@GMAIL.COM", "outra-senha");
                Clicar(janela, "BotaoConectarConta");
                Bombear();

                janela.Contas.Count.Should().Be(1);
                ((TextBlock)janela.FindName("ErroDaContaTexto")).Text.Should().Contain("já está na lista");

                janela.Close();
            });
        }

        [Fact]
        public void SemIMAP_ALinhaDIZ_QueNaoHaLeituraAImplementar()
        {
            // O defeito: a linha dizia "ainda não lida nesta sessão", que PROMETE uma leitura
            // que vem. Enquanto não existe serviço de IMAP nenhuma leitura vem, e a frase fazia
            // o usuário esperar por algo que não ia acontecer — e desconfiar da própria senha.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = System.IO.Path.Combine(pasta, "settings.json");
                var cofre = new MailVault(pasta);

                var primeira = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new MailServiceStub(), cofre, new EstadoDasCaixas(pasta));
                Clicar(primeira, "BotaoAdicionarConta");
                Digitar(primeira, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(primeira, "BotaoConectarConta");
                Bombear();
                primeira.Close();

                var segunda = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new MailServiceStub(), cofre, new EstadoDasCaixas(pasta));

                string texto = segunda.Contas.Contas[0].StatusText;

                texto.Should().NotContain("ainda não lida",
                    "essa frase promete uma leitura que, sem IMAP, nunca acontece");
                texto.Should().Contain(MailServiceStub.TextoPendente);

                segunda.Close();
            });
        }

        [Fact]
        public void ComIMAP_AVarreduraRODA_AoAbrirATela()
        {
            // O ponto ambar diz "Verificando...", e isso tem de significar que alguem esta
            // verificando. Sem varrer na abertura, a conta guardada ficava parada em ambar para
            // sempre e o usuario nao tinha nenhum gesto para tirar ela de la — o mesmo beco sem
            // saida de antes, agora com outra frase.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = System.IO.Path.Combine(pasta, "settings.json");
                var cofre = new MailVault(pasta);

                var primeira = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new MailServiceStub(), cofre, new EstadoDasCaixas(pasta));
                Clicar(primeira, "BotaoAdicionarConta");
                Digitar(primeira, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(primeira, "BotaoConectarConta");
                Bombear();
                primeira.Close();

                var segunda = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new ServicoQueVarre(mensagens: 34, naoLidas: 5), cofre, new EstadoDasCaixas(pasta));
                Bombear();

                var conta = segunda.Contas.Contas[0];
                conta.Status.Should().Be(MailAccountStatus.Ok, "a varredura passou");

                // A primeira janela rodou com o ESQUELETO, que nao le nada, entao nao ficou
                // estado guardado: esta segunda abertura e a primeira leitura de verdade e
                // olha a janela por data. A frase tem de dizer isso.
                conta.StatusText.Should().Contain("34 em 3d").And.Contain("5 por ler");

                segunda.Close();
            });
        }

        [Fact]
        public void AVarreduraGRAVA_OEstadoParaAProximaVez()
        {
            // Sem o uidValidity e o lastUid guardados, toda varredura recomeca pela data e
            // retria as mesmas mensagens — o oposto do que o vigia precisa fazer.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = System.IO.Path.Combine(pasta, "settings.json");
                var cofre = new MailVault(pasta);
                var estado = new EstadoDasCaixas(pasta);

                var janela = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new ServicoQueVarre(mensagens: 12, naoLidas: 2, uidValidity: 8271, ultimoUid: 91043),
                    cofre, estado);

                Clicar(janela, "BotaoAdicionarConta");
                Digitar(janela, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(janela, "BotaoConectarConta");
                Bombear();

                var guardado = estado.Ler("ana@gmail.com");
                guardado.Should().NotBeNull();
                guardado!.UidValidity.Should().Be(8271);
                guardado.LastUid.Should().Be(91043);
                guardado.LastReadUtc.Should().NotBeNull();

                janela.Close();
            });
        }

        [Fact]
        public void VarreduraQueFALHA_PintaALinhaDeVERMELHO()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = System.IO.Path.Combine(pasta, "settings.json");
                var cofre = new MailVault(pasta);

                var primeira = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new MailServiceStub(), cofre, new EstadoDasCaixas(pasta));
                Clicar(primeira, "BotaoAdicionarConta");
                Digitar(primeira, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(primeira, "BotaoConectarConta");
                Bombear();
                primeira.Close();

                var segunda = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new ServicoQueRecusa(), cofre, new EstadoDasCaixas(pasta));
                Bombear();

                var conta = segunda.Contas.Contas[0];
                conta.Status.Should().Be(MailAccountStatus.Error);
                conta.StatusText.Should().Contain("não consegui ler a caixa");

                segunda.Close();
            });
        }

        [Fact]
        public void ContaSemSenhaNoCofre_DIZ_OQueFazer()
        {
            // Blob de outra máquina não decifra, e a conta volta do disco sem senha. "Ainda não
            // lida" seria enganoso aqui também: não falta leitura, falta credencial.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = System.IO.Path.Combine(pasta, "settings.json");

                var servicoDeSettings = new SettingsService(caminho);
                var gravado = servicoDeSettings.LoadSettings();
                gravado.MailAccounts.Add(new MailAccountSettings
                {
                    Address = "ana@gmail.com",
                    ImapHost = "imap.gmail.com",
                    ImapPort = 993,
                    IsPrimary = true
                });
                servicoDeSettings.SaveSettings(gravado);

                var janela = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new MailServiceStub(), new MailVault(pasta), new EstadoDasCaixas(pasta));

                var conta = janela.Contas.Contas[0];
                conta.HasPassword.Should().BeFalse();
                conta.StatusText.Should().Contain("senha de app ausente");

                janela.Close();
            });
        }

        [Fact]
        public void AsContasSobrevivemAoFechaEAbre_ComASenhaNoCofre()
        {
            // §9 passo 8, último item da lista de testes mínimos.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = Path.Combine(pasta, "settings.json");
                var cofre = new MailVault(pasta);

                var primeira = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new MailServiceStub(), cofre, new EstadoDasCaixas(pasta));

                Clicar(primeira, "BotaoAdicionarConta");
                Digitar(primeira, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(primeira, "BotaoConectarConta");
                Bombear();
                primeira.Close();

                var segunda = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new MailServiceStub(), cofre, new EstadoDasCaixas(pasta));

                segunda.Contas.Count.Should().Be(1, "a conta volta do disco");
                segunda.Contas.Contas[0].Address.Should().Be("ana@gmail.com");
                segunda.Contas.Contas[0].HasPassword.Should().BeTrue("a senha continua no cofre");
                segunda.Contas.Contas[0].Status.Should().Be(MailAccountStatus.Checking,
                    "estado não é gravado: dizer 'Conectada' na abertura seria afirmar o que ninguém verificou");

                segunda.Close();
            });
        }

        [Fact]
        public void AdicionarConta_NAO_PERSISTE_EdicaoPendenteDasOutrasPaginas()
        {
            // §7 A15 diz que a conta é gravada na hora. Se essa gravação saísse do objeto em
            // edição, ela levaria de carona a Base URL alterada e não salva — exatamente o que
            // o "Salvar" desabilitado promete que não aconteceu.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = Path.Combine(pasta, "settings.json");
                var servicoDeSettings = new SettingsService(caminho);

                var janela = new SettingsWindow(
                    servicoDeSettings, PaginaDeConfiguracoes.Identidade,
                    new MailServiceStub(), new MailVault(pasta), new EstadoDasCaixas(pasta));

                string urlOriginal = ((TextBox)janela.FindName("UrlTextBox")).Text;
                ((TextBox)janela.FindName("UrlTextBox")).Text = "http://nao-salvar.example/v1";

                Clicar(janela, "BotaoAdicionarConta");
                Digitar(janela, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(janela, "BotaoConectarConta");
                Bombear();

                var doDisco = new SettingsService(caminho).LoadSettings();

                doDisco.MailAccounts.Should().HaveCount(1, "a conta foi gravada na hora");
                doDisco.ApiUrl.Should().Be(urlOriginal, "a URL pendente NÃO foi de carona");

                janela.Close();
            });
        }

        [Fact]
        public void APrincipal_NaoSaiPelaTelaEnquantoHouverOutra()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                foreach (string endereco in new[] { "a@gmail.com", "b@gmail.com" })
                {
                    Clicar(janela, "BotaoAdicionarConta");
                    Digitar(janela, endereco, "abcdefghijklmnop");
                    Clicar(janela, "BotaoConectarConta");
                    Bombear();
                }

                janela.Contas.Count.Should().Be(2);
                janela.Contas.PodeRemover(janela.Contas.Principal!).Should().BeFalse();

                janela.Contas.TornarPrincipal(janela.Contas.Contas[1]);
                janela.Contas.PodeRemover(janela.Contas.Contas[0]).Should().BeTrue(
                    "promovida a outra, a antiga principal já pode sair");

                janela.Close();
            });
        }

        [Fact]
        public void ReabrirATela_NaoRELE_ACaixaInteira()
        {
            // O bug relatado: entrar nas configuracoes refazia a leitura toda, mesmo com a
            // conta ja registrada e ja lida. A causa era o estado guardado nao chegar ao
            // servico — ele so rotulava como "novo" o que ja tinha sido baixado, sem poupar
            // uma unica mensagem de trabalho.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = System.IO.Path.Combine(pasta, "settings.json");
                var cofre = new MailVault(pasta);
                var estado = new EstadoDasCaixas(pasta);

                var primeira = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new ServicoQueVarre(mensagens: 33, naoLidas: 4, uidValidity: 8271, ultimoUid: 91043),
                    cofre, estado);
                Clicar(primeira, "BotaoAdicionarConta");
                Digitar(primeira, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(primeira, "BotaoConectarConta");
                Bombear();
                primeira.Close();

                var servico = new ServicoQueVarre(mensagens: 2, naoLidas: 1,
                                                  uidValidity: 8271, ultimoUid: 91045);
                var segunda = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    servico, cofre, estado);
                Bombear();

                servico.UltimoGuardado.Should().NotBeNull(
                    "sem o estado em maos o servico nao tem como pular o que ja foi lido");
                servico.UltimoGuardado!.LastUid.Should().Be(91043);
                servico.UltimoGuardado.ServeParaPartir(8271).Should().BeTrue(
                    "o selo bate, entao a busca pode ir direto aos UIDs acima de 91043");

                segunda.Close();
            });
        }

        [Fact]
        public void JanelaVAZIA_NaoAPAGA_OProgressoJaGuardado()
        {
            // Uma varredura sem nada na janela devolve ultimoUid 0, que significa "nao vi
            // nada" e NAO "recomece do zero". Gravar esse zero faria a caixa inteira voltar a
            // parecer novidade — um fim de semana sem e-mail bastaria para causar isso.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = System.IO.Path.Combine(pasta, "settings.json");
                var cofre = new MailVault(pasta);
                var estado = new EstadoDasCaixas(pasta);

                var primeira = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new ServicoQueVarre(mensagens: 12, naoLidas: 2, uidValidity: 8271, ultimoUid: 91043),
                    cofre, estado);
                Clicar(primeira, "BotaoAdicionarConta");
                Digitar(primeira, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(primeira, "BotaoConectarConta");
                Bombear();
                primeira.Close();

                var segunda = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new ServicoQueVarre(mensagens: 0, naoLidas: 0, uidValidity: 8271, ultimoUid: 0),
                    cofre, estado);
                Bombear();

                estado.Ler("ana@gmail.com")!.LastUid.Should().Be(91043,
                    "a caixa quieta nao desfaz o que ja tinha sido lido");

                segunda.Close();
            });
        }

        [Fact]
        public void SeloDeValidadeTROCADO_DESCARTA_OUidGuardado()
        {
            // uidValidity diferente significa que o servidor RENUMEROU a caixa: o UID guardado
            // e de outra numeracao, e preserva-lo pelo maior numero apontaria para uma mensagem
            // que nao existe. Aqui o numero novo tem de vencer mesmo sendo MENOR.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = System.IO.Path.Combine(pasta, "settings.json");
                var cofre = new MailVault(pasta);
                var estado = new EstadoDasCaixas(pasta);

                var primeira = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new ServicoQueVarre(mensagens: 12, naoLidas: 2, uidValidity: 8271, ultimoUid: 91043),
                    cofre, estado);
                Clicar(primeira, "BotaoAdicionarConta");
                Digitar(primeira, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(primeira, "BotaoConectarConta");
                Bombear();
                primeira.Close();

                var segunda = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new ServicoQueVarre(mensagens: 4, naoLidas: 1, uidValidity: 9999, ultimoUid: 7),
                    cofre, estado);
                Bombear();

                var guardado = estado.Ler("ana@gmail.com")!;
                guardado.UidValidity.Should().Be(9999);
                guardado.LastUid.Should().Be(7, "numeracao nova nao se compara com a antiga");

                segunda.Close();
            });
        }

        /// <summary>Serviço que conecta e devolve uma varredura com números fixos.</summary>
        private sealed class ServicoQueVarre : IMailService
        {
            private readonly int _mensagens;
            private readonly int _naoLidas;
            private readonly uint _uidValidity;
            private readonly uint _ultimoUid;

            public ServicoQueVarre(int mensagens, int naoLidas, uint uidValidity = 1, uint ultimoUid = 100)
            {
                _mensagens = mensagens;
                _naoLidas = naoLidas;
                _uidValidity = uidValidity;
                _ultimoUid = ultimoUid;
            }

            public bool Disponivel => true;
            public string MotivoDaIndisponibilidade => "";

            public System.Threading.Tasks.Task<MailLoginResult> TestLoginAsync(
                string endereco, string senhaDeApp, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.Task.FromResult(new MailLoginResult(
                    true, ImapHostGuesser.Primeiro(endereco)!.Value, "", Verificado: true));

            /// <summary>O estado que a tela entregou na última chamada.</summary>
            public EstadoDaCaixa? UltimoGuardado { get; private set; }

            public System.Threading.Tasks.Task<MailScanResult> VarrerAsync(
                string endereco, string senhaDeApp, ImapEndpoint endpoint,
                DateTime desdeUtc, EstadoDaCaixa? guardado, System.Threading.CancellationToken ct)
            {
                UltimoGuardado = guardado;

                // Mesma decisao do servico de verdade, pela mesma casa: o estado so serve se o
                // selo bater. Sem espelhar isso aqui, o dublê responderia "por data" para
                // sempre e os testes de estado deixariam de significar alguma coisa.
                bool incremental = guardado != null && guardado.ServeParaPartir(_uidValidity);

                return System.Threading.Tasks.Task.FromResult(new MailScanResult(
                    true, _mensagens, _naoLidas, incremental, _uidValidity, _ultimoUid, ""));
            }
        }

        /// <summary>Serviço que recusa qualquer login, para o caminho de erro do formulário.</summary>
        private sealed class ServicoQueRecusa : IMailService
        {
            public bool Disponivel => true;
            public string MotivoDaIndisponibilidade => "";

            public System.Threading.Tasks.Task<MailScanResult> VarrerAsync(
                string endereco, string senhaDeApp, ImapEndpoint endpoint,
                DateTime desdeUtc, EstadoDaCaixa? guardado, System.Threading.CancellationToken ct)
                => System.Threading.Tasks.Task.FromResult(
                    new MailScanResult(false, 0, 0, false, 0, 0, "não consegui ler a caixa agora"));

            public System.Threading.Tasks.Task<MailLoginResult> TestLoginAsync(
                string endereco, string senhaDeApp, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.Task.FromResult(new MailLoginResult(
                    false, default, "senha de app recusada — altere a senha", false));
        }
    }
}
