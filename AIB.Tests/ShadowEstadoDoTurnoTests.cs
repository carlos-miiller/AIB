using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O orbe como JANELA DO QUE A AIB ESTÁ FAZENDO.
    /// <para>
    /// O estado normal do programa é oculto, na bandeja: o orbe é o painel de estado. Até aqui
    /// ele só sabia do FIM de um turno, e falava a resposta de TODOS eles — inclusive a dos
    /// turnos digitados na janela de conversa, que já estava mostrando aquela mesma frase em
    /// balão. Durante os minutos em que o modelo trabalha, ele ficava parado.
    /// </para>
    /// <para>
    /// O que esta suíte trava: o passo do turno acende o anel sem virar fala, a fala só é
    /// repetida quando não tem outro lugar onde aparecer, e religar o orbe não deixa o anterior
    /// assinado na conversa.
    /// </para>
    /// </summary>
    public class ShadowEstadoDoTurnoTests
    {
        // ─────────────────────────────────────────────────────────────────
        // Quando o orbe repete a fala
        // ─────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(true,  true,  true,  "pedido pela barra: a resposta volta para onde a pergunta foi feita")]
        [InlineData(true,  false, true,  "pedido pela barra, e a conversa nem está na tela")]
        [InlineData(false, false, true,  "digitado na conversa, mas ela saiu da tela: o balão é o único lugar")]
        [InlineData(false, true,  false, "digitado na conversa ABERTA: ela já mostra a resposta em balão")]
        public void ARegraDeFalar(bool doOrbe, bool conversaNaTela, bool esperado, string porque)
        {
            // Função pura pelo mesmo motivo de DeveAparecer: a decisão vale para o fim de todo
            // turno, e uma cópia dela escrita à mão no meio de um handler é a que envelhece
            // errado.
            ShadowAssistantWindow.OrbeDeveFalar(doOrbe, conversaNaTela)
                .Should().Be(esperado, porque);
        }

        [Fact]
        public void ComAConversaFORA_DaTela_OOrbeREPETE_AFala()
        {
            // A janela de ensaio nasce escondida, que é o estado normal do programa.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var conversa = JanelaDeEnsaio.Nova();
                var orbe = new ShadowAssistantWindow();

                App.LigarOrbe(conversa, orbe);

                Disparar(conversa, nameof(ChatWindow.TurnoConcluido), "o ramal é 4275");

                orbe.FalasPendentes.Should().Be(1, "ninguém mais tem essa resposta na tela");
                orbe.Pulsando.Should().BeTrue("e ela espera o clique, sem interromper");

                orbe.Close();
                conversa.Close();
            });
        }

        [Fact]
        public void ComConversaPropria_ARespostaDaJanela_NaoEntraNaPilhaDoOrbe()
        {
            // Bug: "quando se manda algo na tela principal e fecha ela, a resposta ainda é
            // carregada para o shadow". A conversa do orbe é apartada; a resposta da janela
            // caía na pilha dele, no meio de outra conversa.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var conversa = JanelaDeEnsaio.Nova();
                var orbe = new ShadowAssistantWindow();
                var servico = JanelaDeEnsaio.Servico();
                var doOrbe = new ConversaDoOrbe(JanelaDeEnsaio.Conversa(servico), servico);

                App.LigarOrbe(conversa, orbe, doOrbe);

                // A janela de ensaio está escondida: é o caso de quem mandou e fechou.
                Disparar(conversa, nameof(ChatWindow.TurnoConcluido), "o ramal é 4275");

                orbe.FalasPendentes.Should().Be(0);
                orbe.Pulsando.Should().BeFalse();
                orbe.Falas.Should().BeEmpty();

                orbe.Close();
                conversa.Close();
            });
        }

        [Fact]
        public void ComTurnoRodando_OBotaoDaBarra_ViraParar_ENaoEnvia()
        {
            // Visto no uso: no orbe o botão de enviar nunca virava parar. Com a IA em laço, a
            // única forma de pará-la era encerrar o programa.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var orbe = new ShadowAssistantWindow();
                var botao = (Button)orbe.FindName("BotaoEnviar");
                var aviao = botao.Content;
                int paradas = 0, envios = 0;
                orbe.ParadaPedida += () => paradas++;
                orbe.MensagemEnviada += _ => envios++;

                orbe.AbrirBarra();
                ((TextBox)orbe.FindName("Campo")).Text = "outra coisa";

                orbe.MostrarEstado("Pensando");
                orbe.MostrarEstado("Navegando", Ferramentas.Navegador);

                botao.Content.Should().BeOfType<Border>("é o quadrado de parar da janela de chat");
                botao.ToolTip.Should().Be("Parar");

                botao.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                paradas.Should().Be(1);
                envios.Should().Be(0, "com turno rodando o botão para, não envia");

                // O passo vazio é o fim do turno: o aviãozinho volta, o mesmo desenho.
                orbe.MostrarEstado("");

                botao.Content.Should().BeSameAs(aviao);
                botao.ToolTip.Should().Be("Enviar");

                botao.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                envios.Should().Be(1);

                orbe.Close();
            });
        }

        [Fact]
        public void ComAConversaNA_TELA_OOrbeNaoREPETE_AFala()
        {
            // Este era o defeito: a mesma frase em dois lugares ao mesmo tempo, e a segunda
            // cópia escondida atrás da janela que já tinha a primeira.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var conversa = JanelaDeEnsaio.Nova();
                var orbe = new ShadowAssistantWindow();

                App.LigarOrbe(conversa, orbe);
                conversa.Show();

                Disparar(conversa, nameof(ChatWindow.TurnoConcluido), "o ramal é 4275");

                orbe.FalasPendentes.Should().Be(0);
                orbe.Pulsando.Should().BeFalse();
                orbe.BalaoVisivel.Should().BeFalse();

                orbe.Close();
                conversa.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // O passo do turno
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void OPasso_ACENDE_OAnel_SemVirarFala()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var orbe = new ShadowAssistantWindow();
                var casca = (Border)orbe.FindName("Casca")!;

                orbe.MostrarEstado("Lendo arquivo");

                orbe.Trabalhando.Should().BeTrue("o anel é o que diz que ela está viva");
                casca.ToolTip.Should().Be("Lendo arquivo");
                orbe.BalaoVisivel.Should().BeFalse("estado é notícia, não resposta");
                orbe.FalasPendentes.Should().Be(0, "e não vira pulso para ler depois");

                orbe.MostrarEstado("");

                orbe.Trabalhando.Should().BeFalse();
                casca.ToolTip.Should().BeNull();

                orbe.Close();
            });
        }

        [Fact]
        public void OPassoSEGUINTE_TROCA_OTextoESegueGirando()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var orbe = new ShadowAssistantWindow();
                var casca = (Border)orbe.FindName("Casca")!;

                orbe.MostrarEstado("Pensando");
                orbe.MostrarEstado("Executando comando");

                orbe.Trabalhando.Should().BeTrue("o turno não parou entre um passo e o outro");
                casca.ToolTip.Should().Be("Executando comando");

                orbe.Close();
            });
        }

        [Fact]
        public void AFerramenta_MOSTRA_OIconeDela_EPensarVoltaAoGlyph()
        {
            // Antes o orbe só girava o anel: com a janela oculta, não dava para saber se ela
            // lia um arquivo, rodava um comando ou navegava.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var orbe = new ShadowAssistantWindow();
                var glyph = (FrameworkElement)orbe.FindName("Glyph")!;

                orbe.MostrarEstado("Navegando", Ferramentas.Navegador);
                orbe.FerramentaMostrada.Should().Be(Ferramentas.Navegador);
                glyph.Visibility.Should().Be(Visibility.Collapsed);

                orbe.MostrarEstado("Pensando");
                orbe.FerramentaMostrada.Should().BeNull("pensando não é ferramenta");
                glyph.Visibility.Should().Be(Visibility.Visible);
                orbe.Trabalhando.Should().BeTrue();

                orbe.MostrarEstado("Executando comando", Ferramentas.Shell);
                orbe.MostrarEstado("");
                orbe.FerramentaMostrada.Should().BeNull();
                glyph.Visibility.Should().Be(Visibility.Visible);

                orbe.Close();
            });
        }

        [Fact]
        public void AVarreduraDeEmail_TEM_PRIORIDADE_ESeuFim_NaoApagaOTurno()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var orbe = new ShadowAssistantWindow();
                var casca = (Border)orbe.FindName("Casca")!;

                orbe.MostrarEstado("Lendo arquivo", Ferramentas.Ler);
                orbe.ComecarAProcessarEmail();
                orbe.FerramentaMostrada.Should().BeNull("durante a varredura o símbolo é o da caixa de entrada");

                orbe.PararDeProcessarEmail();
                orbe.Trabalhando.Should().BeTrue("o turno continua");
                orbe.FerramentaMostrada.Should().Be(Ferramentas.Ler);
                casca.ToolTip.Should().Be("Lendo arquivo");

                orbe.Close();
            });
        }

        [Fact]
        public void OFimDoTurno_NaoAPAGA_OAnelDaVarreduraDeEmail()
        {
            // O mesmo defeito que separou PararDeProcessarEmail de TerminarDeProcessarEmail: a
            // varredura roda em paralelo com o turno, e o fim de um não pode apagar o sinal do
            // outro — o anel apagado com a caixa ainda sendo lida.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var orbe = new ShadowAssistantWindow();

                orbe.ComecarAProcessarEmail();
                orbe.MostrarEstado("");

                orbe.Trabalhando.Should().BeTrue("a caixa ainda está sendo lida");
                orbe.ProcessandoEmail.Should().BeTrue();

                orbe.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // A ligação com a conversa
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void RELIGAR_OOrbe_NaoDUPLICA_AAssinaturaDaConversa()
        {
            // A conversa VIVE MAIS que o orbe: ela nasce no arranque e morre com o programa,
            // enquanto o orbe é fechado e recriado a cada vez que a chave é desligada e
            // religada. Assinar sem guardar como soltar deixava o orbe FECHADO recebendo o fim
            // de cada turno e mexendo numa janela que já não existia.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var conversa = JanelaDeEnsaio.Nova();

                var primeiro = new ShadowAssistantWindow();
                var soltar = App.LigarOrbe(conversa, primeiro);

                soltar();
                primeiro.Close();

                var segundo = new ShadowAssistantWindow();
                App.LigarOrbe(conversa, segundo);

                Assinantes(conversa, nameof(ChatWindow.TurnoConcluido))
                    .Should().Be(1, "um orbe de pé, uma assinatura");
                Assinantes(conversa, nameof(ChatWindow.PassoDoTurnoMudou))
                    .Should().Be(1, "e o mesmo vale para o passo do turno");

                segundo.Close();
                conversa.Close();
            });
        }

        [Fact]
        public void SOLTAR_ALigacao_CALA_OOrbeAntigo()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var conversa = JanelaDeEnsaio.Nova();
                var orbe = new ShadowAssistantWindow();

                var soltar = App.LigarOrbe(conversa, orbe);
                soltar();

                // Sem assinante, não há o que disparar — e é exatamente esse o ponto: o orbe
                // solto não ouve mais o fim do turno.
                Assinantes(conversa, nameof(ChatWindow.TurnoConcluido)).Should().Be(0);
                Assinantes(conversa, nameof(ChatWindow.PassoDoTurnoMudou)).Should().Be(0);
                orbe.FalasPendentes.Should().Be(0, "este orbe foi desligado do turno");

                orbe.Close();
                conversa.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // O clique no e-mail da pilha
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void OItemDaPILHA_AVISA_QualEmailFoiEscolhido()
        {
            // O item já existia na fala do orbe e o clique nele não fazia NADA: a lista era
            // desenho. No orbe o corpo do acordeão fica recolhido, então este é o único gesto
            // que o item oferece.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var orbe = new ShadowAssistantWindow();

                var email = new MailSummary(
                    "Contrato de manutenção", "Pede assinatura até sexta.", MailUrgency.Maxima);

                orbe.TerminarDeProcessarEmail("Li 3 e-mails.", new[] { email });
                orbe.AbrirBarra();

                var lista = (ItemsControl)orbe.ElementoDaFala(0, "ListaDeEmails")!;
                var item = (MailListItem)lista.ItemTemplate.LoadContent();
                item.DataContext = email;

                MailSummary? escolhido = null;
                orbe.EmailEscolhido += e => escolhido = e;

                Clicar(item);

                escolhido.Should().BeSameAs(email, "o clique tem de levar ESTE e-mail à conversa");

                orbe.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Dispara um evento de campo da conversa sem rodar um turno de verdade — não há modelo
        /// nos ensaios, e o que se confere aqui é a LIGAÇÃO, não o laço de stream.
        /// </summary>
        private static void Disparar(ChatWindow conversa, string evento, string texto)
        {
            var delegado = Campo(conversa, evento);
            delegado.Should().NotBeNull($"{evento} precisa ter alguém escutando");
            ((Action<string>)delegado!).Invoke(texto);
        }

        private static int Assinantes(ChatWindow conversa, string evento) =>
            Campo(conversa, evento)?.GetInvocationList().Length ?? 0;

        private static Delegate? Campo(ChatWindow conversa, string evento)
        {
            var campo = typeof(ChatWindow).GetField(
                evento, BindingFlags.Instance | BindingFlags.NonPublic);

            campo.Should().NotBeNull($"{evento} é um evento de campo da ChatWindow");
            return (Delegate?)campo!.GetValue(conversa);
        }

        /// <summary>
        /// O clique do item, pelo evento que a lista escuta. O gesto de mouse de verdade
        /// depende de o item estar numa árvore visual medida, e o que interessa aqui é o que a
        /// pilha faz com o pedido.
        /// </summary>
        private static void Clicar(MailListItem item)
        {
            var campo = typeof(MailListItem).GetField(
                "PediuAlternar", BindingFlags.Instance | BindingFlags.NonPublic);

            var pedido = (EventHandler?)campo!.GetValue(item);
            pedido.Should().NotBeNull("sem ninguém escutando, o item volta a ser desenho");

            pedido!.Invoke(item, EventArgs.Empty);
        }
    }
}
