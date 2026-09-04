using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIB.Services;
using AIB.Services.Mail;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A página Shadow da tela de configurações.
    /// <para>
    /// Até aqui o Shadow só ligava pela bandeja, e a triagem de e-mail não tinha interruptor
    /// nenhum. As duas coisas passam a ser configuração; o que esta suíte trava é que elas
    /// sejam configuração DE VERDADE — a chave grava, muda a tela na hora, e a linha de ajuda
    /// não promete o que não existe.
    /// </para>
    /// </summary>
    public class PaginaShadowTests
    {
        private static string PastaTemporaria()
        {
            string pasta = Path.Combine(Path.GetTempPath(), "aib-shadow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pasta);
            return pasta;
        }

        private static (SettingsWindow janela, MailVault cofre, string caminho) Nova(
            Action<UserAppSettings>? preparar = null)
        {
            string pasta = PastaTemporaria();
            string caminho = Path.Combine(pasta, "settings.json");
            var servico = new SettingsService(caminho);
            var cofre = new MailVault(pasta);

            if (preparar != null)
            {
                var s = servico.LoadSettings();
                preparar(s);
                servico.SaveSettings(s);
            }

            var janela = new SettingsWindow(
                servico, PaginaDeConfiguracoes.Shadow,
                new MailServiceStub(), cofre, new EstadoDasCaixas(pasta));

            return (janela, cofre, caminho);
        }

        private static T Achar<T>(SettingsWindow janela, string nome) where T : class
            => (T)janela.FindName(nome)!;

        // ─────────────────────────────────────────────────────────────────
        // A página existe e é alcançável
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ARotaDireta_ABRE_APaginaShadow()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                janela.PaginaAtiva.Should().Be(PaginaDeConfiguracoes.Shadow);
                Achar<Grid>(janela, "PaginaShadow").Visibility.Should().Be(Visibility.Visible);
                Achar<Grid>(janela, "PaginaEmail").Visibility.Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

        [Fact]
        public void OItemDeNavegacao_FicaENTRE_EmailEAvancado()
        {
            // A ordem do menu não é decorativa: o usuário decora a posição, e mover um item
            // depois muda o alvo do clique de todo mundo sem aviso nenhum.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova();

                var barra = (Panel)Achar<RadioButton>(janela, "NavEmail").Parent;
                var itens = barra.Children.OfType<RadioButton>().Select(r => r.Name).ToList();

                itens.Should().ContainInOrder("NavEmail", "NavShadow", "NavAvancado");

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // As duas chaves
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void AsChavesNASCEM_ComOQueEstavaGravado()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova(s =>
                {
                    s.ShadowAssistantEnabled = true;
                    s.ShadowHandlesMail = true;
                });

                Achar<ToggleButton>(janela, "ShadowAssistantSwitch").IsChecked.Should().BeTrue();
                Achar<ToggleButton>(janela, "ShadowMailSwitch").IsChecked.Should().BeTrue();

                janela.Close();
            });
        }

        [Fact]
        public void SalvarGRAVA_AsDuasChaves()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, caminho) = Nova();

                Achar<ToggleButton>(janela, "ShadowAssistantSwitch").IsChecked = true;
                Achar<ToggleButton>(janela, "ShadowMailSwitch").IsChecked = true;

                Achar<Button>(janela, "SaveButton").RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                var gravado = new SettingsService(caminho).LoadSettings();
                gravado.ShadowAssistantEnabled.Should().BeTrue();
                gravado.ShadowHandlesMail.Should().BeTrue();
            });
        }

        [Fact]
        public void ATriagemNASCE_DESLIGADA()
        {
            // Opt-in, pelo mesmo motivo do Shadow: ninguém ganha um programa lendo o próprio
            // e-mail por ter atualizado a versão.
            new UserAppSettings().ShadowHandlesMail.Should().BeFalse();
            new UserAppSettings().ShadowAssistantEnabled.Should().BeFalse();
        }

        [Fact]
        public void AsDuasChavesSao_INDEPENDENTES()
        {
            // Querer a bola no desktop não é querer que ela abra a caixa de entrada. Amarrar as
            // duas tiraria do usuário a única decisão que ele realmente precisa tomar aqui.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, caminho) = Nova(s => s.ShadowAssistantEnabled = true);

                Achar<ToggleButton>(janela, "ShadowMailSwitch").IsChecked = false;
                Achar<Button>(janela, "SaveButton").RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                var gravado = new SettingsService(caminho).LoadSettings();
                gravado.ShadowAssistantEnabled.Should().BeTrue("o Shadow continua ligado");
                gravado.ShadowHandlesMail.Should().BeFalse("e ainda assim não lê e-mail");
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // A ajuda sai do ESTADO
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void SHADOW_DESLIGADO_DesabilitaATriagem_EDizPorQue()
        {
            // Uma chave ligável que não tem onde agir é a mesma armadilha do ponto âmbar sem
            // saída: parece que funciona, não funciona, e nada na tela explica.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova(s => s.ShadowAssistantEnabled = false);

                Achar<ToggleButton>(janela, "ShadowMailSwitch").IsEnabled.Should().BeFalse();
                Achar<TextBlock>(janela, "ShadowMailAjuda").Text.Should().Contain("Ligue o Shadow");

                janela.Close();
            });
        }

        [Fact]
        public void SEM_CaixaConectada_AAjudaAPONTA_ParaAPaginaCerta()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova(s => s.ShadowAssistantEnabled = true);

                Achar<ToggleButton>(janela, "ShadowMailSwitch").IsEnabled.Should().BeTrue();
                Achar<TextBlock>(janela, "ShadowMailAjuda").Text
                    .Should().Contain("Nenhuma caixa conectada")
                    .And.Contain("E-mail", "dizer que falta algo sem dizer onde resolver é meio aviso");

                janela.Close();
            });
        }

        [Fact]
        public void COM_Caixa_E_Shadow_AAjudaDIZ_OQueATriagemFazHoje()
        {
            // A triagem que resume e prioriza não existe: a varredura de hoje só conta
            // mensagens. Prometer resumo aqui venderia o que ainda não há.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = PastaTemporaria();
                string caminho = Path.Combine(pasta, "settings.json");
                var servico = new SettingsService(caminho);
                var cofre = new MailVault(pasta);

                var s = servico.LoadSettings();
                s.ShadowAssistantEnabled = true;
                s.MailAccounts = new System.Collections.Generic.List<MailAccountSettings>
                {
                    new() { Address = "ana@gmail.com", ImapHost = "imap.gmail.com", ImapPort = 993, IsPrimary = true }
                };
                servico.SaveSettings(s);
                cofre.Guardar("ana@gmail.com", "abcdefghijklmnop");

                var janela = new SettingsWindow(
                    servico, PaginaDeConfiguracoes.Shadow,
                    new MailServiceStub(), cofre, new EstadoDasCaixas(pasta));

                Achar<TextBlock>(janela, "ShadowMailAjuda").Text
                    .Should().Be(SettingsWindow.TextoDaTriagem);

                janela.Close();
            });
        }

        [Fact]
        public void DesligarOShadow_ATUALIZA_AAjudaNaHora()
        {
            // Sem isso a linha continuaria prometendo triagem depois de o usuário desligar o
            // Shadow, e só se corrigiria ao reabrir a tela.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _, _) = Nova(s => s.ShadowAssistantEnabled = true);

                Achar<ToggleButton>(janela, "ShadowAssistantSwitch").IsChecked = false;

                Achar<TextBlock>(janela, "ShadowMailAjuda").Text.Should().Contain("Ligue o Shadow");
                Achar<ToggleButton>(janela, "ShadowMailSwitch").IsEnabled.Should().BeFalse();

                janela.Close();
            });
        }
    }
}
