using AIB.Services.Ai;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O uso relatado por APIs compatíveis com a OpenAI (OpenAI, OpenRouter). Vai para a conta de
    /// cada fala no raw.jsonl — e ia trocado: cache no lugar dos tokens de saída.
    /// </summary>
    public class OpenAiProviderUsoTests
    {
        [Fact]
        public void EntradaSaidaECache_CadaUmNoSeuCampo()
        {
            var usage = OpenAIChatModelFactory.ChatTokenUsage(
                outputTokenCount: 120,
                inputTokenCount: 3500,
                totalTokenCount: 3620,
                inputTokenDetails: OpenAIChatModelFactory.ChatInputTokenUsageDetails(cachedTokenCount: 3000));

            var uso = OpenAiProvider.Uso(usage)!;

            uso.PromptEvalCount.Should().Be(3500);
            uso.EvalCount.Should().Be(120, "eram os 3000 do cache que caíam aqui");
            uso.CachedTokens.Should().Be(3000);
        }

        [Fact]
        public void SemDetalheDeCache_AindaRelataOsTokens_ECacheFicaDesconhecido()
        {
            var usage = OpenAIChatModelFactory.ChatTokenUsage(
                outputTokenCount: 40, inputTokenCount: 900, totalTokenCount: 940);

            var uso = OpenAiProvider.Uso(usage)!;

            uso.PromptEvalCount.Should().Be(900);
            uso.EvalCount.Should().Be(40);
            uso.CachedTokens.Should().BeNull("sem o detalhe não se sabe; zero diria que nada foi reaproveitado");
        }

        [Fact]
        public void SemUso_NaoRelataNada() => OpenAiProvider.Uso(null).Should().BeNull();
    }
}
