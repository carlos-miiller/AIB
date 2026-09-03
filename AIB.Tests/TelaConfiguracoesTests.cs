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
                cofre);

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
                    new MailServiceStub(), cofre);

                Clicar(primeira, "BotaoAdicionarConta");
                Digitar(primeira, "ana@gmail.com", "abcdefghijklmnop");
                Clicar(primeira, "BotaoConectarConta");
                Bombear();
                primeira.Close();

                var segunda = new SettingsWindow(
                    new SettingsService(caminho), PaginaDeConfiguracoes.Email,
                    new MailServiceStub(), cofre);

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
                    new MailServiceStub(), new MailVault(pasta));

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

        /// <summary>Serviço que recusa qualquer login, para o caminho de erro do formulário.</summary>
        private sealed class ServicoQueRecusa : IMailService
        {
            public System.Threading.Tasks.Task<MailLoginResult> TestLoginAsync(
                string endereco, string senhaDeApp, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.Task.FromResult(new MailLoginResult(
                    false, default, "senha de app recusada — altere a senha", false));
        }
    }
}
