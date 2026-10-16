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
