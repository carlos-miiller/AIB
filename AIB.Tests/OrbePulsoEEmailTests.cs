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

                janela.MostrarFala("qualquer coisa");
                var balao = (Border)janela.ElementoDaFala(0, "Balao")!;

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
        // §4.6  A pilha de falas
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Enviar_CriaABolhaDoUsuario()
        {
            // O defeito: o texto sumia do campo e nao reaparecia em lugar nenhum. Quem mandava
            // a mensagem ficava olhando uma barra vazia com um anel girando, sem confirmacao do
            // que tinha sido enviado.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.AbrirBarra();
                ((TextBox)janela.FindName("Campo")).Text = "  qual o ramal do Fernando  ";
                janela.Enviar();

                janela.Falas.Should().HaveCount(1);
                janela.Falas[0].Should().BeOfType<FalaDoUsuario>();
                janela.Falas[0].Texto.Should().Be("qual o ramal do Fernando");
                janela.BalaoVisivel.Should().BeTrue();

                janela.Close();
            });
        }

        [Fact]
        public void FecharEReabrirABarra_DEVOLVE_AConversa()
        {
            // O outro defeito: fechar a barra descartava as bolhas, e reabrir dava uma barra
            // vazia. Fechar ESCONDE; so o X descarta. A pergunta e a resposta continuam la
            // porque e a resposta que o usuario voltou para ler.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.AbrirBarra();
                ((TextBox)janela.FindName("Campo")).Text = "qual o ramal";
                janela.Enviar();
                janela.ResponderTurno("4275");

                janela.Falas.Should().HaveCount(2);

                janela.FecharBarra();
                janela.BalaoVisivel.Should().BeFalse("sem barra, as bolhas apontariam para nada");
                janela.Falas.Should().HaveCount(2, "escondido nao e descartado");

                janela.AbrirBarra();
                janela.BalaoVisivel.Should().BeTrue();
                janela.Falas.Should().HaveCount(2);
                janela.TextoDaFala.Should().Be("4275");

                janela.Close();
            });
        }

        [Fact]
        public void AOrdemDaPilha_EhMaisRecenteEmbaixo()
        {
            // §4.6 — "mais recente embaixo". A pilha e a leitura de cima para baixo da ultima
            // troca; inverter deixaria a resposta acima da pergunta que a gerou.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                janela.AbrirBarra();
                ((TextBox)janela.FindName("Campo")).Text = "pergunta";
                janela.Enviar();
                janela.ResponderTurno("resposta");

                janela.Falas[0].Texto.Should().Be("pergunta");
                janela.Falas[1].Texto.Should().Be("resposta");

                janela.Close();
            });
        }

        [Fact]
        public void APilha_NaoTemTeto_ELA_ROLA()
        {
            // Nao ha mais teto de bolhas. O que limita e a ALTURA do rolo: um teto de contagem
            // apagava a pergunta que explicava a resposta ainda visivel logo abaixo dela, e o
            // que precisa nao crescer sem fim e o espaco que a janela ocupa na tela.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();
                var rolo = (ScrollViewer)janela.FindName("RoloDasFalas");

                janela.AbrirBarra();
                for (int i = 1; i <= 6; i++)
                {
                    ((TextBox)janela.FindName("Campo")).Text = $"pergunta {i}";
                    janela.Enviar();
                    janela.ResponderTurno($"resposta {i}");
                }

                janela.Falas.Should().HaveCount(12, "nada e descartado");
                janela.Falas[0].Texto.Should().Be("pergunta 1", "a mais antiga continua la");

                // Era 450; o usuário pediu uns 35% a mais: "está muito pequeno".
                rolo.MaxHeight.Should().Be(608, "o limite e de altura, nao de contagem");
                rolo.VerticalScrollBarVisibility.Should().Be(ScrollBarVisibility.Hidden,
                    "a rolagem e invisivel: a roda do mouse basta");

                janela.Close();
            });
        }

        [Fact]
        public void OTopoDaPilhaRolada_TerminaEmDesvanecimento()
        {
            // Sem a mascara, a pilha cheia termina em corte reto no meio de uma bolha, o que
            // sobre o desktop le como defeito de desenho. Com ela FIXA, o topo da primeira
            // bolha ficaria lavado mesmo sem nada cortado atras — entao ela entra e sai
            // conforme a posicao do rolo.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();
                var rolo = (ScrollViewer)janela.FindName("RoloDasFalas");

                janela.AbrirBarra();
                janela.MostrarFala("uma linha so");

                janela.AtualizarDesvanecimento(0);
                rolo.OpacityMask.Should().BeNull("nada acima da vista, nada a desvanecer");

                janela.AtualizarDesvanecimento(120);
                rolo.OpacityMask.Should().NotBeNull("o que ficou acima termina em fade");

                janela.Close();
            });
        }

        [Fact]
        public void ODesvanecimento_Tem32pxFIXOS()
        {
            // Coordenada absoluta, e nao relativa: com mapeamento relativo os 32px encolheriam
            // junto com a pilha e o fade sumiria justamente quando ela esta curta.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();

                var fade = (LinearGradientBrush)janela.FindResource("DesvanecimentoDoTopo");

                fade.MappingMode.Should().Be(BrushMappingMode.Absolute);
                fade.StartPoint.Should().Be(new Point(0, 0));
                fade.EndPoint.Should().Be(new Point(0, 32));
                fade.GradientStops[0].Color.A.Should().Be(0x00, "o topo e transparente");
                fade.GradientStops[1].Color.A.Should().Be(0xFF, "e 32px abaixo ja e opaco");

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

                deFora.Width.Should().Be(71, "56 do orbe mais 7,5 de folga de cada lado");

                janela.Close();
            });
        }

        [Fact]
        public void OAnelDoPulso_TambemNaoMEXE_NaAlturaDoPalco()
        {
            // O anel do pulso tem 61 e divide a celula com a casca de 56. Mesma armadilha do
            // anel de progresso: sem a margem de -2,5 a linha Auto cresceria e o orbe pularia
            // na tela toda vez que uma fala chegasse — justo quando ele deve so pulsar.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();
                var palco = (Grid)janela.FindName("Palco");
                var infinito = new Size(double.PositiveInfinity, double.PositiveInfinity);

                palco.Measure(infinito);
                double parado = palco.DesiredSize.Height;

                janela.EnfileirarFala("chegou algo");
                palco.Measure(infinito);

                palco.DesiredSize.Height.Should().Be(parado);

                janela.Close();
            });
        }

        [Fact]
        public void AJanela_CabeOPalcoInteiro()
        {
            // O defeito: a margem do palco subiu de 40 para 48 e a largura da janela ficou em
            // 600. O palco passou a precisar de 520+48+48 = 616, e os 8px que sobravam de cada
            // lado eram recortados — a sombra e a onda do pulso terminavam num corte reto.
            // A largura da janela PRECISA acompanhar a margem; este ensaio e o que avisa.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();
                var palco = (Grid)janela.FindName("Palco");

                double precisa = palco.Width + palco.Margin.Left + palco.Margin.Right;

                janela.Width.Should().BeGreaterThanOrEqualTo(precisa,
                    "o que passar da janela e recortado, nao desenhado");

                janela.Close();
            });
        }

        [Fact]
        public void AOndaDoPulso_CabeDentroDaJanela()
        {
            // A onda cresce por RenderTransform, que nao mexe no layout — mas a janela
            // RECORTA o que passar dela. Se a expansao crescer sem a margem crescer junto, a
            // borda da onda sai cortada em linha reta nos ultimos quadros.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = Nova();
                var anel = (Border)janela.FindName("AnelDePulso");
                var palco = (Grid)janela.FindName("Palco");

                double raioMaximo = anel.Width / 2 * 2.34;
                double meiaJanela = 56 / 2.0 + palco.Margin.Top;

                raioMaximo.Should().BeLessThan(meiaJanela,
                    "a margem da janela precisa acompanhar a expansao do pulso");

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

        [Fact]
        public void PararDeProcessar_NAO_ApagaOsEmailsQueORelatorioEntregou()
        {
            // O vigia avisa o fim de TODA passada, inclusive das que falaram. O aviso chega
            // depois do relatorio, entao ele nao pode carregar relatorio nulo para o mesmo
            // metodo — faria a lista de e-mails sumir logo depois de ser entregue.
            WpfHost.EmSta(() =>
            {
                var janela = new ShadowAssistantWindow();

                janela.ComecarAProcessarEmail();
                janela.TerminarDeProcessarEmail("3 precisam de voce", Caixa(3));
                janela.PararDeProcessarEmail();

                janela.ProcessandoEmail.Should().BeFalse();
                janela.Trabalhando.Should().BeFalse();

                janela.AbrirBarra();

                var fala = (FalaDaIA)janela.Falas[^1];
                fala.Emails.Count.Should().Be(3, "o aviso de fim nao mexe no que o relatorio deixou");

                janela.Close();
            });
        }

        [Fact]
        public void PararDeProcessar_SOZINHO_DevolveOOrbeAoRepouso()
        {
            WpfHost.EmSta(() =>
            {
                var janela = new ShadowAssistantWindow();

                janela.ComecarAProcessarEmail();
                janela.ProcessandoEmail.Should().BeTrue();

                janela.PararDeProcessarEmail();
                janela.PararDeProcessarEmail();   // idempotente: o caso normal é chamar duas vezes

                janela.ProcessandoEmail.Should().BeFalse();
                janela.Trabalhando.Should().BeFalse();
                janela.Pulsando.Should().BeFalse("nada foi enfileirado");
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

                janela.ComecarAProcessarEmail();
                janela.TerminarDeProcessarEmail("Li 34 e-mails. Três precisam de você.", Caixa(3));

                janela.Pulsando.Should().BeTrue();
                janela.Falas.Should().BeEmpty("nada aparece antes do clique");

                janela.AbrirBarra();

                janela.TextoDaFala.Should().Contain("Três precisam de você");

                var fala = (FalaDaIA)janela.Falas[^1];
                fala.Emails.Count.Should().Be(3);
                fala.VisibilidadeDaLista.Should().Be(Visibility.Visible);

                var lista = (ItemsControl)janela.ElementoDaFala(0, "ListaDeEmails")!;
                lista.Items.Count.Should().Be(3, "o que o modelo diz precisa chegar à tela");

                // E3 de tela-chat §6.6 — o item é o MESMO controle da aba de e-mails do
                // painel, com o ajuste do orbe: raio 12 e sem hover. A pilha aqui é leitura
                // de passagem sobre o desktop, e um realce a cada item sob o cursor viraria
                // ruído. Duas cópias do item divergiriam na primeira correção de um lado só.
                var item = (AIB.Views.MailListItem)lista.ItemTemplate.LoadContent();
                item.CornerRadius.Should().Be(new CornerRadius(12));
                item.RealceLilas.Should().BeFalse();

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

                var fala = (FalaDaIA)janela.Falas[^1];
                fala.Emails.Count.Should().Be(3);
                fala.Excedente.Should().Be("+4 outros");
                fala.VisibilidadeDoExcedente.Should().Be(Visibility.Visible);

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
                janela.Falas.Should().BeEmpty("o X leva o balão inteiro, lista incluída");
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
