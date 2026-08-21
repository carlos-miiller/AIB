using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Ai;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    public class ConversationServiceTests : IDisposable
    {
        private readonly string _dir;

        public ConversationServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AIB_ConversationServiceTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Dublês e montagem
        // ─────────────────────────────────────────────────────────────────────

        private sealed class FakeProvider : IChatProvider
        {
            private readonly IReadOnlyList<StreamChunk> _turn;

            public FakeProvider(IReadOnlyList<StreamChunk>? turn = null)
                => _turn = turn ?? new StreamChunk[] { new StreamChunk.Done(StreamFinishReason.Stop, "stop") };

            public string Name => "Fake";
            public string Model => "fake";

            public List<ChatMessage> LastCompleteMessages { get; } = new();
            public int WarmupCalls { get; private set; }

            public async IAsyncEnumerable<StreamChunk> StreamAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                foreach (var c in _turn) yield return c;
            }

            public Task<ChatCompletionResult> CompleteAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                CancellationToken ct)
            {
                LastCompleteMessages.Clear();
                LastCompleteMessages.AddRange(messages);
                return Task.FromResult(new ChatCompletionResult("SISTEMA ONLINE", null, null));
            }

            public Task WarmupAsync(CancellationToken ct)
            {
                WarmupCalls++;
                return Task.CompletedTask;
            }
        }

        private sealed class FixedProviderFactory : IChatProviderFactory
        {
            private readonly IChatProvider _provider;
            public FixedProviderFactory(IChatProvider provider) => _provider = provider;
            public IChatProvider GetProvider(UserAppSettings settings) => _provider;
        }

        private SettingsService BuildSettings(bool sendSystemPrompt)
        {
            var service = new SettingsService(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dat"));
            service.SaveSettings(new UserAppSettings
            {
                AiProvider = "Ollama",
                ModelName = "modelo-de-teste",
                EnableIntelligentTools = true,
                SendSystemPrompt = sendSystemPrompt,
                MessageCount = 0,
                // Sem persona: o default é "Ayano", e o carregador de SOUL.MD leria o arquivo
                // real de ~/.AIB, acoplando estes testes ao disco do usuário. Vazio faz
                // LoadActiveCharacterSoul devolver null e o system prompt ficar só o base.
                ActiveCharacter = ""
            });
            return service;
        }

        private ConversationService BuildConversation(
            SettingsService settings,
            IChatProvider provider,
            out ToolRegistry registry)
        {
            registry = new ToolRegistry();
            var counter = new TokenCounter();
            var factory = new FixedProviderFactory(provider);
            var loop = new AgentLoop(registry, factory, settings, counter);
            return new ConversationService(settings, registry, loop, counter, factory);
        }

        private static string TextOf(ChatMessage message)
        {
            if (message.Content == null) return "";
            return string.Concat(message.Content.Where(p => p?.Text != null).Select(p => p.Text));
        }

        /// <summary>
        /// Quantas mensagens de <paramref name="charsPorMensagem"/> caracteres são precisas para
        /// estourar com folga o orçamento do nível 1. Derivado, e não um 12 fixo: a escala de
        /// tokens por nível já mudou uma vez e deixou estes testes podando zero mensagens em
        /// silêncio — passavam a impressão de cobrir o Trim sem nunca acioná-lo.
        /// </summary>
        private static int MensagensParaEstourarNivel1(int charsPorMensagem)
        {
            int orcamento = LevelService.GetMaxTokensForLevel(1);
            var counter = new TokenCounter();
            int porMensagem = counter.CountText(Filler(charsPorMensagem));
            return (orcamento / porMensagem) + 4;
        }

        private static string Filler(int chars) => new string('a', chars) + " palavra repetida para gastar tokens. ";

        // ─────────────────────────────────────────────────────────────────────
        // Testes
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Snapshot_EhCopiaIsoladaDoHistoricoVivo()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            var before = conversation.SnapshotHistory();
            int countBefore = before.Count;

            conversation.AppendAssistantText("mensagem nova");

            before.Count.Should().Be(countBefore);
            conversation.SnapshotHistory().Count.Should().Be(countBefore + 1);
        }

        [Fact]
        public async Task Aquecimento_NaoMutaOHistoricoVivo()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = new FakeProvider();
            var conversation = BuildConversation(settings, provider, out _);

            conversation.AppendAssistantText("conversa real em andamento");
            int countAntes = conversation.SnapshotHistory().Count;

            await conversation.StartWarmupAsync();

            // O heartbeat existiu — mas só dentro do store descartável.
            provider.WarmupCalls.Should().Be(1);
            provider.LastCompleteMessages.Should().Contain(m => TextOf(m).Contains("[SYSTEM_HEARTBEAT]"));

            // O histórico vivo continua exatamente como estava: nada foi anexado e,
            // principalmente, nada foi apagado por um RemoveRange de limpeza.
            var depois = conversation.SnapshotHistory();
            depois.Count.Should().Be(countAntes);
            depois.Should().NotContain(m => TextOf(m).Contains("[SYSTEM_HEARTBEAT]"));
            TextOf(depois[depois.Count - 1]).Should().Be("conversa real em andamento");
        }

        [Fact]
        public void Trim_NuncaRemoveOSystemPrompt()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.SnapshotHistory()[0].Should().BeOfType<SystemChatMessage>();
            int quantas = MensagensParaEstourarNivel1(2000);
            for (int i = 0; i < quantas; i++) conversation.AppendAssistantText(Filler(2000));

            int antes = conversation.SnapshotHistory().Count;
            conversation.Trim(1); // nível 1 = 3072 tokens

            var depois = conversation.SnapshotHistory();
            depois.Count.Should().BeLessThan(antes);
            depois[0].Should().BeOfType<SystemChatMessage>();
            conversation.CountTokens().Should().BeLessThanOrEqualTo(LevelService.GetMaxTokensForLevel(1));
        }

        [Fact]
        public void Trim_SemSystemPrompt_TambemPodaAPrimeiraMensagem()
        {
            // Sem SendSystemPrompt não existe âncora no índice 0. Travar o índice 0 mesmo
            // assim tornava a primeira mensagem do usuário imortal, e ela nunca saía do
            // contexto por mais longa que fosse a conversa.
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.SnapshotHistory().Should().BeEmpty();
            conversation.AppendRecoveredContext("sessao antiga", Filler(4000));
            int quantas = MensagensParaEstourarNivel1(2000);
            for (int i = 0; i < quantas; i++) conversation.AppendAssistantText(Filler(2000));

            conversation.Trim(1);

            var depois = conversation.SnapshotHistory();
            depois.Should().NotContain(m => TextOf(m).Contains("[CONTEXTO RECUPERADO DO CHAT: sessao antiga]"));
            conversation.CountTokens().Should().BeLessThanOrEqualTo(LevelService.GetMaxTokensForLevel(1));
        }

        [Fact]
        public async Task TetoDeIteracoes_ChegaVisivelAoUsuario()
        {
            // O modelo pede ferramenta para sempre: antes o stream simplesmente acabava e a
            // interface mostrava a conversa como se tivesse dado certo.
            var provider = new FakeProvider(new StreamChunk[]
            {
                new StreamChunk.ToolCallDelta("k0", "id0", "ferramenta_inexistente", "{}"),
                new StreamChunk.Done(StreamFinishReason.ToolCalls, "tool_calls")
            });
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, provider, out _);

            var technical = new List<string>();
            string texto = "";
            await foreach (var item in conversation.StreamResponseAsync("faça algo", t => technical.Add(t)))
                if (item is ChatStreamItem.Text t2) texto += t2.Value;

            texto.Should().Contain($"Limite de {AgentLoop.MaxIterations} etapas atingido");
            technical.Should().Contain(t => t.Contains($"[LOOP] Teto de {AgentLoop.MaxIterations} iterações atingido"));
        }

        [Fact]
        public async Task TurnoNormal_AnexaUsuarioEResposta()
        {
            var provider = new FakeProvider(new StreamChunk[]
            {
                new StreamChunk.TextDelta("Olá!", TextChannel.Final),
                new StreamChunk.Done(StreamFinishReason.Stop, "stop")
            });
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, provider, out _);

            string texto = "";
            await foreach (var item in conversation.StreamResponseAsync("oi"))
                if (item is ChatStreamItem.Text t2) texto += t2.Value;

            texto.Should().Be("Olá!");

            var historico = conversation.SnapshotHistory();
            historico[0].Should().BeOfType<SystemChatMessage>();
            historico[1].Should().BeOfType<UserChatMessage>();
            TextOf(historico[1]).Should().Be("oi");
            historico[2].Should().BeOfType<AssistantChatMessage>();
            TextOf(historico[2]).Should().Be("Olá!");
        }

        [Fact]
        public async Task DoisTurnosSeguidos_NaoDescartamOCtsDoTurnoEmVoo()
        {
            // O CTS nascia FORA do portão de turno: um segundo envio descartava o token que
            // o turno em voo ainda usava, e o Stop caía num CTS já descartado.
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = new FakeProvider(new StreamChunk[]
            {
                new StreamChunk.TextDelta("ok", TextChannel.Final),
                new StreamChunk.Done(StreamFinishReason.Stop, "stop")
            });
            var conversation = BuildConversation(settings, provider, out _);

            async Task<string> TurnAsync(string text)
            {
                string acc = "";
                await foreach (var item in conversation.StreamResponseAsync(text))
                    if (item is ChatStreamItem.Text t2) acc += t2.Value;
                return acc;
            }

            var first = TurnAsync("primeira");
            var second = TurnAsync("segunda");

            (await first).Should().Be("ok");
            (await second).Should().Be("ok");

            // Fora de turno o Stop é inofensivo: não há CTS descartado para cancelar.
            conversation.Invoking(c => c.CancelGeneration()).Should().NotThrow();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Concorrência: o histórico é tocado pela UI, pelo aquecimento e pela persistência
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task EscritasConcorrentes_NaoPerdemNemCorrompemMensagens()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            int baseline = conversation.SnapshotHistory().Count;
            const int writers = 8;
            const int perWriter = 50;

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < perWriter; i++)
                    conversation.AppendAssistantText($"w{w}-{i}");
            })).ToArray();

            await Task.WhenAll(tasks);

            // List<T>.Add sem lock perde itens e às vezes estoura IndexOutOfRange no realloc.
            conversation.SnapshotHistory().Should().HaveCount(baseline + writers * perWriter);
        }

        [Fact]
        public async Task LeituraConcorrenteComEscrita_NuncaLanca_ESempreVeUmEstadoCoerente()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            using var cts = new CancellationTokenSource();
            var exceptions = new List<Exception>();

            var reader = Task.Run(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        // Enumerar a lista viva durante um Add lançaria InvalidOperationException.
                        var snapshot = conversation.SnapshotHistory();
                        foreach (var m in snapshot) _ = TextOf(m);
                        _ = conversation.CurrentTokenCount;
                    }
                }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
            });

            var writer = Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 400; i++) conversation.AppendAssistantText($"m{i}");
                }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
                finally { cts.Cancel(); }
            });

            await Task.WhenAll(reader, writer);

            exceptions.Should().BeEmpty();
        }

        [Fact]
        public async Task PodaConcorrenteComEscrita_NaoCorrompeOHistorico()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            var exceptions = new List<Exception>();

            var trimmer = Task.Run(() =>
            {
                try { for (int i = 0; i < 200; i++) conversation.Trim(1); }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
            });

            var writer = Task.Run(() =>
            {
                try { for (int i = 0; i < 200; i++) conversation.AppendAssistantText(Filler(200)); }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
            });

            await Task.WhenAll(trimmer, writer);

            exceptions.Should().BeEmpty();
            // O system prompt tem trava dura: nenhuma poda concorrente pode evacuá-lo.
            conversation.SnapshotHistory().First().Should().BeOfType<SystemChatMessage>();
        }

        [Fact]
        public async Task AquecimentoConcorrenteComTurno_NaoApagaMensagemDoTurnoEmVoo()
        {
            // Defeito antigo: o finally do aquecimento fazia RemoveRange sobre o histórico vivo
            // e deletava mensagens que uma requisição concorrente tinha acabado de anexar.
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = new FakeProvider(new StreamChunk[]
            {
                new StreamChunk.TextDelta("resposta", TextChannel.Final),
                new StreamChunk.Done(StreamFinishReason.Stop, "stop")
            });
            var conversation = BuildConversation(settings, provider, out _);

            var warmup = conversation.StartWarmupAsync();

            var turn = Task.Run(async () =>
            {
                await foreach (var _ in conversation.StreamResponseAsync("pergunta")) { }
            });

            await Task.WhenAll(warmup, turn);

            var history = conversation.SnapshotHistory();
            history.OfType<UserChatMessage>().Select(TextOf).Should().Contain("pergunta");
            history.Select(TextOf).Should().NotContain(t => t.Contains("[SYSTEM_HEARTBEAT]"),
                "a mensagem fantasma do aquecimento nunca entra no histórico vivo");
        }

        [Fact]
        public void ResetHistory_ConcorrenteComLeitura_NaoLanca()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            var exceptions = new List<Exception>();

            Parallel.For(0, 100, i =>
            {
                try
                {
                    if (i % 10 == 0) conversation.ResetHistory();
                    else _ = conversation.SnapshotHistory().Count;
                }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
            });

            exceptions.Should().BeEmpty();
        }

        [Fact]
        public void AppendRecoveredContext_EntraNoHistoricoSemExporALista()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.AppendRecoveredContext("Título do chat", "conteúdo recuperado");

            var texts = conversation.SnapshotHistory().Select(TextOf).ToList();
            texts.Should().Contain(t => t.Contains("conteúdo recuperado"));
        }

        [Fact]
        public void CurrentTokenCount_AcompanhaOCrescimentoDoHistorico()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            int before = conversation.CurrentTokenCount;
            conversation.AppendAssistantText(Filler(500));

            conversation.CurrentTokenCount.Should().BeGreaterThan(before);
        }

    }
}
