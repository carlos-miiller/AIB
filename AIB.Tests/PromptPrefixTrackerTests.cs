using System.Collections.Generic;
using AIB.Services;
using AIB.Services.Agent;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Previsão de tokens reaproveitados pelo KV cache do provider.
    /// Existe porque o Ollama não reporta cache: 'prompt_eval_count' devolve o tamanho do
    /// prompt e não se move (medido: 3485 tanto num prefill frio de 204s quanto num de 343ms).
    /// </summary>
    public class PromptPrefixTrackerTests
    {
        private static PromptPrefixTracker New() => new PromptPrefixTracker(new TokenCounter());

        private static List<ChatMessage> Conversa(params string[] textos)
        {
            var list = new List<ChatMessage> { ChatMessage.CreateSystemMessage("alma do personagem") };
            for (int i = 0; i < textos.Length; i++)
            {
                if (i % 2 == 0) list.Add(ChatMessage.CreateUserMessage(textos[i]));
                else list.Add(ChatMessage.CreateAssistantMessage(textos[i]));
            }
            return list;
        }

        [Fact]
        public void PrimeiroPrompt_NaoTemNadaParaReaproveitar()
        {
            var tracker = New();

            tracker.RecordAndGetReusableTokens(Conversa("oi")).Should().Be(0,
                "não existe requisição anterior com que compartilhar prefixo");
        }

        [Fact]
        public void HistoricoQueSoCresce_ReaproveitaTudoMenosONovo()
        {
            var tracker = New();
            var turno1 = Conversa("oi");
            tracker.RecordAndGetReusableTokens(turno1);

            var turno2 = Conversa("oi", "olá", "qual seu nome?");
            int reaproveitado = tracker.RecordAndGetReusableTokens(turno2);

            var counter = new TokenCounter();
            reaproveitado.Should().Be(counter.CountMessages(turno1),
                "o turno anterior inteiro é prefixo do atual");
            reaproveitado.Should().BeLessThan(counter.CountMessages(turno2),
                "as mensagens novas não estão no cache");
        }

        [Fact]
        public void PrefixoQuebrado_NoMeio_SoReaproveitaAteOPontoDeDivergencia()
        {
            var tracker = New();
            tracker.RecordAndGetReusableTokens(Conversa("oi", "olá", "tudo bem?"));

            // Mesma primeira mensagem, segunda diferente: o cache morre a partir dali.
            var divergente = Conversa("oi", "RESPOSTA DIFERENTE", "tudo bem?");
            int reaproveitado = tracker.RecordAndGetReusableTokens(divergente);

            var counter = new TokenCounter();
            int ateADivergencia = counter.CountMessages(new[] { divergente[0], divergente[1] });
            reaproveitado.Should().Be(ateADivergencia,
                "o prefixo comum termina na mensagem que mudou");
        }

        [Fact]
        public void PodaNaFrente_InvalidaOPrefixoInteiro()
        {
            var tracker = New();
            tracker.RecordAndGetReusableTokens(Conversa("oi", "olá", "e aí?"));

            // O Trim removeu a mensagem logo após o system prompt: o índice 1 já não bate.
            var podado = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage("alma do personagem"),
                ChatMessage.CreateUserMessage("e aí?")
            };
            int reaproveitado = tracker.RecordAndGetReusableTokens(podado);

            reaproveitado.Should().Be(new TokenCounter().CountMessages(new[] { podado[0] }),
                "só o system prompt sobrevive à poda; do índice 1 em diante o cache é perdido");
        }

        [Fact]
        public void PromptIdentico_ReaproveitaOTotal()
        {
            var tracker = New();
            var conversa = Conversa("oi", "olá", "de novo");
            tracker.RecordAndGetReusableTokens(conversa);

            tracker.RecordAndGetReusableTokens(conversa)
                .Should().Be(new TokenCounter().CountMessages(conversa));
        }

        // ── Confirmação contra o custo real de prefill ───────────────────────────

        [Fact]
        public void SemDadosDeDuracao_ConfiaNaPrevisao()
        {
            New().ConfirmOrDiscard(500, null, null).Should().Be(500);
        }

        [Fact]
        public void PrefillBarato_ConfirmaAPrevisao()
        {
            var tracker = New();

            // Primeira medição vira a referência de frio: 3485 tokens em 204.062ms.
            tracker.ConfirmOrDiscard(0, 3485, 204062.3);

            // Mesma quantidade de tokens em 343ms — 595× mais barato, cache quente.
            tracker.ConfirmOrDiscard(3485, 3485, 342.7).Should().Be(3485);
        }

        [Fact]
        public void PrefillCaro_DescartaAPrevisao()
        {
            var tracker = New();
            tracker.ConfirmOrDiscard(0, 3485, 204062.3);

            // Voltou ao custo frio: o provider descartou o cache (recarregou o modelo, num_ctx
            // diferente, outro cliente na instância). Não podemos prometer economia que não houve.
            tracker.ConfirmOrDiscard(3485, 3485, 198000.0).Should().Be(0);
        }

        [Fact]
        public void PrevisaoZerada_ContinuaZero()
        {
            New().ConfirmOrDiscard(0, 3485, 342.7).Should().Be(0);
        }
    }
}
