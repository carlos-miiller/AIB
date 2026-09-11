using System;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O modo e-mail da área central — tela-chat-v3 §3.3(d), §3.10 e §3.11.
    /// <para>
    /// Substitui a suíte da antiga aba 2 do painel (§6.2), que saiu junto com a aba. O que esta
    /// trava não é o desenho: é o conjunto de decisões que, erradas, fazem a tela MENTIR — as
    /// contagens da caixa aparecendo enquanto se lê UM e-mail, o convite de conversa vazia por
    /// cima de uma caixa cheia, o caminho de volta armado depois de já se ter voltado.
    /// </para>
    /// </summary>
    public class ModoDeEmailNaJanelaTests
    {
        private static void EmSta(Action acao) => WpfHost.EmSta(acao);

        private static T Achar<T>(DependencyObject janela, string nome) where T : class
            => (T)((FrameworkElement)janela).FindName(nome)!;

        private static MailSummary Email(string assunto = "Jurídico — contrato Vertex") =>
            new(assunto, "Pedem sua assinatura no aditivo até as 18h.",
                MailUrgency.Maxima,
                Url: "https://mail.google.com/mail/u/0/#inbox/abc",
                Account: "eu@gmail.com",
                LastMessageAt: new DateTime(2026, 9, 10, 9, 12, 0),
                MessageCount: 4,
                AwaitingMe: true,
                ThreadId: "123",
                De: "juridico@vertex.com.br");

        // ─────────────────────────────────────────────────────────────────
        // §3.3(d) — o switch troca a área central
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ModoEmail_TROCA_AListaDeMensagensPelaCaixa()
        {
            // Os dois são irmãos na MESMA linha da grade: só a Visibility muda, e por isso a
            // janela não muda de tamanho ao trocar de modo (§8 A2).
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;

                Achar<Grid>(janela, "CaixaDeEntrada").Visibility.Should().Be(Visibility.Visible);
                Achar<Grid>(janela, "CabecalhoDoEmail").Visibility.Should().Be(Visibility.Visible);
                Achar<ScrollViewer>(janela, "ChatScrollViewer").Visibility.Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

        [Fact]
        public void NaLISTA_ABarraDeInputSOME()
        {
            // Não se fala com a IA a partir da lista: ali a área central é a caixa de ponta a
            // ponta. A barra volta na LEITURA, e só lá (§3.11).
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;

                Achar<Grid>(janela, "BarraDeInput").Visibility.Should().Be(Visibility.Collapsed);
                Achar<Grid>(janela, "RodapeDeContexto").Visibility.Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

        [Fact]
        public void NoModoEmail_OConviteDeConversaVaziaNaoAPARECE()
        {
            // "Nenhuma conversa ainda" é do CHAT. Anunciá-lo por cima de uma caixa de entrada
            // seria descrever a tela errada.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;

                Achar<StackPanel>(janela, "EmptyState").Visibility.Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // §3.11 — a leitura com a IA
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void NaLEITURA_OTituloVIRA_Caminho()
        {
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                Achar<TextBlock>(janela, "TituloDaCaixa").Visibility.Should().Be(Visibility.Collapsed);
                Achar<Grid>(janela, "CaminhoDaLeitura").Visibility.Should().Be(Visibility.Visible);
                Achar<TextBlock>(janela, "AssuntoDaLeitura").Text
                    .Should().Be("Jurídico — contrato Vertex");

                janela.Close();
            });
        }

        [Fact]
        public void NaLEITURA_AsContagensEAEngrenagemSAEM()
        {
            // As três medidas descrevem a CAIXA. Dentro de um item elas continuariam certas e
            // continuariam respondendo a outra pergunta.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                Achar<TextBlock>(janela, "MedidasDaCaixa").Visibility.Should().Be(Visibility.Collapsed);
                Achar<Button>(janela, "EngrenagemDoEmail").Visibility.Should().Be(Visibility.Collapsed);
                Achar<StackPanel>(janela, "AcoesDaLeitura").Visibility.Should().Be(Visibility.Visible);

                janela.Close();
            });
        }

        [Fact]
        public void NaLEITURA_ABarraDeInputVOLTA_ComPlaceholderProprio()
        {
            // É o ÚNICO ponto do modo e-mail em que ela aparece. O rodapé de contexto segue
            // fora: ele descreve a conversa inteira, e aqui a atenção é de um e-mail só.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                Achar<Grid>(janela, "BarraDeInput").Visibility.Should().Be(Visibility.Visible);
                Achar<Grid>(janela, "RodapeDeContexto").Visibility.Should().Be(Visibility.Collapsed);
                Achar<TextBlock>(janela, "InputPlaceholder").Text
                    .Should().Contain("sobre este e-mail");

                janela.Close();
            });
        }

        [Fact]
        public void NaLEITURA_AListaSAI_MasOCabecalhoFICA()
        {
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                Achar<Grid>(janela, "CaixaDeEntrada").Visibility.Should().Be(Visibility.Collapsed);
                Achar<Grid>(janela, "CabecalhoDoEmail").Visibility.Should().Be(Visibility.Visible);
                Achar<ScrollViewer>(janela, "ChatScrollViewer").Visibility.Should().Be(Visibility.Visible);

                janela.Close();
            });
        }

        [Fact]
        public void OCaminhoDeVolta_DEVOLVE_AListaCompleta()
        {
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                Achar<Button>(janela, "BotaoVoltarParaCaixa")
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                Achar<Grid>(janela, "CaminhoDaLeitura").Visibility.Should().Be(Visibility.Collapsed);
                Achar<TextBlock>(janela, "TituloDaCaixa").Visibility.Should().Be(Visibility.Visible);
                Achar<Grid>(janela, "CaixaDeEntrada").Visibility.Should().Be(Visibility.Visible);
                Achar<Grid>(janela, "BarraDeInput").Visibility.Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

        [Fact]
        public void VoltarAoCHAT_ENCERRA_ALeitura()
        {
            // Sem isto o caminho fica armado: o próximo clique em "E-mail" cairia numa leitura
            // que o usuário já tinha abandonado.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                Achar<RadioButton>(janela, "ModoChat").IsChecked = true;
                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;

                Achar<Grid>(janela, "CaminhoDaLeitura").Visibility.Should().Be(Visibility.Collapsed);
                Achar<Grid>(janela, "CaixaDeEntrada").Visibility.Should().Be(Visibility.Visible);

                janela.Close();
            });
        }

        [Fact]
        public void ORotuloDoBotaoSecundario_SEGUE_OProvedorDaCAIXA()
        {
            // Da caixa, e não de quem mandou: prometer "Abrir no Gmail" numa conta Outlook
            // porque a mensagem veio do Gmail seria mentir sobre para onde o clique leva.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email() with { Account = "eu@outlook.com" });

                Achar<Button>(janela, "BotaoAbrirNoClienteDaLeitura").Content
                    .Should().Be("Abrir no Outlook");

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // O que desce para o modelo
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void OEnquadramento_LEVA_OVeredito()
        {
            string texto = ChatWindow.EnquadramentoDoEmail(Email());

            texto.Should().Contain("Jurídico — contrato Vertex");
            texto.Should().Contain("juridico@vertex.com.br");
            texto.Should().Contain("Máxima");
            texto.Should().Contain("Pedem sua assinatura");
        }

        [Fact]
        public void OEnquadramento_DIZ_QueNaoTemOCorpo()
        {
            // Regra 3: o corpo vive em memória durante uma triagem e morre lá. Sem esta frase o
            // modelo responde como se tivesse lido a mensagem inteira e inventa cláusula,
            // anexo e prazo que ninguém escreveu.
            ChatWindow.EnquadramentoDoEmail(Email())
                .Should().Contain("Não tenho o corpo da mensagem aqui");
        }

        // ─────────────────────────────────────────────────────────────────
        // A linha do remetente no corpo do acordeão
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ORemetente_SAI_ComQuemMandouEQuando()
        {
            string linha = MailListItem.Remetente(Email(), new DateTime(2026, 9, 10, 18, 0, 0));

            linha.Should().StartWith("juridico@vertex.com.br · ");
            linha.Should().Contain("10/09/2026");
        }

        [Fact]
        public void SemRemetenteConhecido_ALinhaNaoGanhaUmSeparadorSOLTO()
        {
            // Um "·" sozinho anunciaria um campo que não veio.
            string linha = MailListItem.Remetente(
                Email() with { De = "" }, new DateTime(2026, 9, 10, 18, 0, 0));

            linha.Should().NotStartWith(" · ");
            linha.Should().NotContain(" ·  ");
        }

        // ─────────────────────────────────────────────────────────────────
        // O item — o que ficou igual
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void NoOrbe_OItemNasce_Raio12_SemHover_ESemMetadados()
        {
            // O padrão do controle é o do ORBE: lá o balão tem 252px e três itens, e uma quarta
            // linha de texto por item comeria a pilha inteira.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var item = new MailListItem();

                item.CornerRadius.Should().Be(new CornerRadius(12));
                item.RealceLilas.Should().BeFalse();
                item.EscalaDeJanela.Should().BeFalse();
                item.MostrarMetadados.Should().BeFalse();
                item.Aberto.Should().BeFalse();
            });
        }

        [Fact]
        public void UrlVAZIA_NaoCHAMA_OShell()
        {
            // URL vazia não pode virar Process.Start(""), que levanta exceção. O item vive em
            // três telas: falhar aqui derrubaria o orbe.
            MailListItem.AbrirNoNavegador("").Should().BeFalse();
            MailListItem.AbrirNoNavegador(null).Should().BeFalse();
            MailListItem.AbrirNoNavegador("   ").Should().BeFalse();
        }

        // ─────────────────────────────────────────────────────────────────
        // O painel perdeu a aba
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void OPainel_NaoTEM_MaisAAbaDeEmails()
        {
            // Duas portas para a mesma caixa acabariam divergindo, e a de 252px era a pior das
            // duas — nela não cabem nem o acordeão nem a leitura com a IA.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = new SidePanelWindow();

                painel.FindName("AbaEmails").Should().BeNull();
                painel.FindName("PaneEmails").Should().BeNull();
                painel.FindName("ListaEmails").Should().BeNull();

                painel.Close();
            });
        }

        [Fact]
        public void OsTRES_Rodapes_NaoQuebramLinha()
        {
            // A16 — a base dos rodapés tem de coincidir, senão o conteúdo pula ao trocar de aba
            // (A8).
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var painel = new SidePanelWindow();

                foreach (var nome in new[] { "HistoricoRodape", "ArquivosRodape", "AcoesRodape" })
                {
                    var rodape = Achar<TextBlock>(painel, nome);
                    rodape.TextWrapping.Should().Be(TextWrapping.NoWrap, nome + " não pode quebrar");
                    rodape.MinHeight.Should().Be(25, nome + " precisa da altura comum");
                }

                painel.Close();
            });
        }
    }
}
