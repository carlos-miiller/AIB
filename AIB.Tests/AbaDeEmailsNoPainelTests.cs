using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIB.Services;
using AIB.Ui;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A quarta aba do painel lateral — tela-chat-v3 §6.2, §6.2.1 e §6.6 E3–E7.
    /// <para>
    /// O que esta suíte trava não é o desenho: é o conjunto de decisões que, erradas, fazem a
    /// aba MENTIR. Um rodapé de contagens visível sem conta conectada anuncia "0 e-mails" como
    /// se a caixa estivesse vazia, quando não se olhou caixa nenhuma; um convite à configuração
    /// mostrado com a conta já conectada manda o usuário resolver um problema que ele já
    /// resolveu.
    /// </para>
    /// </summary>
    public class AbaDeEmailsNoPainelTests
    {
        private static void EmSta(Action acao) => WpfHost.EmSta(acao);

        private static readonly IReadOnlyList<MailSummary> Nenhum = Array.Empty<MailSummary>();

        private static SidePanelWindow Painel(bool configurado, IReadOnlyList<MailSummary>? emails = null)
            => new(emailConfigurado: () => configurado,
                   emails: () => emails ?? Nenhum);

        private static T Achar<T>(SidePanelWindow painel, string nome) where T : class
            => (T)painel.FindName(nome)!;

        // ─────────────────────────────────────────────────────────────────
        // E4 — a aba existe e está no lugar certo
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void AAbaDeEmails_EA_SEGUNDA_DaBarra()
        {
            // E4 fixa a ordem: Histórico | E-mails | Arquivos | Ações. A ordem importa porque
            // as abas são SÓ ícones — não há rótulo para reancorar quem decorou a posição, e
            // trocar a ordem depois moveria o clique de todo mundo sem aviso.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: false);

                var barra = (Panel)Achar<RadioButton>(painel, "AbaHistorico").Parent;
                var abas = barra.Children.OfType<RadioButton>().Select(a => a.Name).ToList();

                abas.Should().Equal("AbaHistorico", "AbaEmails", "AbaArquivos", "AbaAcoes");

                painel.Close();
            });
        }

        [Fact]
        public void MarcarAAba_MOSTRA_APaneDeEmails()
        {
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: false);

                Achar<RadioButton>(painel, "AbaEmails").IsChecked = true;

                Achar<Grid>(painel, "PaneEmails").Visibility.Should().Be(Visibility.Visible);
                Achar<Grid>(painel, "PaneHistorico").Visibility.Should().Be(Visibility.Collapsed);

                painel.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // E7 / §6.2.1 — os três estados
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void SEM_ContaConectada_OConviteAPARECE_EORodapeSOME()
        {
            // A17 pede UM gatilho governando lista, rodapé e convite. Sem isso o rodapé fica de
            // pé e escreve "0 e-mails • 0 urgentes • 0 tokens" — que se lê como "sua caixa está
            // vazia" quando o que houve foi não ter caixa nenhuma.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: false);

                Achar<StackPanel>(painel, "ConviteDeEmail").Visibility.Should().Be(Visibility.Visible);
                Achar<ScrollViewer>(painel, "RoloDeEmails").Visibility.Should().Be(Visibility.Collapsed);
                Achar<StackPanel>(painel, "EmailsRodapeCaixa").Visibility.Should().Be(Visibility.Collapsed);

                painel.Close();
            });
        }

        [Fact]
        public void COM_Conta_E_ZeroEmails_EListaVazia_NaoConvite()
        {
            // §6.2.1 separa os dois vazios de propósito: (a) sem conta pede configuração, (b)
            // conta conectada e caixa limpa não pede nada. Mostrar o convite em (b) mandaria o
            // usuário reconfigurar uma conta que já funciona.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: true);

                Achar<StackPanel>(painel, "ConviteDeEmail").Visibility.Should().Be(Visibility.Collapsed);
                Achar<TextBlock>(painel, "EmailsVazio").Visibility.Should().Be(Visibility.Visible);
                Achar<StackPanel>(painel, "EmailsRodapeCaixa").Visibility.Should().Be(Visibility.Visible);

                painel.Close();
            });
        }

        [Fact]
        public void ALinhaDeTitulo_FICA_DePe_MesmoSemConta()
        {
            // A8 — toda pane usa a mesma linha de título para o cabeçalho não pular de altura
            // ao trocar de aba, e §6.2.1 diz que ela CONTINUA visível no estado de convite.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: false);

                var pane = Achar<Grid>(painel, "PaneEmails");
                var titulo = pane.Children.OfType<Grid>()
                    .SelectMany(g => g.Children.OfType<TextBlock>())
                    .FirstOrDefault(t => t.Text == "E-mails");

                titulo.Should().NotBeNull("a linha de título não some com a lista");
                titulo!.Visibility.Should().Be(Visibility.Visible);

                painel.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // E5 — a lista
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void AListaSAI_PorUrgenciaDECRESCENTE()
        {
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: true, emails: new[]
                {
                    new MailSummary("baixa", "", MailUrgency.Baixa),
                    new MailSummary("maxima", "", MailUrgency.Maxima),
                    new MailSummary("media", "", MailUrgency.Media)
                });

                var lista = Achar<ItemsControl>(painel, "ListaEmails");
                var nomes = lista.ItemsSource.Cast<MailSummary>().Select(e => e.Name).ToList();

                nomes.Should().Equal("maxima", "media", "baixa");

                painel.Close();
            });
        }

        [Fact]
        public void DentroDoMESMO_Nivel_AOrdemDeChegadaSOBREVIVE()
        {
            // "urgência decrescente e, dentro do mesmo nível, mais recente no topo" só funciona
            // se a ordenação for ESTÁVEL — a lista já chega com o mais recente primeiro. Uma
            // ordenação instável embaralharia os empates a cada remontagem da aba.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: true, emails: new[]
                {
                    new MailSummary("recente", "", MailUrgency.Media),
                    new MailSummary("antigo", "", MailUrgency.Media)
                });

                var lista = Achar<ItemsControl>(painel, "ListaEmails");
                lista.ItemsSource.Cast<MailSummary>().Select(e => e.Name)
                    .Should().Equal("recente", "antigo");

                painel.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // E6 — o rodapé de três medidas
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ORodapeTraz_AsTRES_Medidas()
        {
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: true, emails: new[]
                {
                    new MailSummary("um", "resumo de teste", MailUrgency.Maxima),
                    new MailSummary("dois", "outro resumo", MailUrgency.Media),
                    new MailSummary("tres", "mais um", MailUrgency.Baixa)
                });

                string texto = Achar<TextBlock>(painel, "EmailsRodape").Text;

                texto.Should().Contain("3 e-mails");
                texto.Should().Contain("2 urgentes", "urgente é Máxima + Média, não só Máxima");
                texto.Should().Contain("tokens");

                painel.Close();
            });
        }

        [Fact]
        public void ZERO_Urgentes_NaoSAI_EmCorDeAlerta()
        {
            // Um "0 urgentes" em âmbar chama atenção para a AUSÊNCIA de alerta — o contrário do
            // que a cor existe para fazer.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var comUrgencia = Painel(configurado: true, emails: new[]
                {
                    new MailSummary("um", "", MailUrgency.Media)
                });
                var semUrgencia = Painel(configurado: true, emails: new[]
                {
                    new MailSummary("um", "", MailUrgency.Baixa)
                });

                var ambar = ((SolidColorBrush)Achar<TextBlock>(comUrgencia, "EmailsRodape").Foreground).Color;
                var cinza = ((SolidColorBrush)Achar<TextBlock>(semUrgencia, "EmailsRodape").Foreground).Color;

                ambar.Should().Be(((SolidColorBrush)UrgenciaConverter.CorDe(MailUrgency.Media)).Color,
                    "a cor vem do mesmo lugar que a do selo, nunca de um hex digitado na View");
                cinza.Should().NotBe(ambar);

                comUrgencia.Close();
                semUrgencia.Close();
            });
        }

        [Fact]
        public void OsQUATRO_Rodapes_NaoQuebramLinha()
        {
            // A16 — o painel dá 269px úteis e o rodapé de e-mails leva três medidas. Se ele
            // quebrar em duas linhas, a base dos quatro rodapés deixa de coincidir e o conteúdo
            // pula ao trocar de aba (A8).
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: true);

                foreach (var nome in new[] { "HistoricoRodape", "EmailsRodape", "ArquivosRodape", "AcoesRodape" })
                {
                    var rodape = Achar<TextBlock>(painel, nome);
                    rodape.TextWrapping.Should().Be(TextWrapping.NoWrap, nome + " não pode quebrar");
                    rodape.MinHeight.Should().Be(25, nome + " precisa da altura comum");
                }

                painel.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // E3 — o item é COMPARTILHADO com o orbe
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void OItemDaLista_EO_MESMO_ControleDoOrbe()
        {
            // E3 manda reusar o controle do Shadow Assistant. Duas cópias do mesmo item
            // divergiriam na primeira correção feita só de um lado — e este layout, diz a
            // própria spec, ainda vai ser revisto.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: true);

                var lista = Achar<ItemsControl>(painel, "ListaEmails");
                var item = lista.ItemTemplate.LoadContent();

                item.Should().BeOfType<MailListItem>();

                painel.Close();
            });
        }

        [Fact]
        public void NoPainel_OItemUsa_Raio14_EHoverLilas()
        {
            // As DUAS únicas diferenças permitidas entre o item do painel e o do orbe.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = Painel(configurado: true);

                var item = (MailListItem)Achar<ItemsControl>(painel, "ListaEmails").ItemTemplate.LoadContent();

                item.CornerRadius.Should().Be(new CornerRadius(14));
                item.RealceLilas.Should().BeTrue();

                painel.Close();
            });
        }

        [Fact]
        public void NoOrbe_OItemNasce_Raio12_ESemHover()
        {
            // O padrão do controle é o do orbe: lá a pilha é leitura de passagem sobre o
            // desktop, e um realce a cada item sob o cursor viraria ruído.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var item = new MailListItem();

                item.CornerRadius.Should().Be(new CornerRadius(12));
                item.RealceLilas.Should().BeFalse();
            });
        }

        [Fact]
        public void UrlVAZIA_NaoCHAMA_OShell()
        {
            // Clique num e-mail sem endereço de thread não pode virar Process.Start(""), que
            // levanta exceção. O item vive nas duas telas: falhar aqui derrubaria o orbe, que é
            // a única coisa entre o usuário e a lista que ele acabou de ler.
            MailListItem.AbrirNoNavegador("").Should().BeFalse();
            MailListItem.AbrirNoNavegador(null).Should().BeFalse();
            MailListItem.AbrirNoNavegador("   ").Should().BeFalse();
        }
    }
}
