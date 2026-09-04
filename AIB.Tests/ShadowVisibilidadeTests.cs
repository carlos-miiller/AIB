using System;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Quando o Shadow está na tela.
    /// <para>
    /// Ele some enquanto a conversa está aberta. Os dois disputam a mesma atenção e o mesmo
    /// canto da tela, e o Shadow existe justamente para as horas em que a conversa NÃO está
    /// aberta — é a porta de entrada, não um segundo lugar para falar com a mesma IA.
    /// </para>
    /// </summary>
    public class ShadowVisibilidadeTests
    {
        [Theory]
        [InlineData(true,  false, true,  "ligado e sem conversa na tela: aparece")]
        [InlineData(true,  true,  false, "a conversa aberta manda o Shadow sair")]
        [InlineData(false, false, false, "desligado não aparece nem com a tela livre")]
        [InlineData(false, true,  false, "desligado e com conversa: continua fora")]
        public void ARegraDeAparecer(bool ligado, bool conversaNaTela, bool esperado, string porque)
        {
            // A regra é função pura porque a decisão é tomada de três lugares — o atalho
            // global, a chave das configurações e o visto da bandeja. Três cópias da mesma
            // condição divergiriam na primeira correção feita num deles.
            ShadowAssistantWindow.DeveAparecer(ligado, conversaNaTela)
                .Should().Be(esperado, porque);
        }

        [Fact]
        public void EsconderOShadow_NaoAPAGA_APilhaDeFalas()
        {
            // Sumir enquanto a conversa está aberta não pode ser fechar: fechado, ele voltaria
            // zerado toda vez, sem a pilha de falas e na posição padrão em vez da que o usuário
            // escolheu.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new ShadowAssistantWindow();

                janela.TerminarDeProcessarEmail("li tudo", Array.Empty<MailSummary>());
                janela.AbrirBarra();
                janela.Falas.Should().NotBeEmpty();

                janela.Hide();
                janela.Falas.Should().NotBeEmpty("esconder não é fechar");

                janela.Close();
            });
        }
    }
}
