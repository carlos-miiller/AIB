using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O painel lateral é uma janela separada, mas não uma janela independente.
    /// <para>
    /// Separada por causa de A2: embutido, ele dividia a largura com a conversa e a fazia
    /// encolher ao abrir. Independente, porém, ele se descolava — dava para arrastar a conversa
    /// para um canto e deixar o painel no outro, e nada o trazia de volta.
    /// </para>
    /// </summary>
    public class PanelAnchoringTests
    {
        private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

        [Fact]
        public void MoverAConversa_LevaOPainelJunto()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var servico = ServicoDescartavel();
                var chat = new ChatWindow(ConversaDescartavel(servico), servico);
                chat.Show();

                // Owner só aceita janela já exibida.
                var painel = new SidePanelWindow { Owner = chat };
                typeof(ChatWindow).GetField("_painel", Privados)!.SetValue(chat, painel);
                painel.Show();

                chat.Left = 300;
                chat.Top = 200;

                painel.Left.Should().Be(chat.Left + chat.Width - 10);
                painel.Top.Should().Be(chat.Top + chat.Height - painel.Height);

                // E de novo, para garantir que não foi só o posicionamento da abertura.
                chat.Left = 640;
                painel.Left.Should().Be(chat.Left + chat.Width - 10);

                painel.Close();
                chat.Close();
            });
        }

        [Fact]
        public void AConversa_FlutuaVinteEDoisPixelsAcimaDaBarraDeTarefas()
        {
            // Era 45, e o vão embaixo da janela chamava mais atenção que a conversa.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var servico = ServicoDescartavel();
                var chat = new ChatWindow(ConversaDescartavel(servico), servico);
                chat.Show();

                typeof(ChatWindow).GetMethod("RepositionWindow", Privados)!.Invoke(chat, null);

                double folga = SystemParameters.WorkArea.Bottom - (chat.Top + chat.ActualHeight);
                folga.Should().Be(22);

                chat.Close();
            });
        }

        // ── Andaimes ────────────────────────────────────────────────────────

        private static SettingsService ServicoDescartavel()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "aib-ancora-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            return new SettingsService(Path.Combine(tmp, "settings.json"));
        }

        private sealed class ProviderMudo : AIB.Services.Ai.IChatProvider
        {
            public string Name => "Ensaio";
            public string Model => "ensaio";

            public async System.Collections.Generic.IAsyncEnumerable<AIB.Services.Ai.StreamChunk> StreamAsync(
                System.Collections.Generic.IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
                System.Collections.Generic.IReadOnlyList<OpenAI.Chat.ChatTool> tools,
                AIB.Services.Ai.ChatRequestOptions options,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await System.Threading.Tasks.Task.CompletedTask;
                yield break;
            }

            public System.Threading.Tasks.Task<AIB.Services.Ai.ChatCompletionResult> CompleteAsync(
                System.Collections.Generic.IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
                System.Collections.Generic.IReadOnlyList<OpenAI.Chat.ChatTool> tools,
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

        private static ConversationService ConversaDescartavel(SettingsService servico)
        {
            var registro = new ToolRegistry();
            var contador = new TokenCounter();
            var fabrica = new FabricaMuda();
            var loop = new AgentLoop(registro, fabrica, servico, contador);

            return new ConversationService(
                servico, registro, loop, contador, fabrica,
                Path.Combine(Path.GetTempPath(), "aib-ancora-mem-" + Guid.NewGuid().ToString("N")));
        }
    }
}
