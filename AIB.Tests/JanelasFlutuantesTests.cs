using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Services.Mail;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// As janelas que aparecem fora da janela principal, redesenhadas numa casca só
    /// (pedido do usuário, 16/10/2026, mockup "Janelas flutuantes do AIB").
    /// </summary>
    public class JanelasFlutuantesTests
    {
        // ─────────────────────────────────────────────────────────────────
        // Confirmar ação — a cor pelo risco
        // ─────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(Ferramentas.Shell, "Get-ChildItem C:\\Users", false, ConfirmCardView.Risco.Cuidado)]
        [InlineData(Ferramentas.Shell, "Remove-Item -Recurse -Force C:\\x", true, ConfirmCardView.Risco.Destrutivo)]
        [InlineData(Ferramentas.Gravar, "CRIAR C:\\novo.md", false, ConfirmCardView.Risco.Cuidado)]
        [InlineData(Ferramentas.Gravar, "SOBRESCREVER C:\\velho.md", false, ConfirmCardView.Risco.Destrutivo)]
        [InlineData(Ferramentas.Editar, "C:\\App.xaml.cs", false, ConfirmCardView.Risco.Destrutivo)]
        [InlineData(Ferramentas.Arquivos, "APAGAR C:\\setup.exe", false, ConfirmCardView.Risco.Cuidado)]
        [InlineData(Ferramentas.Lembrar, "o usuário prefere PT-BR", false, ConfirmCardView.Risco.Leitura)]
        public void AConfirmacaoFlutuante_PintaPeloRisco(string ferramenta, string alvo, bool bloqueado,
                                                         ConfirmCardView.Risco esperado)
        {
            // Pedido do usuário (16/10/2026): "Ação destrutiva" em vermelho até para um
            // Get-ChildItem ensina a não ler o alarme. Vermelho fica para o que não tem volta.
            ConfirmCardView.RiscoDe(ferramenta, alvo, bloqueado).Risco.Should().Be(esperado);
        }

        // ─────────────────────────────────────────────────────────────────
        // Avisos — o e-mail saiu do orbe
        // ─────────────────────────────────────────────────────────────────

        private static readonly Avisos.Acoes SemAcao = new(
            "Ellen", _ => { }, _ => { }, _ => { }, () => { }, _ => { });

        private static MailSummary Email(string assunto, MailUrgency u, bool iniciada = false, string resumo = "responder") =>
            new(assunto, resumo, u, Account: "eu@x.com", ThreadId: assunto, De: "Ana <ana@x.com>", Iniciada: iniciada);

        private static DigestoDeEmail Digesto(params MailSummary[] itens) =>
            new(itens, 171, System.Array.Empty<Descartada>(), System.Array.Empty<Rajada>(), System.DateTime.UtcNow);

        [Fact]
        public void ATriagem_ViraAvisoLilas_QueFechaSozinho()
        {
            // Pedido do usuário (16/10/2026): "vamos remover todo o input de e-mail dentro da
            // orbe" — o digest entrava como fala no orbe, com a pilha de e-mails.
            var (receita, vez) = Avisos.DeDigesto(Digesto(
                Email("Chamado #4821", MailUrgency.Maxima), Email("Contrato", MailUrgency.Media)), SemAcao)!.Value;

            vez.Should().Be(VezDaJanela.Triagem);
            receita.Tom.Should().Be(AvisoWindow.Tom.Lilas);
            receita.Titulo.Should().Be("2 e-mails pedem você, 1 urgente");
            receita.Emails.Should().HaveCount(2);
            receita.FechaSozinho.Should().Be(Avisos.PrazoDaTriagem);
        }

        [Fact]
        public void ARespostaAUmaConversaSua_ViraAvisoAzul_QueNaoFechaSozinho()
        {
            var (receita, vez) = Avisos.DeDigesto(Digesto(
                Email("Re: Proposta", MailUrgency.Media, iniciada: true, resumo: MailDigestService.RespostaPendente)), SemAcao)!.Value;

            vez.Should().Be(VezDaJanela.RespostaSua);
            receita.Pill.Should().Be("Você iniciou");
            receita.FechaSozinho.Should().BeNull("é a resposta que você estava esperando");
            receita.Botoes![0].Texto.Should().Be("Abrir com Ellen");
        }

        [Fact]
        public void DigestoSemNadaADizer_NaoViraAviso() =>
            Avisos.DeDigesto(Digesto(), SemAcao).Should().BeNull();

        [Fact]
        public void OLembreteDeEmail_TemAbrirOEmail_EOAtrasado_NaoTemAdiar()
        {
            var agora = new System.DateTime(2026, 10, 19, 12, 0, 0, System.DateTimeKind.Utc);
            var deEmail = new Lembrete("c37f", agora, "Checar a VipPhone", "VipPhone", "eu@x.com|thr:1879");

            var (naHora, vez) = Avisos.DeLembrete(deEmail, agora, SemAcao);
            vez.Should().Be(VezDaJanela.Lembrete);
            naHora.Botoes!.Select(b => b.Texto).Should().Equal("Abrir o e-mail", "Adiar 1 h", "Feito");

            var (atrasado, _) = Avisos.DeLembrete(deEmail, agora.AddHours(3), SemAcao);
            atrasado.Pill.Should().StartWith("Era para");
            atrasado.Botoes!.Select(b => b.Texto).Should().Equal("Abrir o e-mail", "Feito");

            var (doShadow, _) = Avisos.DeLembrete(deEmail with { ChaveDoEmail = "", Shadow = true }, agora, SemAcao);
            doShadow.Shadow.Should().BeTrue();
            doShadow.Botoes![0].Texto.Should().Be("Abrir no Shadow");
        }

        [Fact]
        public void ARespostaPronta_SaiDoBalaoDaBandeja_ECOmErroNaoFechaSozinha()
        {
            // Era o balão do Windows, cortado em 200 caracteres e sem botão.
            var (ok, vez) = Avisos.DePronta("Encontrei os 10 PDFs.", null, "Ellen", shadow: false, abrir: () => { });
            vez.Should().Be(VezDaJanela.Pronta);
            ok.Pill.Should().Be("Ellen respondeu");
            ok.FechaSozinho.Should().Be(Avisos.PrazoDaResposta);
            ok.Botoes![0].Texto.Should().Be("Abrir conversa");

            var (erro, _) = Avisos.DePronta("", "limite de uso por minuto", "Ellen", shadow: false, abrir: () => { });
            erro.Tom.Should().Be(AvisoWindow.Tom.Perigo);
            erro.FechaSozinho.Should().BeNull("um erro escondido vira uma resposta que você acha que veio");
        }

        [Fact]
        public void SemOrbe_OAvisoFicaAcimaDaBandeja()
        {
            var area = new Rect(0, 0, 1920, 1040);
            var p = PertoDoOrbe.NaBandeja(new Size(440, 300), area, sombraEmbaixo: 30);

            p.X.Should().Be(1480);
            p.Y.Should().Be(770, "a sombra pode sobrar abaixo da área de trabalho");
        }

        [Fact]
        public void AJanelaDeAviso_Monta_SemRoubarOFoco()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var (receita, _) = Avisos.DeDigesto(Digesto(Email("Contrato", MailUrgency.Media)), SemAcao)!.Value;
                var janela = AvisoWindow.Montar(receita);

                janela.ShowActivated.Should().BeFalse("quem está digitando continua digitando");
                janela.ShowInTaskbar.Should().BeFalse();
                ((TextBlock)janela.FindName("TituloTexto")).Text.Should().Be("1 e-mail pede você");
                ((Panel)janela.FindName("Botoes")).Children.Count.Should().Be(2);
                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // Pergunta do AIB
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void APergunta_DizAAcaoNoBotao_ENaoPermitirRecusar()
        {
            // "Permitir/Recusar" são as palavras do portão de ferramentas; numa ação que o
            // próprio usuário pediu ("Ignorar esta conversa?") elas não dizem o que acontece.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var janela = ConfirmDialog.Montar(null, "Ignorar esta conversa?", "Ela sai da caixa.",
                    acao: "Ignorar", temVolta: true, pill: "Volta com mensagem nova");

                ((Button)janela.FindName("PermitirButton")).Content.Should().Be("Ignorar");
                ((Button)janela.FindName("RecusarButton")).Content.Should().Be("Cancelar");
                ((TextBlock)janela.FindName("PillTexto")).Text.Should().Be("Volta com mensagem nova");
                janela.Close();
            });
        }

        [Fact]
        public void OReset_SoAcende_ComAPalavraDigitada()
        {
            // Decisão do usuário (16/10/2026): a ação mais destrutiva do app pede RESTAURAR.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var janela = ConfirmDialog.Montar(null, "Restaurar o AIB?", "Some tudo.",
                    acao: "Apagar tudo", palavra: "RESTAURAR");

                var botao = (Button)janela.FindName("PermitirButton");
                var campo = (TextBox)janela.FindName("PalavraTexto");

                botao.IsEnabled.Should().BeFalse();
                campo.Text = "restaurar";
                botao.IsEnabled.Should().BeFalse("maiúsculas como pedidas");
                campo.Text = "RESTAURAR";
                botao.IsEnabled.Should().BeTrue();
                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────
        // Shadow — o que veio da conversa do orbe
        // ─────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(false, true, false, true, "conversa fora da tela, orbe na tela: perto do orbe")]
        [InlineData(true, true, false, false, "conversa na tela: o card entra nela")]
        [InlineData(true, true, true, true, "pedido do Shadow: perto do orbe mesmo com a janela aberta")]
        [InlineData(false, false, true, false, "sem orbe não há onde ancorar")]
        public void APerguntaDoShadow_VaiSempreParaPertoDoOrbe(bool conversa, bool orbe, bool shadow, bool esperado, string porque)
        {
            // Pedido do usuário (16/10/2026): o que vem do Shadow ganha o selo e fica no Shadow.
            // Antes, com a janela aberta, o card do orbe entrava no meio de outra conversa.
            ChatWindow.DeveUsarJanelaDoOrbe(conversa, orbe, shadow).Should().Be(esperado, porque);
        }

        [Fact]
        public void AConfirmacaoDoShadow_LevaOSelo_EARespostaDoShadow_AbreNoShadow()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var card = new ConfirmCardView();
                card.Preencher(new CommandConfirmationContext { Tool = Ferramentas.Shell, Command = "dir", Level = 1 });
                card.ParaFlutuante(shadow: true);
                ((FrameworkElement)card.FindName("SeloShadow")).Visibility.Should().Be(Visibility.Visible);
            });

            var (receita, _) = Avisos.DePronta("Pronto.", null, "Ellen", shadow: true, abrir: () => { });
            receita.Shadow.Should().BeTrue();
            receita.Botoes![0].Texto.Should().Be("Abrir no Shadow");
        }

        [Fact]
        public void SoAFlutuante_MudaDeCor_OCardDaConversaContinuaComoEra()
        {
            // Decisão do usuário: por enquanto só as flutuantes.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var contexto = new CommandConfirmationContext
                {
                    Tool = Ferramentas.Shell, Command = "Get-ChildItem C:\\Users", Level = 1
                };

                var naConversa = new ConfirmCardView();
                naConversa.Preencher(contexto);
                ((TextBlock)naConversa.FindName("PillTexto")).Text.Should().Be("Ação destrutiva");

                var flutuante = new ConfirmCardView();
                flutuante.Preencher(contexto);
                flutuante.ParaFlutuante();

                flutuante.Pintado.Should().Be(ConfirmCardView.Risco.Cuidado);
                ((TextBlock)flutuante.FindName("PillTexto")).Text.Should().Be("Executa na sua máquina");
            });
        }

        [Fact]
        public void AFlutuante_FicaAcimaDoOrbe_EDesligarOOrbeNaoAFecha()
        {
            // Visto no uso (19/10/2026): o Shadow pediu confirmação, a barra fechou com a perda
            // de foco e, reaberta, ficou POR CIMA da confirmação — as duas são Topmost e vence a
            // última ativada. Com o orbe de dono, a confirmação fica sempre acima dele.
            WpfHost.EmSta(() =>
            {
                Window Fora() => new()
                {
                    Left = -20000, Top = -20000, Width = 50, Height = 50,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
                };

                var orbe = Fora();
                var confirmacao = Fora();
                orbe.Show();

                Flutuantes.Exibir(confirmacao, orbe);
                confirmacao.Owner.Should().BeSameAs(orbe);

                // Desligar o orbe fechava junto a pergunta na tela — e fechar é recusar.
                Flutuantes.Soltar(orbe);
                orbe.Close();
                confirmacao.IsVisible.Should().BeTrue();

                // Sem orbe na tela, não há dono.
                var aviso = Fora();
                Flutuantes.Exibir(aviso, null);
                aviso.Owner.Should().BeNull();

                confirmacao.Close();
                aviso.Close();
            });
        }
    }
}
