using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Mail;
using AIB.Services.Memory;
using AIB.Ui;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Os valores que deixaram de ser constantes no código, os padrões deles e o botão de
    /// restaurar de cada página.
    /// <para>
    /// O risco desta entrega não é a chave não gravar: é ela gravar um número que quebra o
    /// programa em silêncio — fração de memória em 99% não deixa espaço para conversar, zero
    /// iterações não roda turno nenhum. Por isso metade daqui é sobre a faixa válida.
    /// </para>
    /// </summary>
    public class ConfiguracoesPadraoTests
    {
        private static string PastaTemporaria()
        {
            string pasta = Path.Combine(Path.GetTempPath(), "aib-padrao-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pasta);
            return pasta;
        }

        private static T Achar<T>(SettingsWindow j, string nome) where T : class => (T)j.FindName(nome)!;

        private static SettingsWindow Nova(string pasta, PaginaDeConfiguracoes pagina,
                                           Action<UserAppSettings>? preparar = null)
        {
            var servico = new SettingsService(Path.Combine(pasta, "settings.json"));

            if (preparar != null)
            {
                var s = servico.LoadSettings();
                preparar(s);
                servico.SaveSettings(s);
            }

            return new SettingsWindow(servico, pagina, new MailServiceStub(),
                                      new MailVault(pasta), new EstadoDasCaixas(pasta));
        }

        // ─────────────────────────────────────────────────────────────────
        // As chaves mortas foram embora
        // ─────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("EphemeralSkillContext")]
        [InlineData("SearchEngine")]
        [InlineData("MaxContextTokens")]
        public void ChavesQueNINGUEM_Lia_NaoExistemMais(string nome)
        {
            // As três estavam gravadas no profile.dat, com valor default, e nenhum código as
            // lia. MaxContextTokens era a pior: a página Avançado mostra "Máximo de Tokens de
            // Contexto", mas aquele número vem do LevelService — quem editasse o arquivo à mão
            // acharia que tinha mudado alguma coisa.
            typeof(UserAppSettings).GetProperty(nome, BindingFlags.Public | BindingFlags.Instance)
                .Should().BeNull(nome + " não era lida por ninguém e saiu");
        }

        // ─────────────────────────────────────────────────────────────────
        // Padrões
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void OsPadroesSAO_UmSoLugar()
        {
            // As constantes do código passaram a apontar para as consts das configurações. Se
            // alguém mudar o padrão num lugar e não no outro, o programa nasce com um valor e
            // o botão de restaurar devolve outro.
            var padrao = new UserAppSettings();

            padrao.MaxTurnIterations.Should().Be(AgentLoop.MaxIterations);
            padrao.CompactionTrigger.Should().Be(MemoryBudget.CompactionTrigger);
            padrao.MemoryFraction.Should().Be(MemoryBudget.MemoryFraction);
            padrao.ShadowMailPreviewCount.Should().Be(FalaDaIA.TetoDeEmails);
        }

        // ─────────────────────────────────────────────────────────────────
        // Saneamento
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ValorABSURDO_VIRA_OMaisProximoValido()
        {
            // Saneia, não recusa: o usuário perde o exagero, não as configurações inteiras.
            var s = new UserAppSettings
            {
                MaxTurnIterations = 9999,
                CompactionTrigger = 4.2,
                MemoryFraction = 0.99,
                ShadowMailPreviewCount = 0,
                MailWindowDays = -3,
                MailTimeoutSeconds = 1
            }.Sanear();

            s.MaxTurnIterations.Should().Be(60);
            s.CompactionTrigger.Should().Be(0.99);
            s.MemoryFraction.Should().Be(0.60);
            s.ShadowMailPreviewCount.Should().Be(1);
            s.MailWindowDays.Should().Be(1);
            s.MailTimeoutSeconds.Should().Be(5);
        }

        [Fact]
        public void ZERO_Iteracoes_NaoPASSA()
        {
            // Zero iterações não roda turno nenhum: a conversa ficaria muda sem erro visível.
            new UserAppSettings { MaxTurnIterations = 0 }.Sanear()
                .MaxTurnIterations.Should().BeGreaterThanOrEqualTo(1);
        }

        [Fact]
        public void NaN_NaFracao_VIRA_OMinimo()
        {
            // Um NaN vindo de JSON estragado passaria por toda comparação de faixa: NaN não é
            // menor nem maior que nada. Sem tratá-lo, a cota de memória viraria NaN e, daí,
            // zero em todo cálculo — a memória sumiria em silêncio.
            new UserAppSettings { MemoryFraction = double.NaN }.Sanear()
                .MemoryFraction.Should().Be(0.05);
        }

        [Fact]
        public void ArquivoComValorAbsurdo_JA_CHEGA_Saneado()
        {
            // O arquivo é editável à mão. Sanear na leitura evita espalhar defesa por cada
            // consumidor — e nenhum deles tem contexto para saber o que fazer com um absurdo.
            string pasta = PastaTemporaria();
            var servico = new SettingsService(Path.Combine(pasta, "settings.json"));

            servico.SaveSettings(new UserAppSettings { MaxTurnIterations = 5000 });
            servico.InvalidateCache();

            servico.LoadSettings().MaxTurnIterations.Should().Be(60);
        }

        // ─────────────────────────────────────────────────────────────────
        // Os consumidores obedecem
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void AFracaoDeMemoria_MUDA_ACota()
        {
            var padrao = MemoryBudget.Compute(12288, 1000);
            var magra = MemoryBudget.Compute(12288, 1000, memoryFraction: 0.10);

            magra.Chapters.Should().BeLessThan(padrao.Chapters);
            magra.Live.Should().BeGreaterThan(padrao.Live, "menos memória sobra mais conversa");
        }

        [Fact]
        public void OGatilhoDeCompactacao_MUDA_OLimiar()
        {
            var quota = MemoryBudget.Compute(12288, 1000);

            MemoryBudget.CompactionThreshold(quota, 0.60)
                .Should().BeLessThan(MemoryBudget.CompactionThreshold(quota, 0.90));
        }

        [Fact]
        public void OTetoDaFala_CORTA_NoNumeroPedido()
        {
            var caixa = Enumerable.Range(0, 6)
                .Select(i => new MailSummary($"assunto {i}", "resumo", MailUrgency.Media))
                .ToList();

            var falaCurta = new FalaDaIA("texto", caixa, teto: 2);
            falaCurta.Emails.Count.Should().Be(2);
            falaCurta.Excedente.Should().Contain("4");

            new FalaDaIA("texto", caixa, teto: 5).Emails.Count.Should().Be(5);
        }

        [Fact]
        public void TetoZERO_NaFala_NaoZERA_ALista()
        {
            // Um teto zero deixaria a fala anunciar "+6 outros" sem mostrar nenhum — a lista
            // some e sobra só a contagem, que não ajuda ninguém.
            var caixa = new[] { new MailSummary("a", "b", MailUrgency.Media) };
            new FalaDaIA("texto", caixa, teto: 0).Emails.Count.Should().Be(1);
        }

        // ─────────────────────────────────────────────────────────────────
        // Restaurar por página
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void RestaurarNoAvancado_VOLTA_OsNumerosDaMemoria()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova(PastaTemporaria(), PaginaDeConfiguracoes.Avancado, s =>
                {
                    s.MaxTurnIterations = 40;
                    s.CompactionTrigger = 0.60;
                    s.EnableIntelligentTools = false;
                });

                Achar<TextBox>(janela, "MaxIterationsTextBox").Text.Should().Be("40");

                Achar<Button>(janela, "SaveButton");   // a página existe
                Restaurar(janela, "Avancado");

                var padrao = new UserAppSettings();
                Achar<TextBox>(janela, "MaxIterationsTextBox").Text
                    .Should().Be(padrao.MaxTurnIterations.ToString());
                Achar<TextBox>(janela, "CompactionTriggerTextBox").Text.Should().Be("85");
                Achar<ToggleButton>(janela, "IntelligentToolsSwitch").IsChecked.Should().BeTrue();

                janela.Close();
            });
        }

        [Fact]
        public void OModeloDoShadow_EUmaLISTA_ComoADoModeloPrincipal()
        {
            // Era caixa de texto: sabia-se o nome de cor ou nao se escolhia. Vira lista
            // editavel, igual a do modelo principal — a mesma consulta enche as duas.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova(PastaTemporaria(), PaginaDeConfiguracoes.Conexao,
                                  s => s.ShadowModelName = "qwen2.5:7b");

                var lista = Achar<ComboBox>(janela, "ShadowModelComboBox");

                lista.IsEditable.Should().BeTrue(
                    "um modelo ainda nao baixado continua sendo escolha legitima");
                lista.Text.Should().Be("qwen2.5:7b");

                janela.Close();
            });
        }

        [Fact]
        public void AsDuasListasDeModelo_NaoCOMPARTILHAM_AColecao()
        {
            // Duas ComboBox apontando para a MESMA instancia de colecao dividem a
            // CollectionView padrao do WPF, e com ela a "currency": mexer numa move a selecao
            // da outra. Cada uma recebe a propria copia.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova(PastaTemporaria(), PaginaDeConfiguracoes.Conexao);

                var principal = Achar<ComboBox>(janela, "ModelComboBox");
                var shadow = Achar<ComboBox>(janela, "ShadowModelComboBox");

                if (principal.ItemsSource != null || shadow.ItemsSource != null)
                    ReferenceEquals(principal.ItemsSource, shadow.ItemsSource)
                        .Should().BeFalse("colecao compartilhada sincroniza a selecao das duas");

                janela.Close();
            });
        }

        [Fact]
        public void RestaurarNoShadow_NaoTOCA_NasOutrasPaginas()
        {
            // O botão é POR PÁGINA justamente por isto: quem quer voltar um número de e-mail
            // atrás não quer perder de quebra o modelo e o provedor.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova(PastaTemporaria(), PaginaDeConfiguracoes.Shadow, s =>
                {
                    s.ShadowAssistantEnabled = true;
                    s.ModelName = "modelo-escolhido-a-dedo";
                    s.MailWindowDays = 12;
                });

                Restaurar(janela, "Shadow");

                Achar<ToggleButton>(janela, "ShadowAssistantSwitch").IsChecked.Should().BeFalse();
                Achar<ComboBox>(janela, "ModelComboBox").Text.Should().Be("modelo-escolhido-a-dedo");
                Achar<TextBox>(janela, "MailWindowTextBox").Text.Should().Be("12");

                janela.Close();
            });
        }

        [Fact]
        public void RestaurarNaConexao_NaoAPAGA_OProvedor()
        {
            // O padrão do provedor é vazio, e vazio não é preferência: é o sinal de "ainda não
            // passou pelo primeiro arranque". Restaurá-lo deixaria o programa sem saber com
            // quem falar, e o usuário sem entender por quê.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova(PastaTemporaria(), PaginaDeConfiguracoes.Conexao,
                                  s => s.AiProvider = "Ollama");

                Restaurar(janela, "Conexao");

                Achar<ComboBox>(janela, "ProviderComboBox").SelectedItem?.ToString()
                    .Should().Be("Ollama");
                Achar<TextBox>(janela, "UrlTextBox").Text.Should().Be(new UserAppSettings().ApiUrl);

                janela.Close();
            });
        }

        [Fact]
        public void RestaurarNoEmail_NaoREMOVE_AsContas()
        {
            // "Restaurar padrões" não pode significar "apagar minhas caixas e as senhas do
            // cofre". Remover conta tem botão próprio, na linha da conta.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                var cofre = new MailVault(pasta);
                cofre.Guardar("ana@gmail.com", "abcdefghijklmnop");

                var janela = Nova(pasta, PaginaDeConfiguracoes.Email, s =>
                {
                    s.MailWindowDays = 20;
                    s.MailAccounts = new System.Collections.Generic.List<MailAccountSettings>
                    {
                        new() { Address = "ana@gmail.com", ImapHost = "imap.gmail.com",
                                ImapPort = 993, IsPrimary = true }
                    };
                });

                janela.Contas.Contas.Should().HaveCount(1);

                Restaurar(janela, "Email");

                janela.Contas.Contas.Should().HaveCount(1, "a conta não é um padrão a restaurar");
                cofre.Existe("ana@gmail.com").Should().BeTrue("a senha continua no cofre");
                Achar<TextBox>(janela, "MailWindowTextBox").Text.Should().Be("3");

                janela.Close();
            });
        }

        [Fact]
        public void TodasAsPaginas_TEM_BotaoDeRestaurar()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova(PastaTemporaria(), PaginaDeConfiguracoes.Identidade);

                var alvos = Botoes(janela).Select(b => (string)b.Tag).OrderBy(t => t).ToList();

                alvos.Should().BeEquivalentTo(
                    Enum.GetNames<PaginaDeConfiguracoes>(),
                    "uma página sem o botão é uma página de onde não se volta atrás");

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Os botões marcados com Tag, pela árvore LÓGICA.
        /// <para>
        /// NÃO pela visual: uma janela que nunca foi mostrada não tem árvore visual, e a busca
        /// voltaria vazia dando a impressão de que os botões não existem. A árvore lógica é o que
        /// o XAML declara, e está de pé desde o InitializeComponent.
        /// </para>
        /// </summary>
        private static System.Collections.Generic.List<Button> Botoes(DependencyObject raiz)
        {
            var achados = new System.Collections.Generic.List<Button>();
            Varrer(raiz);
            return achados;

            void Varrer(DependencyObject no)
            {
                foreach (var filho in LogicalTreeHelper.GetChildren(no))
                {
                    if (filho is Button b && b.Tag is string) achados.Add(b);
                    if (filho is DependencyObject dep) Varrer(dep);
                }
            }
        }

        private static void Restaurar(SettingsWindow janela, string pagina)
        {
            var botao = Botoes(janela).FirstOrDefault(b => (string)b.Tag == pagina);
            botao.Should().NotBeNull("a página " + pagina + " precisa do botão de restaurar");
            botao!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
    }
}
