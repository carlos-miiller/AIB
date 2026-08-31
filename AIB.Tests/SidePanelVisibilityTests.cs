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
    /// A regra de visibilidade do painel lateral.
    /// <para>
    /// Existe por causa de um defeito real: abrir o painel escondia a janela de conversa e
    /// deixava o painel sozinho na tela, permanente e sem o botão que o abriu para poder
    /// fechá-lo. A conversa some ao perder o foco, e o painel — janela irmã, não-modal — não
    /// passava pelo <see cref="ModalGuard"/>.
    /// </para>
    /// <para>
    /// Foco de verdade não se simula em ensaio sem sessão interativa. O que dá para travar, e é
    /// o que quebrou, são as decisões em volta: o painel abre sem roubar o foco, some junto com
    /// a conversa, e a intenção do usuário sobrevive a esse sumiço.
    /// </para>
    /// </summary>
    public class SidePanelVisibilityTests
    {
        private static void EmSta(Action acao)
        {
            Exception? falha = null;

            var t = new Thread(() =>
            {
                try { acao(); }
                catch (Exception ex) { falha = ex; }
            });

            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

            if (falha != null) throw new Xunit.Sdk.XunitException($"{falha.GetType().Name}: {falha.Message}");
        }

        private static void GarantirRecursos()
        {
            var app = System.Windows.Application.Current ?? new System.Windows.Application();

            if (app.Resources.MergedDictionaries.Count == 0)
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/AIB;component/Themes/Controls.xaml")
                });
            }
        }

        [Fact]
        public void OPainel_AbreSemRoubarOFoco()
        {
            // ShowActivated=False é a primeira metade da correção: sem ele, mostrar o painel
            // tira o foco da conversa e dispara o Deactivated que a esconde.
            EmSta(() =>
            {
                GarantirRecursos();

                var painel = new SidePanelWindow();
                painel.ShowActivated.Should().BeFalse(
                    "abrir o painel não pode tirar o foco da conversa");

                painel.Close();
            });
        }

        [Fact]
        public void FecharPeloXDoPainel_AvisaQuemOAbriu()
        {
            // A conversa precisa distinguir "o painel sumiu junto comigo" de "o usuário fechou
            // o painel" para decidir se ele volta na próxima vez.
            EmSta(() =>
            {
                GarantirRecursos();

                var painel = new SidePanelWindow();
                bool avisou = false;
                painel.FechadoPeloUsuario += () => avisou = true;

                var fechar = typeof(SidePanelWindow).GetMethod(
                    "Fechar_Click", BindingFlags.NonPublic | BindingFlags.Instance);

                fechar.Should().NotBeNull();
                fechar!.Invoke(painel, new object[] { painel, new RoutedEventArgs() });

                avisou.Should().BeTrue();
                painel.IsVisible.Should().BeFalse();

                painel.Close();
            });
        }

        [Fact]
        public void EsconderTudo_LevaOPainelJunto()
        {
            // Esconder a janela DONA não esconde as que ela possui: era assim que o painel
            // ficava na tela sem a conversa.
            EmSta(() =>
            {
                GarantirRecursos();

                var servico = ServicoDescartavel();
                var chat = new ChatWindow(ConversaDescartavel(servico), servico);

                var campoPainel = typeof(ChatWindow).GetField(
                    "_painel", BindingFlags.NonPublic | BindingFlags.Instance);
                campoPainel.Should().NotBeNull();

                var painel = new SidePanelWindow();
                campoPainel!.SetValue(chat, painel);

                var esconder = typeof(ChatWindow).GetMethod(
                    "EsconderTudo", BindingFlags.NonPublic | BindingFlags.Instance);
                esconder.Should().NotBeNull();

                painel.Show();
                painel.IsVisible.Should().BeTrue();

                esconder!.Invoke(chat, null);

                painel.IsVisible.Should().BeFalse("o painel some junto com a conversa");

                painel.Close();
                chat.Close();
            });
        }

        // ── Andaimes ────────────────────────────────────────────────────────

        private static SettingsService ServicoDescartavel()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "aib-painel-" + Guid.NewGuid().ToString("N"));
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
                Path.Combine(Path.GetTempPath(), "aib-painel-mem-" + Guid.NewGuid().ToString("N")));
        }
    }
}
