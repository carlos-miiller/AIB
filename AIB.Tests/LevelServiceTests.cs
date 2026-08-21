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

        // Escala amarrada ao num_ctx de 16384, reservando 4096 para a geração. A anterior ia a
        // 53248 — 3,25× a janela real do modelo, então o Ollama truncava o prompt pela frente.
        [Theory]
        [InlineData(1, 8192)]
        [InlineData(2, 8704)]
        [InlineData(3, 9216)]
        [InlineData(4, 9728)]
        [InlineData(5, 10240)]
        [InlineData(6, 10752)]
        [InlineData(7, 11264)]
        [InlineData(8, 11776)]
        [InlineData(9, 12288)]
        [InlineData(10, 12288)]
        public void GetMaxTokensForLevel_ShouldReturnCorrectTokens(int level, int expectedTokens)
        {
            int tokens = LevelService.GetMaxTokensForLevel(level);
            tokens.Should().Be(expectedTokens);
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
