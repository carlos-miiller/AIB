using System.Windows;
using System.Windows.Controls;
using AIB.Services;
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
    }
}
