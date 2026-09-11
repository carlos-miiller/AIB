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

        // Escala DERIVADA da janela: um quarto no piso, três quartos no topo. Os valores abaixo
        // são os de num_ctx = 32768 e mudam junto com ele, de propósito.
        [Theory]
        [InlineData(1,  8192)]
        [InlineData(2, 10240)]
        [InlineData(3, 12288)]
        [InlineData(4, 14336)]
        [InlineData(5, 16384)]
        [InlineData(6, 18432)]
        [InlineData(7, 20480)]
        [InlineData(8, 22528)]
        [InlineData(9, 24576)]
        [InlineData(10, 24576)]
        public void GetMaxTokensForLevel_ShouldReturnCorrectTokens(int level, int expectedTokens)
        {
            // Os valores de uma janela de 32768: um quarto no piso, três quartos no topo, oito
            // passos de 2048. O piso segue 8192, que é onde uma alma grande cabe com folga de
            // conversa; o topo dobrou, porque a tabela anterior parou em 12288 quando a janela
            // ainda era 16384 e não acompanhou a mudança.
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

        [Fact]
        public void AEscala_CRESCE_EmPassosQueSeNotam()
        {
            // Eram +512 por nível: chegar ao topo rendia 4096 tokens, metade de um nível 1.
            // Progressão que o usuário não sente não é progressão, é decoração.
            int primeiro = LevelService.GetMaxTokensForLevel(1);
            int ultimo = LevelService.GetMaxTokensForLevel(LevelService.NivelMaximo);

            ultimo.Should().BeGreaterThanOrEqualTo(primeiro * 2,
                "o topo tem de valer pelo menos o dobro do piso");

            for (int n = 2; n <= LevelService.NivelMaximo; n++)
                LevelService.GetMaxTokensForLevel(n)
                    .Should().BeGreaterThan(LevelService.GetMaxTokensForLevel(n - 1),
                        $"o nível {n} tem de valer mais que o {n - 1}");
        }

        [Fact]
        public void OTopoDaEscala_CABE_AbaixoDoTetoDaPoda()
        {
            // A geração também consome a janela. Se o orçamento do nível 9 encostasse no teto da
            // poda, o nível mais alto seria o único em que a poda de emergência dispararia
            // sozinha — o prêmio virava defeito.
            LevelService.GetMaxTokensForLevel(LevelService.NivelMaximo)
                .Should().BeLessThan(ConversationService.TetoDaPoda);
        }

        [Fact]
        public void AEscala_SEGUE_AJanelaDoModelo()
        {
            // O ponto da mudança. A tabela antiga era literal e o comentário dela dizia "teto
            // amarrado ao num_ctx de 16384"; a janela virou 32768 e a tabela ficou. Uma
            // dependência escrita em prosa quebra em silêncio — esta é aritmética.
            int janela = AIB.Services.Ai.ChatRequestOptions.Default.NumCtx;

            LevelService.GetMaxTokensForLevel(1).Should().Be(janela / 4);
            LevelService.GetMaxTokensForLevel(LevelService.NivelMaximo).Should().Be(janela * 3 / 4);
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
