using System;
using Xunit;
using FluentAssertions;
using AIB.Services;

namespace AIB.Tests
{
    public class LevelServiceTests
    {
        [Theory]
        [InlineData(0, 1)]
        [InlineData(19, 1)]
        [InlineData(20, 2)]
        [InlineData(49, 2)]
        [InlineData(50, 3)]
        [InlineData(1500, 9)]
        [InlineData(2000, 9)]
        public void GetLevel_ShouldReturnCorrectLevel(int xp, int expectedLevel)
        {
            int level = LevelService.GetLevel(xp);
            level.Should().Be(expectedLevel);
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 20)]
        [InlineData(3, 50)]
        [InlineData(9, 1500)]
        [InlineData(0, 0)]
        public void GetXPForCurrentLevel_ShouldReturnCorrectXP(int level, int expectedXp)
        {
            int xp = LevelService.GetXPForCurrentLevel(level);
            xp.Should().Be(expectedXp);
        }

        // Escala geométrica, de 24.000 a 256.000. Pedido do usuário, 27/10/2026: com a escala
        // derivada da janela, o Gemini (132.768) ia de 33.192 a 99.576 em degraus de 8.298 —
        // pouca distância entre os níveis. Janela grande o bastante para nada ser cortado.
        [Theory]
        [InlineData(1,  24000)]
        [InlineData(2,  32264)]
        [InlineData(3,  43373)]
        [InlineData(4,  58307)]
        [InlineData(5,  78384)]
        [InlineData(6, 105373)]
        [InlineData(7, 141655)]
        [InlineData(8, 190430)]
        [InlineData(9, 256000)]
        [InlineData(10, 256000)]
        public void GetMaxTokensForLevel_ShouldReturnCorrectTokens(int level, int expectedTokens)
        {
            LevelService.GetMaxTokensForLevel(level, 1_048_576).Should().Be(expectedTokens);
        }

        [Theory]
        [InlineData(1, 20)]
        [InlineData(2, 50)]
        [InlineData(3, 100)]
        [InlineData(4, 200)]
        [InlineData(5, 350)]
        [InlineData(6, 600)]
        [InlineData(7, 1000)]
        [InlineData(8, 1500)]
        public void GetXPForNextLevel_AbaixoDoTeto_ApontaOLimiarSeguinte(int level, int expectedXp)
        {
            LevelService.GetXPForNextLevel(level).Should().Be(expectedXp);
        }

        [Fact]
        public void AEscala_CRESCE_EmPassosQueSeNotam()
        {
            // Eram +512 por nível: chegar ao topo rendia 4096 tokens, metade de um nível 1.
            // Progressão que o usuário não sente não é progressão, é decoração.
            for (int n = 2; n <= LevelService.NivelMaximo; n++)
                LevelService.Escala(n).Should().BeGreaterThan(LevelService.Escala(n - 1) * 13 / 10,
                    $"o nível {n} tem de valer pelo menos 30% a mais que o {n - 1}");
        }

        [Theory]
        [InlineData(32768)]
        [InlineData(132768)]
        public void ONivel_NUNCA_PassaDeTresQuartosDaJanela(int janela)
        {
            // A geração também consome a janela, e a poda corta em janela - 2048. Sem o limite,
            // o nível 9 pediria 256.000 a um modelo local de 32.768.
            for (int n = 1; n <= LevelService.NivelMaximo; n++)
                LevelService.GetMaxTokensForLevel(n, janela).Should().BeLessThanOrEqualTo(janela * 3 / 4);

            LevelService.GetMaxTokensForLevel(LevelService.NivelMaximo, janela).Should().Be(janela * 3 / 4);
        }

        [Fact]
        public void NaJanelaMaxima_ONivel9_ValeInteiro()
        {
            // A janela máxima era 262.144 e cortava o nível 9 em 196.608: o topo da escala não
            // existia para ninguém.
            LevelService.GetMaxTokensForLevel(LevelService.NivelMaximo, AIB.Services.PerfilDeProvedor.JanelaMaxima)
                .Should().Be(LevelService.Topo);
        }

        [Fact]
        public void NumModeloLocal_ONivel1_CabeInteiro()
        {
            // 24.000 foi escolhido para caber abaixo dos três quartos de 32.768 (24.576).
            LevelService.GetMaxTokensForLevel(1, 32768).Should().Be(24000);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void GetXPForNextLevel_AbaixoDoTeto_EhMaiorQueOLimiarAtual(int level)
        {
            // A UI desenha o progresso como (xp - atual) / (proximo - atual). O denominador
            // precisa ser positivo, senao a barra vira divisao por zero.
            LevelService.GetXPForNextLevel(level)
                .Should().BeGreaterThan(LevelService.GetXPForCurrentLevel(level));
        }

        [Fact]
        public void GetXPForNextLevel_NoTeto_NaoDevolveOLimiarDoProprioNivel()
        {
            int current = LevelService.GetXPForCurrentLevel(9);
            int next = LevelService.GetXPForNextLevel(9);

            next.Should().NotBe(current,
                "no nivel maximo o proximo limiar nao pode ser igual ao atual: (xp-atual)/(proximo-atual) divide por zero");
        }

        [Fact]
        public void GetLevel_EGetXPForCurrentLevel_SaoCoerentes()
        {
            for (int xp = 0; xp <= 2000; xp += 7)
            {
                int level = LevelService.GetLevel(xp);
                LevelService.GetXPForCurrentLevel(level)
                    .Should().BeLessThanOrEqualTo(xp);
            }
        }
    }
}
