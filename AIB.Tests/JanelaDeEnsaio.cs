using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Views;

namespace AIB.Tests
{
    /// <summary>
    /// Monta uma <see cref="ChatWindow"/> descartável, com provider mudo e pastas temporárias.
    /// <para>
    /// A janela exige uma ConversationService de verdade, que por sua vez exige registro de
    /// ferramentas, contador, fábrica de provider e raiz de memória. Montar isso à mão em cada
    /// classe de ensaio era o mesmo bloco de sessenta linhas copiado, e cada cópia era mais um
    /// lugar onde a raiz de memória podia escapar para o ~/.AIB real.
    /// </para>
    /// </summary>
    internal static class JanelaDeEnsaio
    {
        public static ChatWindow Nova() => Nova(out _);

        public static ChatWindow Nova(out ConversationService conversa)
        {
            var servico = Servico();
            conversa = Conversa(servico);
            return new ChatWindow(conversa, servico);
        }

        public static SettingsService Servico()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "aib-ensaio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            return new SettingsService(Path.Combine(tmp, "settings.json"));
        }

        public static ConversationService Conversa(SettingsService servico)
        {
            var registro = new ToolRegistry();
            var contador = new TokenCounter();
            var fabrica = new FabricaMuda();
            var loop = new AgentLoop(registro, fabrica, servico, contador);

            return new ConversationService(
                servico, registro, loop, contador, fabrica,
                Path.Combine(Path.GetTempPath(), "aib-ensaio-mem-" + Guid.NewGuid().ToString("N")));
        }

        private sealed class ProviderMudo : AIB.Services.Ai.IChatProvider
        {
            public string Name => "Ensaio";
            public string Model => "ensaio";

            public async IAsyncEnumerable<AIB.Services.Ai.StreamChunk> StreamAsync(
                IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
                IReadOnlyList<OpenAI.Chat.ChatTool> tools,
                AIB.Services.Ai.ChatRequestOptions options,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await System.Threading.Tasks.Task.CompletedTask;
                yield break;
            }

            public System.Threading.Tasks.Task<AIB.Services.Ai.ChatCompletionResult> CompleteAsync(
                IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
                IReadOnlyList<OpenAI.Chat.ChatTool> tools,
                AIB.Services.Ai.ChatRequestOptions options,
                CancellationToken ct) => throw new NotSupportedException();

            public System.Threading.Tasks.Task WarmupAsync(CancellationToken ct) =>
                System.Threading.Tasks.Task.CompletedTask;
        }

        private sealed class FabricaMuda : AIB.Services.Ai.IChatProviderFactory
        {
            private readonly AIB.Services.Ai.IChatProvider _p = new ProviderMudo();
            public AIB.Services.Ai.IChatProvider GetProvider(UserAppSettings settings) => _p;
        }
    }
}
