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

        [Theory]
        [InlineData(1, 3072)]
        [InlineData(2, 5120)]
        [InlineData(3, 7168)]
        [InlineData(4, 12288)]
        [InlineData(5, 17408)]
        [InlineData(6, 22528)]
        [InlineData(7, 32768)]
        [InlineData(8, 43008)]
        [InlineData(9, 53248)]
        [InlineData(10, 53248)]
        public void GetMaxTokensForLevel_ShouldReturnCorrectTokens(int level, int expectedTokens)
        {
            int tokens = LevelService.GetMaxTokensForLevel(level);
            tokens.Should().Be(expectedTokens);
        }
    }
}
