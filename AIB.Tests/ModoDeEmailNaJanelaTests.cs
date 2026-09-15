using System;
using System.Linq;
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
            // É o ÚNICO ponto do modo e-mail em que ela aparece. O rodapé de contexto sai de
            // vista — ele descreve a conversa inteira, e aqui a atenção é de um e-mail só —,
            // mas guardando o lugar: ver NaLEITURA_ORodapeFicaOCULTO_MasNaoCOLAPSADO.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                Achar<Grid>(janela, "BarraDeInput").Visibility.Should().Be(Visibility.Visible);
                Achar<Grid>(janela, "RodapeDeContexto").Visibility.Should().NotBe(Visibility.Visible);
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
        public void NaLEITURA_OTopoDaConversaNaoSOMA_ComOCabecalho()
        {
            // No CHAT os 24px de topo do rolo afastam a primeira bolha da divisória do header.
            // Na LEITURA quem afasta é o cabeçalho, e os dois somados empurravam o cartão quase
            // cinquenta pixels para baixo — numa janela de 520 isso é um décimo da altura.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                double noChat = Achar<ScrollViewer>(janela, "ChatScrollViewer").Padding.Top;

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                Achar<ScrollViewer>(janela, "ChatScrollViewer").Padding.Top
                    .Should().BeLessThan(noChat);

                // E volta ao normal quando se volta ao chat: um rolo que fica apertado para
                // sempre seria a mesma falha com o sinal trocado.
                Achar<RadioButton>(janela, "ModoChat").IsChecked = true;
                Achar<ScrollViewer>(janela, "ChatScrollViewer").Padding.Top.Should().Be(noChat);

                janela.Close();
            });
        }

        [Fact]
        public void NaLEITURA_ORodapeFicaOCULTO_MasNaoCOLAPSADO()
        {
            // A barra de input não tem margem de baixo própria: quem sempre deu o chão dela foi
            // o rodapé de contexto. Colapsá-lo fazia o input encostar na borda arredondada da
            // janela e aparecer cortado. Hidden guarda o lugar.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                Achar<Grid>(janela, "RodapeDeContexto").Visibility
                    .Should().Be(Visibility.Hidden);

                janela.Close();
            });
        }

        [Fact]
        public void NaLISTA_ORodapeCOLAPSA_DeVerdade()
        {
            // Ali não há input embaixo para sustentar, e a lista ganha a linha inteira.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;

                Achar<Grid>(janela, "RodapeDeContexto").Visibility
                    .Should().Be(Visibility.Collapsed);

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
        public void OPlaceholder_USA_ONomeDaPersonalidade_NaoOTituloDaConversa()
        {
            // Já saiu na tela "Fale com Nova conversa sobre este e-mail...": o nome vinha de
            // ChatTitleText, que é o TÍTULO DA CONVERSA. Os dois campos ficam perto e têm nomes
            // parecidos; só um deles é um nome próprio.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                string texto = Achar<TextBlock>(janela, "InputPlaceholder").Text;

                texto.Should().NotContain("Nova conversa");

                // A MESMA fonte do nome no header: se os dois divergirem, um dos dois mente.
                string noHeader = Achar<TextBlock>(janela, "AgentNameText").Text;
                texto.ToUpperInvariant().Should().Contain(noHeader);

                janela.Close();
            });
        }

        // ──────────────────────────────────────────────────────────
        // A faixa de espera — §5.4
        // ──────────────────────────────────────────────────────────

        [Fact]
        public void AFaixaDeEspera_MOSTRA_AnelTempoEBotao()
        {
            // Numa máquina que faz prefill a ~30 tok/s, espera sem sinal nenhum é
            // indistinguível de travamento. O anel diz que está vivo; o botão devolve a
            // decisão a quem está esperando.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                janela.AnunciarCompactacao(
                    new ConversationService.PassoDaCompactacao("capítulo", 3, 1));

                Achar<Border>(janela, "StatusBar").Visibility.Should().Be(Visibility.Visible);
                Achar<System.Windows.Shapes.Ellipse>(janela, "AnelDaFaixa").Visibility
                    .Should().Be(Visibility.Visible);

                var botao = Achar<Button>(janela, "AcaoDaFaixa");
                botao.Visibility.Should().Be(Visibility.Visible);
                botao.Content.Should().Be("Interromper");

                Achar<TextBlock>(janela, "StatusText").Text.Should().Contain("capítulo 3");

                janela.Close();
            });
        }

        [Fact]
        public void AFaixaDeEspera_SOME_QuandoACompactacaoAcaba()
        {
            // Uma faixa de "compactando" que fica na tela depois do fim é a mesma mentira que
            // ela existe para desfazer.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                janela.AnunciarCompactacao(
                    new ConversationService.PassoDaCompactacao("arco", 1, 0));

                janela.EsconderEspera();

                Achar<Border>(janela, "StatusBar").Visibility.Should().Be(Visibility.Collapsed);
                Achar<Button>(janela, "AcaoDaFaixa").Visibility.Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

        [Fact]
        public void NaLEITURA_EXISTE_ComoDescartarAConversa()
        {
            // A conversa de e-mail ficou FORA da lista do painel, e com ela ficou fora do botão
            // direito → Excluir. "Não aparece na lista" não pode virar "não dá para
            // administrar": o único lugar onde ela é alcançável é o próprio e-mail.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;
                janela.EntrarNaLeitura(Email());

                var lixeira = Achar<Button>(janela, "BotaoDescartarConversa");

                lixeira.Should().NotBeNull();
                lixeira.ToolTip.Should().Be("Descartar esta conversa");

                janela.Close();
            });
        }

        [Fact]
        public void NaLISTA_ALixeiraNaoAPARECE()
        {
            // Ela descarta a conversa DESTE e-mail. Na lista não há "este e-mail", e um botão
            // de ação sem alvo é um convite a descobrir o alvo do jeito ruim.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Achar<RadioButton>(janela, "ModoEmail").IsChecked = true;

                // Ela mora dentro de AcoesDaLeitura, que só aparece na leitura.
                Achar<StackPanel>(janela, "AcoesDaLeitura").Visibility
                    .Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

        [Theory]
        [InlineData("/compact")]
        [InlineData("/memoria")]
        [InlineData("/skills")]
        public void ALista_De_Barra_SO_OfereceComandoQueEXISTE(string comando)
        {
            // Ela sugeria /clear, /help e /vault, e nenhum dos três era tratado: escolher um
            // deles mandava o texto ao modelo como pergunta. Uma lista que inventa comandos
            // ensina o atalho errado e só desmente depois do envio.
            //
            // O ensaio confere pelo caminho do usuário — digitar "/" e ver o que aparece — e
            // não pelo campo, que poderia estar certo com a lista na tela vindo de outro lugar.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                var caixa = Achar<System.Windows.Controls.TextBox>(janela, "InputBox");
                caixa.Text = "/";

                var lista = Achar<System.Windows.Controls.ListBox>(janela, "CommandsList");
                var oferecidos = lista.ItemsSource.Cast<string>().ToList();

                oferecidos.Should().Contain(comando);
                oferecidos.Should().NotContain(new[] { "/clear", "/help", "/vault" });

                // Trapaça de desenvolvimento não é recurso: oferecê-la na lista a transformaria
                // em recurso.
                oferecidos.Should().NotContain("/unlock_level");

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
        public void OIgnorar_ESTA_NoItemDaLista_SoOndeHaQuemGuarde()
        {
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var item = new MailListItem { DataContext = Email(), Aberto = true };
                var botao = (Button)item.FindName("BotaoIgnorar");

                botao.Visibility.Should().Be(Visibility.Collapsed,
                    "no orbe não há quem guarde o que foi ignorado, e o botão não faria nada");

                item.PodeIgnorar = true;
                botao.Visibility.Should().Be(Visibility.Visible);

                bool pediu = false;
                item.PediuIgnorar += (_, _) => pediu = true;
                botao.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                pediu.Should().BeTrue();
            });
        }

        [Fact]
        public void ODescarteDaConversa_ESTA_NoItemDaLista_SoQuandoHaConversa()
        {
            // O descarte só existia na leitura (§3.11), e chegar lá é "Abrir com <NOME>" — que
            // manda um turno ao modelo. Para jogar fora uma conversa era preciso continuá-la.
            EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var item = new MailListItem { DataContext = Email(), Aberto = true };
                var botao = (Button)item.FindName("BotaoDescartarConversa");

                botao.Visibility.Should().Be(Visibility.Collapsed, "sem conversa não há o que descartar");

                item.TemConversa = true;
                botao.Visibility.Should().Be(Visibility.Visible);

                bool pediu = false;
                item.PediuDescartarConversa += (_, _) => pediu = true;
                botao.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                pediu.Should().BeTrue();
            });
        }

        [Fact]
        public void OEnquadramento_DIZ_QueNaoTemOCorpo_EOndeEleEsta()
        {
            // Regra 3: o corpo não abre a conversa. Sem esta frase o modelo responde como se
            // tivesse lido a mensagem inteira e inventa cláusula, anexo e prazo que ninguém
            // escreveu. E sem dizer ONDE o texto está, ele responde "não sei" a perguntas que
            // tinham resposta a uma chamada de distância.
            string texto = ChatWindow.EnquadramentoDoEmail(Email());

            texto.Should().Contain("O texto original do e-mail não está aqui");
            texto.Should().Contain(AIB.Services.Ferramentas.LerEmail);
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
