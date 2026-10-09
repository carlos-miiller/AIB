using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Input;
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
    [Collection("Historico")]
    public class SidePanelVisibilityTests
    {
        private static void EmSta(Action acao) => WpfHost.EmSta(acao);

        private static void GarantirRecursos() => WpfHost.GarantirRecursos();

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

        [Fact]
        public void Esc_NoCampoDeTexto_LevaOPainelJunto()
        {
            // Esc chamava Hide() direto, e Hide() é justamente o que não basta: escondia a
            // conversa e deixava o painel na tela sozinho — o mesmo defeito que EsconderTudo
            // existe para não deixar acontecer, e que o atalho e a perda de foco já evitavam.
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

                // A tecla só vira evento com uma janela de verdade atrás: o KeyEventArgs exige
                // a PresentationSource, e ela só existe depois que a janela apareceu.
                chat.Show();
                painel.Show();

                var fonte = PresentationSource.FromVisual(chat);
                fonte.Should().NotBeNull("sem fonte de entrada não há como montar a tecla");

                var tecla = new KeyEventArgs(Keyboard.PrimaryDevice, fonte, 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };

                var handler = typeof(ChatWindow).GetMethod(
                    "InputBox_PreviewKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
                handler.Should().NotBeNull();

                handler!.Invoke(chat, new object[] { chat.FindName("InputBox")!, tecla });

                chat.IsVisible.Should().BeFalse("Esc esconde a conversa");
                painel.IsVisible.Should().BeFalse("e o painel vai junto, como em qualquer outra saída");

                painel.Close();
                chat.Close();
            });
        }

        [Fact]
        public void OPainelQueVoltaComAConversa_MostraOHistoricoDeAgora()
        {
            // Visto no uso: a conversa foi renomeada quando o capítulo fechou, com a janela
            // escondida (o usuário aprovava cartões no navegador). O nome novo apareceu no
            // cabeçalho, e a lista do painel ficou com o antigo até a conversa ser reaberta —
            // o painel escondido não é remontado, e ao voltar era só mostrado como estava.
            EmSta(() =>
            {
                GarantirRecursos();

                var servico = ServicoDescartavel();
                var chat = new ChatWindow(ConversaDescartavel(servico), servico);
                var painel = new SidePanelWindow();

                typeof(ChatWindow).GetField("_painel", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(chat, painel);
                typeof(ChatWindow).GetField("_painelAberto", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(chat, true);

                // Com os dois escondidos, a conversa ganha nome.
                string nome = "Nome dado depois " + Guid.NewGuid().ToString("N")[..8];
                ChatHistoryService.SaveCurrentSession(
                    new System.Collections.Generic.List<OpenAI.Chat.ChatMessage>
                    {
                        OpenAI.Chat.ChatMessage.CreateSystemMessage(""),
                        OpenAI.Chat.ChatMessage.CreateUserMessage("conversa " + nome),
                        OpenAI.Chat.ChatMessage.CreateAssistantMessage("certo")
                    },
                    nome, Guid.NewGuid().ToString());

                Textos(painel).Should().NotContain(t => t.Contains(nome), "o painel foi montado antes");

                chat.ToggleWindow();

                painel.IsVisible.Should().BeTrue();
                Textos(painel).Should().Contain(t => t.Contains(nome));

                painel.Close();
                chat.Close();
            });
        }

        /// <summary>Todo texto mostrado na árvore de uma janela.</summary>
        private static System.Collections.Generic.List<string> Textos(DependencyObject raiz)
        {
            var achados = new System.Collections.Generic.List<string>();
            var fila = new System.Collections.Generic.Queue<object>();
            fila.Enqueue(raiz);

            while (fila.Count > 0)
            {
                object atual = fila.Dequeue();
                if (atual is System.Windows.Controls.TextBlock bloco) achados.Add(bloco.Text ?? "");
                if (atual is System.Windows.Controls.ContentControl { Content: string s }) achados.Add(s);
                if (atual is System.Windows.Controls.ItemsControl itens)
                    foreach (object item in itens.Items) fila.Enqueue(item);
                if (atual is DependencyObject d)
                    foreach (object filho in LogicalTreeHelper.GetChildren(d)) fila.Enqueue(filho);
            }

            return achados;
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
