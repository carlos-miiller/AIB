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
        // Legibilidade sobre o desktop
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OBalaoEhOPACO_NaoTranslucido()
        {
            // A §4.6 manda surfaceCard, que e branco a 3,5%. Funciona no mock porque o <body>
            // dele tem um degrade escuro atras. No desktop nao ha nada atras: a janela e
            // transparente, e o balao ficava com o papel de parede aparecendo atraves dele.
            // A janela de chat nao sofre disso porque os cards dela ficam DENTRO do vidro da
            // janela; este balao flutua sozinho e precisa ser o proprio vidro (§0 O6).
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();
                var balao = (Border)janela.FindName("Balao");

                var fundo = (SolidColorBrush)balao.Background;

                fundo.Color.A.Should().BeGreaterThan(0xC8,
                    "sobre o desktop, qualquer coisa abaixo de ~90% deixa o texto disputar com o papel de parede");

                janela.Close();
            });
        }

        [Fact]
        public void OsItensDeEmail_SaoTranslucidosSOBRE_OBalao()
        {
            // Aqui a transparencia esta certa e e o contrario do caso acima: o item de e-mail
            // nao flutua sobre o desktop, ele fica sobre o balao, que ja e opaco. O branco a
            // 5% vira a separacao sutil entre um item e outro.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                var fundoDoItem = (SolidColorBrush)janela.FindResource("ReadFill05Brush");

                fundoDoItem.Color.A.Should().BeLessThan(0x40);

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Chegada da resposta de um turno
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void RespostaComABarraAberta_ApareceDireto()
        {
            // O usuario esta olhando para a barra, esperando o que ele mesmo pediu. Faze-lo
            // clicar de novo para ver a propria resposta seria pedir um gesto que nao informa
            // nada.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.AbrirBarra();
                janela.ComecarATrabalhar();
                janela.ResponderTurno("o ramal e 4275");

                janela.BalaoVisivel.Should().BeTrue();
                janela.Pulsando.Should().BeFalse();
                janela.Trabalhando.Should().BeFalse();

                janela.Close();
            });
        }

        [Fact]
        public void RespostaComABarraFECHADA_EnfileiraEPulsa()
        {
            // Este era o defeito escondido: a resposta abria um balao sozinho, ancorado numa
            // barra que nao estava mais na tela — uma caixa de texto flutuando sobre o desktop
            // apontando para nada. Quem perguntou e foi fazer outra coisa recebe o mesmo
            // tratamento de qualquer fala proativa: o pulso espera.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.AbrirBarra();
                janela.ComecarATrabalhar();
                janela.FecharBarra();

                janela.ResponderTurno("demorei, mas achei");

                janela.BalaoVisivel.Should().BeFalse("nao ha barra para ancorar o balao");
                janela.Pulsando.Should().BeTrue();
                janela.FalasPendentes.Should().Be(1);

                janela.AbrirBarra();
                janela.TextoDaFala.Should().Be("demorei, mas achei");

                janela.Close();
            });
        }

        [Fact]
        public void ADiferencaEntrePedidoEProativo()
        {
            // A varredura de e-mail e proativa por definicao: SEMPRE enfileira e pulsa, mesmo
            // com a barra aberta. Ninguem pediu por ela, entao ela nao tem direito de ocupar a
            // tela — e a §0 O3 em duas linhas de codigo.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.AbrirBarra();
                janela.TerminarDeProcessarEmail("38 triados, 3 para voce", Caixa(3));

                janela.BalaoVisivel.Should().BeFalse();
                janela.Pulsando.Should().BeTrue();

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // §5.6  Varredura
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OAnelDeProgresso_FicaPOR_FORA_DoOrbe()
        {
            // §5.5 — "anel de progresso indeterminado ao redor da borda". O CSS do mock usa
            // inset:-3px: um anel MAIOR que o circulo, contornando-o. A primeira versao pos o
            // arco na celula do glyph, e ele girava espremido DENTRO do orbe.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.ComecarATrabalhar();

                var deFora = (Path)janela.FindName("AnelDoOrbe");
                var noGlyph = (Path)janela.FindName("AnelDeProgresso");

                deFora.Visibility.Should().Be(Visibility.Visible);
                noGlyph.Visibility.Should().Be(Visibility.Collapsed);

                deFora.Width.Should().Be(62, "56 do orbe mais 3 de folga de cada lado");

                janela.Close();
            });
        }

        [Fact]
        public void OAnelNaoMEXE_NaAlturaDoPalco()
        {
            // O defeito: o anel tem 62px e divide a celula com a casca de 56. A linha e Auto,
            // entao ela crescia para 62 quando ele aparecia; a janela cresce junto porque a
            // altura e SizeToContent, o reposicionamento subia o Top em 6, e a casca descia 3
            // dentro da linha maior. Liquido, o orbe pulava 3px para cima ao comecar a
            // trabalhar e voltava ao terminar.
            // A margem de -3 e o inset:-3px do mock: a pegada de LAYOUT volta a 56x56 e o
            // anel transborda so no desenho.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();
                var palco = (Grid)janela.FindName("Palco");

                var infinito = new Size(double.PositiveInfinity, double.PositiveInfinity);

                palco.Measure(infinito);
                double parado = palco.DesiredSize.Height;

                janela.ComecarATrabalhar();
                palco.Measure(infinito);
                double trabalhando = palco.DesiredSize.Height;

                trabalhando.Should().Be(parado, "o anel nao pode empurrar o orbe na tela");

                janela.Close();
            });
        }

        [Fact]
        public void OAnelDaBarra_EstaCENTRADO_NoSimbolo()
        {
            // O defeito: glyph, icone de inbox e anel tinham cada um a propria margem
            // esquerda — 20, 15 e 13, todas chutadas — e por isso nenhum coincidia com os
            // outros. O anel aparecia deslocado do simbolo que ele deveria envolver.
            // Agora os tres sao centrados na MESMA celula, e coincidir deixa de ser calculo
            // para virar estrutura.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                var anel = (Path)janela.FindName("AnelDeProgresso");
                var glyph = (TextBlock)janela.FindName("Glyph");
                var inbox = (Path)janela.FindName("IconeDeInbox");

                anel.HorizontalAlignment.Should().Be(HorizontalAlignment.Center);
                glyph.HorizontalAlignment.Should().Be(HorizontalAlignment.Center);
                inbox.HorizontalAlignment.Should().Be(HorizontalAlignment.Center);

                anel.Parent.Should().BeSameAs(glyph.Parent, "tem de ser a mesma celula");
                inbox.Parent.Should().BeSameAs(glyph.Parent);

                janela.Close();
            });
        }

        [Fact]
        public void OAnelDaBarra_NaoAlargaACelula()
        {
            // O anel tem 26 e o simbolo uns 13. Sem a margem de -13 a celula passaria a ter a
            // largura do ANEL, e tanto o simbolo quanto tudo o que vem a direita dele na barra
            // andariam quando o trabalho comecasse.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();
                var celula = (Grid)janela.FindName("CelulaDoGlyph");

                var infinito = new Size(double.PositiveInfinity, double.PositiveInfinity);

                celula.Measure(infinito);
                double parado = celula.DesiredSize.Width;

                janela.AbrirBarra();
                janela.ComecarATrabalhar();
                celula.Measure(infinito);

                celula.DesiredSize.Width.Should().Be(parado,
                    "o anel envolve o simbolo, nao empurra o que esta ao lado");

                janela.Close();
            });
        }

        [Fact]
        public void ComABarraAberta_OAnelVaiParaOGlyph()
        {
            // §5.6 — com o orbe expandido nao ha circulo em volta do que girar, e a spec
            // resolve assim: "o anel continua girando no glyph pequeno da ponta esquerda".
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.AbrirBarra();
                janela.ComecarATrabalhar();

                ((Path)janela.FindName("AnelDoOrbe")).Visibility.Should().Be(Visibility.Collapsed);
                ((Path)janela.FindName("AnelDeProgresso")).Visibility.Should().Be(Visibility.Visible);

                janela.Close();
            });
        }

        [Fact]
        public void OMorph_TrocaOAnelDeLugar()
        {
            // Abrir a barra no meio de um turno tem de mover o anel, nao apaga-lo: o trabalho
            // continua, e o unico sinal de que ele continua e esse arco girando.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.ComecarATrabalhar();
                ((Path)janela.FindName("AnelDoOrbe")).Visibility.Should().Be(Visibility.Visible);

                janela.AbrirBarra();
                ((Path)janela.FindName("AnelDoOrbe")).Visibility.Should().Be(Visibility.Collapsed);
                ((Path)janela.FindName("AnelDeProgresso")).Visibility.Should().Be(Visibility.Visible);

                janela.FecharBarra();
                ((Path)janela.FindName("AnelDoOrbe")).Visibility.Should().Be(Visibility.Visible);

                janela.Close();
            });
        }

        [Fact]
        public void SemTrabalho_NenhumDosDoisAneisAparece()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.ComecarATrabalhar();
                janela.ResponderTurno("pronto");

                ((Path)janela.FindName("AnelDoOrbe")).Visibility.Should().Be(Visibility.Collapsed);
                ((Path)janela.FindName("AnelDeProgresso")).Visibility.Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

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
                ((Path)janela.FindName("AnelDoOrbe")).Visibility.Should().Be(Visibility.Visible,
                    "no estado orbe o anel contorna a borda, nao fica dentro");
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
