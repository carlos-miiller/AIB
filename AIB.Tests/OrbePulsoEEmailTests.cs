using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AIB.Services;
using AIB.Ui;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O pulso (§5.2), a varredura de e-mails (§5.6) e o relatório (§5.7).
    /// <para>
    /// É aqui que mora a regra que dá sentido ao produto inteiro: a IA avisa SEM interromper.
    /// O orbe pulsa em silêncio e espera; nada abre sozinho sobre o trabalho de quem está
    /// usando a máquina.
    /// </para>
    /// </summary>
    public class OrbePulsoEEmailTests
    {
        private static ShadowAssistantWindow Nova() => new();

        // ─────────────────────────────────────────────────────────────────────
        // §5.2  Pulso
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void FalaProativa_PulsaEmSilencio_SemAbrirBalao()
        {
            // §0 O3 e §8 A5. Um balão que abre por conta própria sobre o trabalho do usuário é
            // invasivo, e o usuário disse que o problema dele é justamente não ter janela para
            // ser interrompido.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.EnfileirarFala("3 e-mails precisam de você hoje.");

                janela.Pulsando.Should().BeTrue();
                janela.BalaoVisivel.Should().BeFalse("a fala só aparece no clique");
                janela.FalasPendentes.Should().Be(1);

                janela.Close();
            });
        }

        [Fact]
        public void OPulsoNaoExpiraSozinho()
        {
            // A10 — se sumisse por conta própria, a mensagem se perderia sem ninguém saber que
            // existiu. Só o clique encerra.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.EnfileirarFala("algo aconteceu");
                janela.Pulsando.Should().BeTrue();

                janela.AbrirBarra();

                janela.Pulsando.Should().BeFalse("o clique é o que encerra");
                janela.BalaoVisivel.Should().BeTrue("§5.4 — barra e fala de uma vez só");
                janela.TextoDaFala.Should().Be("algo aconteceu");
                janela.FalasPendentes.Should().Be(0);

                janela.Close();
            });
        }

        [Fact]
        public void FalaVazia_NaoFazOOrbePulsar()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.EnfileirarFala("   ");

                janela.Pulsando.Should().BeFalse();
                janela.FalasPendentes.Should().Be(0);

                janela.Close();
            });
        }

        [Fact]
        public void VariasFalas_EntramNumBalaoSo_AsTresMaisRecentes()
        {
            // A §4.6 prevê empilhar até três balões. Um só, com o texto junto, entrega a mesma
            // informação sem construir uma pilha que pode cobrir meia tela.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.EnfileirarFala("primeira");
                janela.EnfileirarFala("segunda");
                janela.EnfileirarFala("terceira");
                janela.EnfileirarFala("quarta");

                janela.AbrirBarra();

                janela.TextoDaFala.Should().NotContain("primeira", "só as três mais recentes");
                janela.TextoDaFala.Should().Contain("segunda");
                janela.TextoDaFala.Should().Contain("quarta");

                janela.Close();
            });
        }

        [Fact]
        public void PulsoUrgente_MudaACor_MasContinuaSendoSoOPulso()
        {
            // Extensão à spec, para o caso do firewall: doze alertas em quarenta minutos é um
            // incidente, e ele não pode se parecer com uma fala comum. O que muda é a COR —
            // continua sem toast, sem badge e sem balão automático.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();
                var anel = (Border)janela.FindName("AnelDePulso");

                janela.EnfileirarFala("link da Vivo oscilando há 47 min", urgente: true);

                janela.BalaoVisivel.Should().BeFalse("urgente não é motivo para interromper");
                anel.BorderBrush.Should().BeSameAs(janela.FindResource("DangerBrush"));

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // §5.6  Varredura
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ProcessandoEmail_TrocaOGlyphPeloIconeDeCaixaDeEntrada()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.ComecarAProcessarEmail();

                ((TextBlock)janela.FindName("Glyph")).Visibility.Should().Be(Visibility.Collapsed);
                ((Path)janela.FindName("IconeDeInbox")).Visibility.Should().Be(Visibility.Visible);
                ((Path)janela.FindName("AnelDeProgresso")).Visibility.Should().Be(Visibility.Visible);
                janela.ProcessandoEmail.Should().BeTrue();

                janela.Close();
            });
        }

        [Fact]
        public void VarreduraSemNadaARelatar_VoltaAoRepousoSemPulsar()
        {
            // A maioria das varreduras não acha nada. Pulsar em todas ensinaria o usuário a
            // ignorar o pulso — que é o único sinal que o sistema tem.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.ComecarAProcessarEmail();
                janela.TerminarDeProcessarEmail();

                janela.ProcessandoEmail.Should().BeFalse();
                janela.Pulsando.Should().BeFalse();
                ((TextBlock)janela.FindName("Glyph")).Visibility.Should().Be(Visibility.Visible);

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // §5.7  Relatório
        // ─────────────────────────────────────────────────────────────────────

        private static List<MailSummary> Caixa(int quantos)
        {
            var lista = new List<MailSummary>();
            for (int i = 0; i < quantos; i++)
                lista.Add(new MailSummary($"Remetente {i}", $"resumo {i}", MailUrgency.Media));
            return lista;
        }

        [Fact]
        public void Relatorio_SoApareceDepoisDoClique_ComALista()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();
                var lista = (ItemsControl)janela.FindName("ListaDeEmails");

                janela.ComecarAProcessarEmail();
                janela.TerminarDeProcessarEmail("Li 34 e-mails. Três precisam de você.", Caixa(3));

                janela.Pulsando.Should().BeTrue();
                lista.Visibility.Should().Be(Visibility.Collapsed, "nada aparece antes do clique");

                janela.AbrirBarra();

                janela.TextoDaFala.Should().Contain("Três precisam de você");
                lista.Visibility.Should().Be(Visibility.Visible);
                lista.Items.Count.Should().Be(3);

                janela.Close();
            });
        }

        [Fact]
        public void MaisDeTres_MostraTresEUmaLinhaDeTexto()
        {
            // A11 — o balão nunca rola. Uma caixa de entrada inteira flutuando no desktop não
            // é o produto.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.TerminarDeProcessarEmail("relatório", Caixa(7));
                janela.AbrirBarra();

                ((ItemsControl)janela.FindName("ListaDeEmails")).Items.Count.Should().Be(3);

                var sobra = (TextBlock)janela.FindName("OutrosEmails");
                sobra.Visibility.Should().Be(Visibility.Visible);
                sobra.Text.Should().Be("+4 outros");

                janela.Close();
            });
        }

        [Fact]
        public void Dispensar_LevaOBalaoInteiro_ListaIncluida()
        {
            // §5.7 — o X dispensa tudo e mantém a barra aberta, para o usuário poder responder
            // por texto ali mesmo.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.TerminarDeProcessarEmail("relatório", Caixa(2));
                janela.AbrirBarra();
                janela.DispensarFala();

                janela.BalaoVisivel.Should().BeFalse();
                ((ItemsControl)janela.FindName("ListaDeEmails")).Visibility
                    .Should().Be(Visibility.Collapsed);
                janela.EmModoBarra.Should().BeTrue();

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // §4.8  Urgência
        // ─────────────────────────────────────────────────────────────────────

        private static object Converter(UrgenciaConverter.Saida modo, MailUrgency nivel) =>
            new UrgenciaConverter { Modo = modo }
                .Convert(nivel, typeof(object), null!, CultureInfo.InvariantCulture);

        [Theory]
        [InlineData(MailUrgency.Maxima, "Máxima")]
        [InlineData(MailUrgency.Media, "Média")]
        [InlineData(MailUrgency.Baixa, "Baixa")]
        public void ORotuloAcompanhaACor(MailUrgency nivel, string esperado) =>
            // A12 — a cor é o sinal de relance, mas o texto fica junto: daltonismo e captura
            // de tela em tons de cinza. Não remover para "limpar" o visual.
            Converter(UrgenciaConverter.Saida.Rotulo, nivel).Should().Be(esperado);

        [Fact]
        public void ABarraEOSeloUsamAMesmaCor()
        {
            // As duas são o mesmo sinal lido de dois jeitos. Se divergirem, um item fica
            // vermelho de um lado e laranja do outro.
            var barra = (SolidColorBrush)Converter(UrgenciaConverter.Saida.Cor, MailUrgency.Maxima);
            var selo = (SolidColorBrush)Converter(UrgenciaConverter.Saida.Cor, MailUrgency.Maxima);

            barra.Color.Should().Be(selo.Color);
            barra.Color.Should().Be((Color)ColorConverter.ConvertFromString("#FFE5484D"));
        }

        [Fact]
        public void OFundoDoSeloEhATranslucidaDaMesmaCor()
        {
            var fundo = (SolidColorBrush)Converter(UrgenciaConverter.Saida.Fundo, MailUrgency.Media);

            fundo.Color.A.Should().BeLessThan(0x40, "§4.8 — a mesma cor a 13%");
            fundo.Color.R.Should().Be(0xE8);
        }
    }
}
