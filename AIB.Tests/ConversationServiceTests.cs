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
    [Collection("ContextoGlobal")]
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

            /// <summary>O que o PRIMEIRO StreamAsync recebeu: mensagens, ferramentas e opções.</summary>
            public (List<ChatMessage> Mensagens, IReadOnlyList<ChatTool> Ferramentas, ChatRequestOptions Opcoes)? PrimeiroStream { get; private set; }
            public int WarmupCalls { get; private set; }

            /// <summary>O resumidor de capitulos passa por CompleteAsync, nao por StreamAsync.</summary>
            public string CompleteReply { get; set; } = "SISTEMA ONLINE";
            public int CompleteCalls { get; private set; }

            /// <summary>Falha do resumidor, para exercitar o descanso apos compactacao perdida.</summary>
            public Exception? CompleteThrows { get; set; }

            public async IAsyncEnumerable<StreamChunk> StreamAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                PrimeiroStream ??= (messages.ToList(), tools, options);
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
                CompleteCalls++;
                if (CompleteThrows != null) throw CompleteThrows;
                return Task.FromResult(new ChatCompletionResult(CompleteReply, null, null));
            }

            public Task WarmupAsync(CancellationToken ct)
            {
                WarmupCalls++;
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Responde na hora — até <see cref="Segurar"/> ligar. Aí avisa que começou e espera ser
        /// liberado, IGNORANDO o cancelamento: é o prefill de minutos que não percebe o botão.
        /// </summary>
        private sealed class ProviderQueSegura : IChatProvider
        {
            public ProviderQueSegura(string resposta) => Resposta = resposta;

            public string Resposta { get; set; }

            public bool Segurar { get; set; }
            public TaskCompletionSource Comecou { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Liberar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string Name => "Fake";
            public string Model => "fake";

            public async IAsyncEnumerable<StreamChunk> StreamAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();

                if (Segurar)
                {
                    Comecou.TrySetResult();
                    await Liberar.Task;
                }

                yield return new StreamChunk.TextDelta(Resposta, TextChannel.Final);
                yield return new StreamChunk.Done(StreamFinishReason.Stop, "stop");
            }

            public Task<ChatCompletionResult> CompleteAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                CancellationToken ct) =>
                Task.FromResult(new ChatCompletionResult("resumo", null, null));

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }

        /// <summary>Cada volta ao modelo devolve o que o roteiro mandar, e pode agir antes.</summary>
        private sealed class ProviderRoteirizado : IChatProvider
        {
            private readonly Func<IReadOnlyList<ChatMessage>, IReadOnlyList<StreamChunk>> _roteiro;

            public ProviderRoteirizado(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<StreamChunk>> roteiro) =>
                _roteiro = roteiro;

            public string Name => "Fake";
            public string Model => "fake";

            public async IAsyncEnumerable<StreamChunk> StreamAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                foreach (var c in _roteiro(messages)) yield return c;
            }

            public Task<ChatCompletionResult> CompleteAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                CancellationToken ct) =>
                Task.FromResult(new ChatCompletionResult("resumo", null, null));

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
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
            // Raiz de memória redirecionada: sem isto cada turno destes testes gravaria um
            // raw.jsonl em ~/.AIB/memory do usuário — a mesma poluição que o log de auditoria
            // já causou uma vez.
            return new ConversationService(
                settings, registry, loop, counter, factory,
                memoryRootOverride: Path.Combine(_dir, "memory"));
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
        private static int MensagensParaEstourarNivel1(int charsPorMensagem) =>
            MensagensParaEstourar(LevelService.GetMaxTokensForLevel(1), charsPorMensagem);

        /// <summary>
        /// Quantas mensagens são precisas para estourar a JANELA do modelo — o único teto em
        /// que a poda ainda age.
        /// <para>
        /// O teto do nível deixou de acioná-la: ele é orçamento de compactação, e podar nele
        /// destruía material que o modelo comportava com folga. Um ensaio de poda que use o
        /// número do nível não poda nada e passa verde sem cobrir coisa alguma — foi o que já
        /// aconteceu uma vez, quando a escala de tokens por nível mudou.
        /// </para>
        /// </summary>
        private static int MensagensParaEstourarAJanela(int charsPorMensagem) =>
            MensagensParaEstourar(ConversationService.TetoDaPoda, charsPorMensagem);

        private static int MensagensParaEstourar(int orcamento, int charsPorMensagem)
        {
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
            int quantas = MensagensParaEstourarAJanela(2000);
            for (int i = 0; i < quantas; i++) conversation.AppendAssistantText(Filler(2000));

            int antes = conversation.SnapshotHistory().Count;
            conversation.Trim(1); // nível 1 = 3072 tokens

            var depois = conversation.SnapshotHistory();
            depois.Count.Should().BeLessThan(antes);
            depois[0].Should().BeOfType<SystemChatMessage>();
            conversation.CountTokens().Should().BeLessThanOrEqualTo(ConversationService.TetoDaPoda);
        }

        [Fact]
        public void Trim_SemSystemPrompt_PodaAMensagemDeUsuarioQueJaSaiuDoTurnoVivo()
        {
            // Sem SendSystemPrompt não existe âncora no índice 0. Travar o índice 0 mesmo
            // assim tornava a primeira mensagem do usuário imortal, e ela nunca saía do
            // contexto por mais longa que fosse a conversa.
            //
            // A proteção que existe hoje é OUTRA e é por posição relativa, não por índice fixo:
            // vale para a mensagem que abriu o turno EM ANDAMENTO. Assim que chega uma mais
            // nova, a antiga volta a ser histórico como qualquer outra mensagem.
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.SnapshotHistory().Should().BeEmpty();
            conversation.AppendRecoveredContext("sessao antiga", Filler(4000));
            int quantas = MensagensParaEstourarAJanela(2000);
            for (int i = 0; i < quantas; i++) conversation.AppendAssistantText(Filler(2000));

            // O turno vivo passa a ser este. O anterior perde a proteção.
            conversation.AppendRecoveredContext("pedido novo", "o que falta?");

            conversation.Trim(1);

            var depois = conversation.SnapshotHistory();
            depois.Should().NotContain(m => TextOf(m).Contains("[CONTEXTO RECUPERADO DO CHAT: sessao antiga]"));
            conversation.CountTokens().Should().BeLessThanOrEqualTo(ConversationService.TetoDaPoda);
        }

        [Fact]
        public async Task UmaPassada_FECHA_QuantosCapitulosForemPrecisos()
        {
            // Sem a poda cortando no teto do nível, a conversa chega ao fim do turno com
            // material de VÁRIOS capítulos acumulado. Fechar um só por passada deixaria o resto
            // para a seguinte, que talvez nunca venha — e enquanto isso o contexto ficaria
            // acima do gatilho a cada turno.
            //
            // O acúmulo é montado direto no histórico, e não por StreamResponseAsync: a
            // compactação roda ao fim de CADA turno, então um laço de turnos consumiria o
            // material aos poucos e o ensaio nunca chegaria a exercitar a passada com backlog.
            var settings = BuildSettings(sendSystemPrompt: false);

            var provider = new FakeProvider { CompleteReply = "Resumo do trecho." };
            var conversation = BuildConversation(settings, provider, out _);

            // Cada chamada acrescenta um par user+assistant: um turno FECHADO, do jeito que o
            // TurnSplitter enxerga. Catorze deles — os 2 mais recentes ficam sempre fora e cada
            // capítulo leva no máximo 8, então sobra material para mais de um.
            for (int i = 0; i < 14; i++)
                conversation.AppendRecoveredContext($"pedido {i}", Filler(20000));

            conversation.Chapters.Should().BeEmpty("nada foi compactado ainda");

            await conversation.CompactIfNeededAsync(userLevel: 1);

            conversation.Chapters.Count.Should().BeGreaterThan(1,
                "uma passada fecha quantos forem precisos, e não um por turno");
        }

        [Fact]
        public void OTetoDoNIVEL_NaoPoda_Mais()
        {
            // O teto do nível é ORÇAMENTO DE COMPACTAÇÃO: diz quando vale a pena resumir, não o
            // que o modelo aguenta. Podar nele destruía material que ainda cabia com folga —
            // medido em 10/09, sete podas cortando em 9.216 numa janela de 16.384, e a
            // compactação nunca teve o que compactar porque a poda comia antes.
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            int quantas = MensagensParaEstourarNivel1(2000);
            for (int i = 0; i < quantas; i++) conversation.AppendAssistantText(Filler(2000));

            int antes = conversation.SnapshotHistory().Count;
            conversation.Trim(1);

            conversation.SnapshotHistory().Count.Should().Be(antes,
                "passou do teto do nível e nada foi descartado: quem resolve isso é a compactação");
            conversation.CountTokens().Should().BeGreaterThan(LevelService.GetMaxTokensForLevel(1));
        }

        [Fact]
        public void APoda_NaoCome_OPedidoDoTurnoEmAndamento()
        {
            // A poda roda a cada rodada de ferramentas. Num turno grande — dez iterações, dois
            // arquivos lidos — o mais antigo que ela encontrava era o PRÓPRIO PEDIDO do
            // usuário, e comê-lo deixava o modelo trabalhando sem saber o que tinha sido
            // pedido.
            //
            // Medido numa conversa real de 10/09: seis turnos na tela, quatro no raw.jsonl. Os
            // dois perdidos foram os dois maiores, e o log mostra o prefixo caindo para 1.059
            // tokens no meio deles — a poda tinha varrido o histórico até o osso.
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.AppendRecoveredContext("pedido do usuario", "replique este arquivo para os outros tres");

            // O miolo do turno: chamadas e resultados grandes, como um arquivo lido.
            int quantas = MensagensParaEstourarAJanela(2000);
            for (int i = 0; i < quantas; i++) conversation.AppendAssistantText(Filler(2000));

            conversation.Trim(1);

            var depois = conversation.SnapshotHistory();
            depois.Should().Contain(m => TextOf(m).Contains("replique este arquivo"),
                "o pedido é a TAREFA, não histórico — podá-lo é apagar o que o turno está fazendo");
            conversation.CountTokens().Should().BeLessThanOrEqualTo(ConversationService.TetoDaPoda,
                "e o miolo do turno continua podável, que é o que de fato ocupa espaço");
        }

        [Fact]
        public void OTurnoSOBREVIVE_APoda_EPodeSerRegistrado()
        {
            // O estrago silencioso vinha depois da poda: o TurnSplitter descarta tudo o que vem
            // antes do primeiro 'user', então um histórico sem a abertura não tem turno nenhum
            // — e o RecordLastTurn não achava o que gravar. O turno inteiro sumia do raw.jsonl
            // sem uma linha de aviso.
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.AppendRecoveredContext("pedido do usuario", "faz a planilha virar html");
            int quantas = MensagensParaEstourarAJanela(2000);
            for (int i = 0; i < quantas; i++) conversation.AppendAssistantText(Filler(2000));

            conversation.Trim(1);

            AIB.Services.Memory.TurnSplitter.Split(conversation.SnapshotHistory())
                .Should().NotBeEmpty("sem a abertura no histórico, o turno não existe para o registro");
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

        // ─────────────────────────────────────────────────────────────────────
        // Registro cru da sessão (memory/sessions/<id>/raw.jsonl)
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TurnoFechado_EhGravadoEmRawJsonl()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = new FakeProvider(new StreamChunk[]
            {
                new StreamChunk.TextDelta("olá", TextChannel.Final),
                new StreamChunk.Done(StreamFinishReason.Stop, "stop")
            });
            var conversation = BuildConversation(settings, provider, out _);

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }

            var linhas = File.ReadAllLines(Path.Combine(conversation.SessionMemoryDir, "raw.jsonl"));

            linhas.Should().ContainSingle();
            linhas[0].Should().Contain("oi").And.Contain("olá");
        }

        [Fact]
        public async Task DoisTurnos_ViramDuasLinhasComIndicesCrescentes()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = new FakeProvider(new StreamChunk[]
            {
                new StreamChunk.TextDelta("ok", TextChannel.Final),
                new StreamChunk.Done(StreamFinishReason.Stop, "stop")
            });
            var conversation = BuildConversation(settings, provider, out _);

            await foreach (var _ in conversation.StreamResponseAsync("primeira")) { }
            await foreach (var _ in conversation.StreamResponseAsync("segunda")) { }

            var linhas = File.ReadAllLines(Path.Combine(conversation.SessionMemoryDir, "raw.jsonl"));

            linhas.Should().HaveCount(2);
            linhas[0].Should().Contain("\"Index\":0");
            // Índice é contador próprio: o Trim reindexaria os turnos a cada poda.
            linhas[1].Should().Contain("\"Index\":1");
        }

        [Fact]
        public async Task TurnoVAZIO_EhGravado_ComAMarcaQueOFecha()
        {
            // Antes ele não era gravado: o histórico terminava sem fala do assistente, o turno
            // não FECHAVA, e ele ficava pendente esperando a mensagem seguinte do usuário. Se o
            // app fechasse antes, sumia — e enquanto estivesse aberto travava a compactação de
            // tudo o que viesse depois, porque SelectTurnsToCompact para no primeiro turno não
            // fechado que encontra.
            //
            // Medido em 10/09: um turno de 55 minutos terminou com resposta vazia e a sessão
            // ficou com UM turno no raw.jsonl, sete podas de emergência e nenhum capítulo.
            var settings = BuildSettings(sendSystemPrompt: false);
            // Stream sem texto e sem ferramenta: o turno termina sem fala do assistente.
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }

            string caminho = Path.Combine(conversation.SessionMemoryDir, "raw.jsonl");
            File.Exists(caminho).Should().BeTrue("o turno aconteceu, e o registro é do que aconteceu");

            string linha = File.ReadAllText(caminho);
            linha.Should().Contain("oi");
            linha.Should().Contain("turno encerrado sem resposta",
                "a marca diz POR QUE não houve fala, em vez de deixar um usuário sem resposta");
        }

        [Fact]
        public async Task TurnoVazio_FICA_CompactavelDepois()
        {
            // A consequência que importa: fechado, ele entra na conta de SelectTurnsToCompact.
            // Aberto, ele era uma parede — todos os turnos seguintes ficavam inalcançáveis para
            // a compactação, e só a poda de emergência agia, descartando sem substituto.
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }

            var turnos = AIB.Services.Memory.TurnSplitter.Split(conversation.SnapshotHistory());

            turnos.Should().HaveCount(1);
            AIB.Services.Memory.TurnSplitter.IsClosed(turnos[0]).Should().BeTrue();
        }

        [Fact]
        public async Task RegistroNuncaEscreveNaMemoriaRealDoUsuario()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = new FakeProvider(new StreamChunk[]
            {
                new StreamChunk.TextDelta("ok", TextChannel.Final),
                new StreamChunk.Done(StreamFinishReason.Stop, "stop")
            });
            var conversation = BuildConversation(settings, provider, out _);

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }

            conversation.SessionMemoryDir.Should().StartWith(_dir);
            conversation.SessionMemoryDir.Should().NotContain(DirectoryService.MemoryDir);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Compactação em capítulos
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Conversa até o primeiro capítulo nascer, ou até o teto de turnos.
        /// <para>
        /// Conversar até o gatilho disparar, em vez de calcular quantos turnos seriam precisos,
        /// mantém o teste honesto quando a escala de tokens por nível mudar — foi exatamente
        /// assim que os testes de poda pararam de podar em silêncio.
        /// </para>
        /// </summary>
        private static async Task<int> ConversarAteCompactar(
            ConversationService conversation, int tetoDeTurnos = 40)
        {
            string pergunta = string.Concat(Enumerable.Repeat("uma frase qualquer para gastar tokens. ", 40));

            for (int i = 0; i < tetoDeTurnos; i++)
            {
                await foreach (var _ in conversation.StreamResponseAsync($"{pergunta} pergunta {i}")) { }
                if (conversation.Chapters.Count > 0) return i + 1;
            }

            return -1;
        }

        private FakeProvider ProviderQueResponde(string resposta) =>
            new(new StreamChunk[]
            {
                new StreamChunk.TextDelta(resposta, TextChannel.Final),
                new StreamChunk.Done(StreamFinishReason.Stop, "stop")
            });

        [Fact]
        public async Task ConversaLonga_FechaCapituloEEncolheOHistorico()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "O usuário fez várias perguntas e o agente respondeu.";
            var conversation = BuildConversation(settings, provider, out _);

            int turnos = await ConversarAteCompactar(conversation);

            turnos.Should().BePositive("o gatilho tem de disparar antes do teto de turnos");
            conversation.Chapters.Should().ContainSingle();
            conversation.Chapters[0].Summary.Should().Be("O usuário fez várias perguntas e o agente respondeu.");

            // O que saiu do contexto vivo continua em disco.
            var turnosVivos = conversation.SnapshotHistory().Count(m => m is UserChatMessage);
            turnosVivos.Should().BeLessThan(turnos, "os turnos antigos viraram capítulo");
            File.ReadAllLines(Path.Combine(conversation.SessionMemoryDir, "raw.jsonl"))
                .Should().HaveCount(turnos, "raw.jsonl nunca perde turno");
        }

        [Fact]
        public async Task Compactacao_GuardaOsTurnosMaisRecentes()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await ConversarAteCompactar(conversation);

            // O contexto imediato nunca é resumido: a última pergunta e a anterior continuam
            // cruas, ou o modelo perderia o assunto em curso.
            conversation.SnapshotHistory().Count(m => m is UserChatMessage)
                .Should().BeGreaterThanOrEqualTo(2);
        }

        [Fact]
        public async Task BlocoDeMemoria_EntraComoSegundaMensagemDeSistema()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            string promptOriginal = TextOf(conversation.SnapshotHistory()[0]);

            await ConversarAteCompactar(conversation);

            var historico = conversation.SnapshotHistory();

            // A primeira mensagem fica byte a byte idêntica: é ela que o cache de prefixo
            // reaproveita, e reconstruí-la exigiria reler SOUL.MD do disco a cada capítulo.
            TextOf(historico[0]).Should().Be(promptOriginal);

            historico[1].Should().BeOfType<SystemChatMessage>();
            TextOf(historico[1]).Should().Contain("Memória da conversa");
        }

        [Fact]
        public async Task SegundoCapitulo_SubstituiOBlocoEmVezDeEmpilharOutraMensagem()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await ConversarAteCompactar(conversation);
            await ConversarAteCompactar(conversation, tetoDeTurnos: 60);

            var historico = conversation.SnapshotHistory();

            historico.Count(m => m is SystemChatMessage)
                .Should().Be(2, "prompt base + UM bloco de memória, sempre");
        }

        [Fact]
        public async Task Poda_NaoComeOBlocoDeMemoria()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await ConversarAteCompactar(conversation);

            // A poda de emergência entra atrás da compactação. Se ela comesse o índice 1,
            // apagaria o resumo que acabou de custar uma chamada ao modelo — e junto os turnos
            // que ele substituiu, que já saíram do histórico vivo.
            for (int i = 0; i < 5; i++) conversation.Trim(userLevel: 1);

            TextOf(conversation.SnapshotHistory()[1]).Should().Contain("Memória da conversa");
        }

        [Fact]
        public async Task CapituloEhGravadoEmChaptersJsonl()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await ConversarAteCompactar(conversation);

            var linhas = File.ReadAllLines(Path.Combine(conversation.SessionMemoryDir, "chapters.jsonl"));

            linhas.Should().ContainSingle();
            linhas[0].Should().Contain("\"Index\":0");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Capitulo e ato a pedido do usuario
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ForcarCapitulo_FechaSemOGatilhoDeTokens()
        {
            // Conversa curta: o gatilho automatico nunca dispararia aqui. O comando existe
            // justamente para isso.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "O usuario cumprimentou e perguntou das horas.";
            var conversation = BuildConversation(settings, provider, out _);

            for (int i = 0; i < 4; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"pergunta {i}")) { }

            conversation.Chapters.Should().BeEmpty("o gatilho de tokens nao foi cruzado");

            string resposta = await conversation.ForcarCapituloAsync(userLevel: 1);

            conversation.Chapters.Should().ContainSingle();
            resposta.Should().Contain("fechado");
        }

        [Fact]
        public async Task ForcarCapitulo_SemTurnoSobrando_RecusaDizendoPorque()
        {
            // Os dois turnos mais recentes ficam sempre fora do capitulo. Com dois turnos no
            // total nao sobra nada, e a recusa precisa dizer isso — "nao foi possivel" mandaria
            // o usuario tentar de novo sem saber o que mudar.
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, ProviderQueResponde("ok"), out _);

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }

            string resposta = await conversation.ForcarCapituloAsync(userLevel: 1);

            conversation.Chapters.Should().BeEmpty();
            resposta.Should().Contain("recentes");
        }

        [Fact]
        public async Task ForcarAto_SemCapitulo_RecusaEExplicaOCaminho()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, ProviderQueResponde("ok"), out _);

            string resposta = await conversation.ForcarAtoAsync(userLevel: 1);

            conversation.Acts.Should().BeEmpty();
            resposta.Should().Contain("capitulo", "a recusa precisa apontar o passo que falta");
        }

        [Fact]
        public async Task ForcarAto_ComUmCapituloSo_Recusa()
        {
            // Um ato sobre um capitulo e resumo de resumo: troca o texto por outro mais pobre e
            // ainda esconde o original, porque capitulo coberto por ato para de ser renderizado.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "resumo do capitulo";
            var conversation = BuildConversation(settings, provider, out _);

            for (int i = 0; i < 4; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"pergunta {i}")) { }

            await conversation.ForcarCapituloAsync(userLevel: 1);
            conversation.Chapters.Should().ContainSingle();

            string resposta = await conversation.ForcarAtoAsync(userLevel: 1);

            conversation.Acts.Should().BeEmpty();
            resposta.Should().Contain("resumo de resumo");
        }

        [Fact]
        public async Task SemPromptDeSistema_OResumoNaoEhDescontadoDoTotal()
        {
            // Com "SendSystemPrompt" desligado nao ha onde ancorar o bloco de memoria, e ele
            // nunca e enviado. O custo do resumo continuava sendo descontado do total assim
            // mesmo, e o total ficava MENOR que o contexto — a guarda de sanidade entao
            // igualava os dois e a economia sumia da tela para sempre.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = string.Concat(
                Enumerable.Repeat("um resumo deliberadamente longo para superar os turnos crus. ", 20));
            var conversation = BuildConversation(settings, provider, out _);

            for (int i = 0; i < 4; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"curta {i}")) { }

            await conversation.ForcarCapituloAsync(userLevel: 1);

            var relatorio = conversation.CurrentTokenReport;

            relatorio.Total.Should().BeGreaterThan(relatorio.Contexto,
                "os turnos compactados pesam no total, e o resumo nao entrou no prompt");
        }

        [Fact]
        public async Task OAtoEncolheOContextoESeguraOTotal()
        {
            // O que o ato faz e o que ele NAO faz. Ele troca os capitulos pelo resumo deles,
            // entao o contexto encolhe. O total nao se mexe: ele representa o que a conversa
            // crua custaria, e a conversa crua nao mudou por causa da promocao.
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "O usuario perguntou varias coisas e o agente respondeu.";
            var conversation = BuildConversation(settings, provider, out _);

            // Turnos GRANDES de proposito. Com mensagens curtas o resumo custa mais que os
            // turnos que ele substituiu, a guarda de sanidade iguala total e contexto, e o
            // ensaio mediria o clamp em vez da promocao.
            string longa = string.Concat(Enumerable.Repeat("uma frase qualquer para gastar tokens. ", 30));

            for (int i = 0; i < 4; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"{longa} primeira leva {i}")) { }
            await conversation.ForcarCapituloAsync(userLevel: 1);

            for (int i = 0; i < 4; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"{longa} segunda leva {i}")) { }
            await conversation.ForcarCapituloAsync(userLevel: 1);

            var antes = conversation.CurrentTokenReport;

            await conversation.ForcarAtoAsync(userLevel: 1);

            var depois = conversation.CurrentTokenReport;

            depois.Contexto.Should().BeLessThan(antes.Contexto,
                "o ato substitui os capitulos no prompt");
            depois.Total.Should().Be(antes.Total,
                "promover nao muda o que a conversa crua custaria");
        }

        [Fact]
        public async Task ForcarAto_ComDoisCapitulos_Fecha()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "resumo qualquer";
            var conversation = BuildConversation(settings, provider, out _);

            for (int i = 0; i < 4; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"primeira leva {i}")) { }
            await conversation.ForcarCapituloAsync(userLevel: 1);

            for (int i = 0; i < 4; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"segunda leva {i}")) { }
            await conversation.ForcarCapituloAsync(userLevel: 1);

            conversation.Chapters.Should().HaveCount(2);

            string resposta = await conversation.ForcarAtoAsync(userLevel: 1);

            conversation.Acts.Should().ContainSingle();
            resposta.Should().Contain("Ato");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Reabrir uma conversa arquivada
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ReabrirConversa_TrazDeVoltaOsCapitulos()
        {
            // O defeito: abrir uma conversa antiga jogava fora os capitulos e os atos dela e
            // recarregava a conversa inteira crua. O contador voltava a mostrar so o numero do
            // contexto, sem economia nenhuma, e o trabalho de compactacao daquela sessao era
            // perdido — em conversa longa, direto para a poda de emergencia.
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "O usuario perguntou varias coisas e o agente respondeu.";
            var conversation = BuildConversation(settings, provider, out _);

            string longa = string.Concat(Enumerable.Repeat("uma frase qualquer para gastar tokens. ", 30));
            for (int i = 0; i < 4; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"{longa} turno {i}")) { }

            await conversation.ForcarCapituloAsync(userLevel: 1);
            conversation.Chapters.Should().ContainSingle();

            string memoria = Path.GetFileName(conversation.SessionMemoryDir);

            var falas = new List<ChatTurn> { new(true, "pergunta antiga"), new(false, "resposta antiga") };
            conversation.LoadConversation(falas, memoria, sessionId: "ensaio-reabrir");

            conversation.Chapters.Should().ContainSingle("os capitulos voltam com a conversa");

            var relatorio = conversation.CurrentTokenReport;
            relatorio.Total.Should().BeGreaterThan(relatorio.Contexto,
                "os turnos que viraram capitulo continuam pesando no total");
        }

        [Fact]
        public async Task ReabrirConversa_NaoRepeteOsTurnosJaResumidos()
        {
            // O outro lado: se os turnos crus voltassem AO LADO dos capitulos, o modelo
            // receberia a mesma conversa duas vezes — uma resumida e outra inteira.
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "resumo do capitulo";
            var conversation = BuildConversation(settings, provider, out _);

            string longa = string.Concat(Enumerable.Repeat("uma frase qualquer para gastar tokens. ", 30));
            for (int i = 0; i < 4; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"{longa} turno {i}")) { }

            await conversation.ForcarCapituloAsync(userLevel: 1);
            string memoria = Path.GetFileName(conversation.SessionMemoryDir);

            var falas = new List<ChatTurn>();
            for (int i = 0; i < 4; i++)
            {
                falas.Add(new ChatTurn(true, $"{longa} turno {i}"));
                falas.Add(new ChatTurn(false, "certo"));
            }

            conversation.LoadConversation(falas, memoria, sessionId: "ensaio-sem-repetir");

            int doUsuario = conversation.SnapshotHistory().Count(m => m is UserChatMessage);

            doUsuario.Should().BeLessThan(4, "os turnos ja resumidos nao voltam crus");
        }

        [Fact]
        public void ReabrirSemPastaDeMemoria_CarregaAConversaInteira()
        {
            // Conversa gravada antes do campo existir: reabre do jeito antigo, com tudo cru.
            // Recusar-se a abrir seria pior que abrir sem memoria.
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, ProviderQueResponde("ok"), out _);

            var falas = new List<ChatTurn>
            {
                new(true, "primeira"), new(false, "resposta"),
                new(true, "segunda"), new(false, "resposta")
            };

            conversation.LoadConversation(falas, memorySessionId: "", sessionId: "ensaio-antigo");

            conversation.SnapshotHistory().Count(m => m is UserChatMessage)
                .Should().Be(2, "sem memoria, a conversa volta inteira");
        }

        [Fact]
        public async Task DescartarAConversaDeUmEmail_NaoARessuscitaAoRecomecar()
        {
            // O defeito: a tela apagava a entrada do histórico e SÓ DEPOIS recomeçava a conversa.
            // O recomeço arquiva a transcrição viva — e a conversa descartada voltava ao
            // histórico, com o mesmo id, um instante depois de o usuário confirmar o descarte.
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            string chave = $"eu@gmail.com|thr:ensaio-{Guid.NewGuid():N}";
            conversation.VincularAEmail(chave);

            await foreach (var _ in conversation.StreamResponseAsync($"sobre o e-mail {chave}")) { }

            ChatHistoryService.ConversaDoEmail(chave).Should().NotBeNull("o turno arquiva a conversa");

            conversation.DescartarConversaDoEmail(chave).Should().BeTrue("era a conversa aberta");

            // O que a tela faz em seguida: começar uma conversa nova.
            conversation.ResetHistory();

            ChatHistoryService.ConversaDoEmail(chave).Should().BeNull("descartada não volta");
            conversation.ChaveDoEmail.Should().BeEmpty();
        }

        [Fact]
        public void DescartarConversaDeOutroEmail_NaoMexeNaConversaAberta()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            string aberta = $"eu@gmail.com|thr:aberta-{Guid.NewGuid():N}";
            conversation.VincularAEmail(aberta);

            conversation.DescartarConversaDoEmail($"eu@gmail.com|thr:outra-{Guid.NewGuid():N}")
                .Should().BeFalse();

            conversation.ChaveDoEmail.Should().Be(aberta);
        }

        [Fact]
        public async Task ReabrirConversa_DevolveOsTurnosGravadosDela()
        {
            // É destes turnos que a aba de ações é remontada ao reabrir. Sem eles, a aba abria
            // vazia para qualquer conversa que não fosse a da sessão corrente.
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            for (int i = 0; i < 2; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"turno {i}")) { }

            string memoria = Path.GetFileName(conversation.SessionMemoryDir);

            var falas = new List<ChatTurn> { new(true, "turno 0"), new(false, "certo") };
            conversation.LoadConversation(falas, memoria, sessionId: "ensaio-registro");

            conversation.TurnosGravados().Should().HaveCount(2);
        }

        [Fact]
        public async Task SegundoCapitulo_GravaOsTurnosPeloNumeroDoRegistro_EAReaberturaRespeita()
        {
            // O defeito: o capítulo gravava a posição do turno no histórico VIVO, que recomeça do
            // zero a cada compactação — o segundo capítulo também dizia "turnos 0–1". Ao reabrir,
            // a conversa parecia coberta só até o turno 1, e os turnos 2 e 3, já resumidos,
            // voltavam crus ao lado do resumo deles.
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "resumo";
            var conversation = BuildConversation(settings, provider, out _);

            for (int i = 0; i < 4; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"turno {i}")) { }
            await conversation.ForcarCapituloAsync(userLevel: 1);

            for (int i = 4; i < 6; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"turno {i}")) { }
            await conversation.ForcarCapituloAsync(userLevel: 1);

            conversation.Chapters.Should().HaveCount(2);
            conversation.Chapters[1].FirstTurn.Should().Be(2);
            conversation.Chapters[1].LastTurn.Should().Be(3);

            string memoria = Path.GetFileName(conversation.SessionMemoryDir);
            var falas = new List<ChatTurn> { new(true, "turno 0"), new(false, "certo") };
            conversation.LoadConversation(falas, memoria, sessionId: "ensaio-dois-capitulos");

            conversation.SnapshotHistory()
                .Where(m => m is UserChatMessage)
                .Select(TextOf)
                .Should().Equal(new[] { "turno 4", "turno 5" },
                    "só os turnos que nenhum capítulo resumiu voltam crus");
        }

        [Fact]
        public async Task ReabrirConversa_TrazAsExecucoes_EOCapituloTemArtefatos()
        {
            // O defeito: a reabertura trazia só as falas. A compactação que rodava depois
            // resumia turnos sem nenhuma execução e fechava capítulos com zero artefatos — numa
            // conversa que tinha lido e gravado arquivos.
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            string sessao = "ensaio-reabrir-inteiro-" + Guid.NewGuid().ToString("N");
            var memoria = new AIB.Services.Memory.SessionMemory(sessao, Path.Combine(_dir, "memory"));
            for (int i = 0; i < 3; i++)
            {
                string caminho = $@"C:\temp\arquivo{i}.txt";
                var turno = AIB.Services.Memory.TurnSplitter.Split(new List<ChatMessage>
                {
                    ChatMessage.CreateUserMessage($"leia o arquivo {i}"),
                    ChatMessage.CreateAssistantMessage(new[]
                    {
                        ChatToolCall.CreateFunctionToolCall($"c{i}", "read",
                            BinaryData.FromString("{\"path\":\"" + caminho.Replace(@"\", @"\\") + "\"}"))
                    }),
                    ChatMessage.CreateToolMessage($"c{i}", "     1\tconteúdo"),
                    ChatMessage.CreateAssistantMessage("li")
                })[0] with { Index = i };
                memoria.AppendTurn(turno).Should().BeTrue();
            }

            var falas = new List<ChatTurn> { new(true, "leia o arquivo 0"), new(false, "li") };
            conversation.LoadConversation(falas, sessao, sessionId: "ensaio-reabrir-inteiro");

            var vivo = conversation.SnapshotHistory();
            vivo.OfType<ToolChatMessage>().Should().HaveCount(3, "os resultados voltam com a conversa");
            vivo.OfType<AssistantChatMessage>().Count(a => a.ToolCalls.Count > 0).Should().Be(3);

            await conversation.ForcarCompactacaoAsync(userLevel: 9);

            conversation.Chapters.Should().ContainSingle();
            conversation.Chapters[0].Artifacts.Should().ContainSingle()
                .Which.Value.Should().Be(@"C:\temp\arquivo0.txt");
        }

        [Fact]
        public async Task Compact_FechaTodosOsCapitulosEDepoisOsAtos()
        {
            // Um /compact fechava UM capítulo (no máximo oito turnos) e parava. Numa conversa
            // longa reaberta, o usuário tinha de repetir o comando sem saber quantas vezes.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "resumo";
            var conversation = BuildConversation(settings, provider, out _);

            for (int i = 0; i < 20; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"pergunta {i}")) { }

            // A compactação automática pode ter fechado capítulos no caminho; o que importa é onde
            // o comando deixa a conversa.
            int antes = conversation.Chapters.Count;

            string resposta = await conversation.ForcarCompactacaoAsync(userLevel: 9);

            conversation.Chapters.Count.Should().BeGreaterThan(antes + 1, "mais de um capítulo num comando só");
            conversation.Chapters[^1].LastTurn.Should().Be(17, "os dois mais recentes ficam vivos");
            AIB.Services.Memory.TurnSplitter.Split(conversation.SnapshotHistory()).Should().HaveCount(2);
            conversation.Acts.Should().NotBeEmpty();
            conversation.Chapters.Count(c => c.Index > conversation.Acts.Max(a => a.LastChapter))
                .Should().BeLessThan(2, "o que sobrou solto já foi promovido");
            resposta.Should().NotContain("Nada a compactar", "o fim da fila não é recusa");
        }

        [Fact]
        public async Task ForcarCapitulo_ComDoisTurnos_ResumeOMaisAntigo()
        {
            // Com a regra fixa de "os dois mais recentes ficam fora", um turno miúdo seguido de
            // uma cadeia de 18 ferramentas travava a compactação: acima do gatilho, e o /compact
            // respondendo "nada a compactar". O piso agora é o último turno.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "resumo";
            var conversation = BuildConversation(settings, provider, out _);

            await foreach (var _ in conversation.StreamResponseAsync("miúdo")) { }
            await foreach (var _ in conversation.StreamResponseAsync("enorme")) { }

            string resposta = await conversation.ForcarCapituloAsync(userLevel: 1);

            resposta.Should().Contain("fechado");
            conversation.Chapters.Should().ContainSingle();
            conversation.Chapters[0].LastTurn.Should().Be(0, "o último turno continua vivo");
            conversation.SnapshotHistory().Select(TextOf).Should().Contain("enorme");
        }

        [Fact]
        public async Task APodaNoMeioDoTurno_NaoTiraNadaDoRegistro()
        {
            // O defeito: o turno ia ao raw.jsonl copiado do histórico vivo no FIM, e a poda de
            // emergência corta o vivo no meio de uma cadeia longa. O que ela cortava antes do fim
            // — chamadas e resultados do começo do turno — não chegava ao disco.
            var settings = BuildSettings(sendSystemPrompt: true);
            ConversationService? conversation = null;
            int chamadas = 0;

            var provider = new ProviderRoteirizado(_ =>
            {
                chamadas++;
                if (chamadas == 1)
                    return new StreamChunk[]
                    {
                        new StreamChunk.ToolCallDelta("k0", "id-do-comeco", "ferramenta_inexistente", "{}"),
                        new StreamChunk.Done(StreamFinishReason.ToolCalls, "tool_calls")
                    };

                // Segunda volta: o turno já tem a chamada e o resultado. Enche a janela e poda,
                // como a cadeia longa faz.
                int quantas = MensagensParaEstourarAJanela(2000);
                for (int i = 0; i < quantas; i++) conversation!.AppendAssistantText(Filler(2000));
                conversation!.Trim(1);

                return new StreamChunk[]
                {
                    new StreamChunk.TextDelta("terminei", TextChannel.Final),
                    new StreamChunk.Done(StreamFinishReason.Stop, "stop")
                };
            });
            conversation = BuildConversation(settings, provider, out _);

            await foreach (var _ in conversation.StreamResponseAsync("tarefa longa")) { }

            conversation.SnapshotHistory().OfType<ToolChatMessage>()
                .Should().BeEmpty("a poda precisa ter cortado o começo do turno para o ensaio valer");

            var turno = conversation.TurnosGravados().Should().ContainSingle().Subject;
            turno.Messages.Should().Contain(m => m.ToolCalls != null && m.ToolCalls.Any(c => c.Id == "id-do-comeco"));
            turno.Messages.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "id-do-comeco");
            turno.Messages[^1].Text.Should().Contain("terminei");
            File.Exists(Path.Combine(conversation.SessionMemoryDir, "turno-aberto.json"))
                .Should().BeFalse("o turno fechou");
        }

        [Fact]
        public async Task ORegistro_GuardaAContaDeCadaPasso()
        {
            // Hora de cada mensagem; modelo, tokens e duração de cada fala; duração, decisão e
            // falha de cada ferramenta. Antes isso só existia espalhado entre o log de execução
            // e o de auditoria, e não dava para medir um turno a partir do raw.jsonl.
            var settings = BuildSettings(sendSystemPrompt: true);
            int chamadas = 0;

            var provider = new ProviderRoteirizado(_ => ++chamadas == 1
                ? new StreamChunk[]
                {
                    new StreamChunk.ToolCallDelta("k0", "id0", "ferramenta_inexistente", "{}"),
                    new StreamChunk.Usage(PromptEvalCount: 321, EvalCount: 12),
                    new StreamChunk.Done(StreamFinishReason.ToolCalls, "tool_calls")
                }
                : new StreamChunk[]
                {
                    new StreamChunk.TextDelta("não existe", TextChannel.Final),
                    new StreamChunk.Usage(PromptEvalCount: 350, EvalCount: 5),
                    new StreamChunk.Done(StreamFinishReason.Stop, "stop")
                });
            var conversation = BuildConversation(settings, provider, out _);

            await foreach (var _ in conversation.StreamResponseAsync("use a ferramenta")) { }

            var mensagens = conversation.TurnosGravados().Should().ContainSingle().Subject.Messages;
            mensagens.Select(m => m.Role).Should().Equal("user", "assistant", "tool", "assistant");
            mensagens.Should().OnlyContain(m => m.AtUtc != null, "toda mensagem tem hora");

            mensagens[1].Modelo.Should().Be("fake");
            mensagens[1].TokensEntrada.Should().Be(321);
            mensagens[1].TokensSaida.Should().Be(12);
            mensagens[1].DuracaoMs.Should().NotBeNull();

            mensagens[2].Decisao.Should().Be("ferramenta_desconhecida");
            mensagens[2].Falhou.Should().BeTrue();
            mensagens[2].DuracaoMs.Should().NotBeNull();
            mensagens[2].EsperaHumanaMs.Should().Be(0);

            mensagens[3].TokensEntrada.Should().Be(350);
            mensagens[3].Decisao.Should().BeNull("fala não passa por portão");
        }

        [Fact]
        public async Task TurnoEmCurso_DeixaOArquivoDeRecuperacao()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = new ProviderQueSegura("certo") { Segurar = true };
            var conversation = BuildConversation(settings, provider, out _);

            var turno = Task.Run(async () =>
            {
                await foreach (var _ in conversation.StreamResponseAsync("pergunta demorada")) { }
            });

            await provider.Comecou.Task.WaitAsync(TimeSpan.FromSeconds(10));

            string arquivo = Path.Combine(conversation.SessionMemoryDir, "turno-aberto.json");
            File.Exists(arquivo).Should().BeTrue("se o AIB cair agora, é o que sobra do turno");
            File.ReadAllText(arquivo).Should().Contain("pergunta demorada");

            provider.Liberar.SetResult();
            await turno.WaitAsync(TimeSpan.FromSeconds(10));

            File.Exists(arquivo).Should().BeFalse();
            conversation.TurnosGravados().Should().ContainSingle().Which.Id.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task TrocarDeConversaNoMeioDoTurno_OTurnoFicaNaConversaDele()
        {
            // O defeito: abrir outra conversa do histórico com um turno em voo não esperava por
            // ele. O resto do turno escrevia no histórico da conversa ABERTA, e o fim dele gravava
            // no raw.jsonl dela o último turno que encontrou — o restaurado, que ficou duplicado.
            // O turno de verdade não ficava em lugar nenhum.
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = new ProviderQueSegura("certo");
            var conversation = BuildConversation(settings, provider, out _);

            for (int i = 0; i < 2; i++)
                await foreach (var _ in conversation.StreamResponseAsync($"antiga {i}")) { }
            string antiga = Path.GetFileName(conversation.SessionMemoryDir);

            conversation.ResetHistory();
            string emVoo = Path.GetFileName(conversation.SessionMemoryDir);

            provider.Resposta = "resposta atrasada";
            provider.Segurar = true;
            var turno = Task.Run(async () =>
            {
                try { await foreach (var _ in conversation.StreamResponseAsync("pergunta em voo")) { } }
                catch (OperationCanceledException) { }
            });

            await provider.Comecou.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var falas = new List<ChatTurn> { new(true, "antiga 0"), new(false, "certo") };
            conversation.LoadConversation(falas, antiga, sessionId: "ensaio-troca-em-voo");

            provider.Liberar.SetResult();
            await turno.WaitAsync(TimeSpan.FromSeconds(10));

            conversation.SnapshotHistory().Select(TextOf)
                .Should().NotContain(t => t.Contains("resposta atrasada") || t.Contains("pergunta em voo"),
                    "o turno em voo não escreve na conversa que foi aberta por cima dele");

            conversation.TurnosGravados().Should().HaveCount(2, "o turno restaurado não é gravado de novo");

            var doTurnoEmVoo = new AIB.Services.Memory.SessionMemory(emVoo, Path.Combine(_dir, "memory")).ReadTurns();
            doTurnoEmVoo.Should().ContainSingle("o turno fica gravado na conversa a que pertencia");
            doTurnoEmVoo[0].Messages.Should().Contain(m => m.Text != null && m.Text.Contains("pergunta em voo"));
            doTurnoEmVoo[0].Messages.Should().Contain(m => m.Text != null && m.Text.Contains("trocada"));
        }

        [Fact]
        public async Task SemCapitulo_OTotalEhOProprioContexto()
        {
            // O contador nao pode inventar economia antes de haver o que economizar: sem
            // capitulo nenhum turno foi substituido por resumo, e os dois numeros sao o mesmo.
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, ProviderQueResponde("ok"), out _);

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }

            var relatorio = conversation.CurrentTokenReport;

            relatorio.Contexto.Should().BePositive();
            relatorio.Total.Should().Be(relatorio.Contexto);
            relatorio.EconomiaPct.Should().BeNull();
        }

        [Fact]
        public async Task DepoisDoCapitulo_OTotalGuardaOsTurnosCrus()
        {
            // A conta que o rodape mostra: o total continua carregando os turnos que sairam do
            // contexto, e o contexto encolheu para o resumo. Sem isto o numero da esquerda
            // encolheria junto com o da direita e a economia sumiria no instante em que ela
            // passou a existir.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "O usuario perguntou varias coisas.";
            var conversation = BuildConversation(settings, provider, out _);

            int turnos = await ConversarAteCompactar(conversation);
            turnos.Should().BePositive();

            var relatorio = conversation.CurrentTokenReport;

            relatorio.Total.Should().BeGreaterThan(relatorio.Contexto,
                "os turnos compactados continuam pesando no total");
            relatorio.EconomiaPct.Should().BePositive();
        }

        [Fact]
        public async Task ConversaCurta_NaoCompacta()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("ok");
            var conversation = BuildConversation(settings, provider, out _);

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }
            await foreach (var _ in conversation.StreamResponseAsync("tudo bem?")) { }

            conversation.Chapters.Should().BeEmpty();

            // Cada compactação custa um prefill frio: disparar cedo seria pior que o corte
            // seco. A única chamada fora do stream aqui é a titulação, que acontece uma vez,
            // depois do primeiro turno, quando o histórico ainda é pequeno.
            provider.CompleteCalls.Should().Be(1, "só a titulação, e nenhuma ao resumidor");
            conversation.Title.Should().Be("SISTEMA ONLINE", "o título vem do CompleteAsync, não do stream");
        }

        [Fact]
        public async Task TurnoQueNaoFechou_EGravadoQuandoOProximoComeca()
        {
            // Defeito real, medido no disco: uma conversa de dois turnos foi para o histórico
            // com um turno só. O primeiro terminou sem resposta do modelo — turno aberto — e o
            // registro só olha o ÚLTIMO turno a cada chamada, então ele nunca voltou a ser
            // examinado. O comentário antigo dizia "fica para a próxima"; não ficava.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("resposta");
            var conversation = BuildConversation(settings, provider, out _);

            await foreach (var _ in conversation.StreamResponseAsync("primeira pergunta")) { }

            // Deixa o turno aberto à força: uma chamada de ferramenta sem o resultado
            // correspondente é exatamente o estado de um turno cancelado no meio.
            conversation.AppendAssistantText("");
            conversation.AppendAssistantToolCalls(new[]
            {
                ChatToolCall.CreateFunctionToolCall("id-x", "read", BinaryData.FromString("{}"))
            });

            await foreach (var _ in conversation.StreamResponseAsync("segunda pergunta")) { }

            string arquivo = System.IO.Path.Combine(conversation.SessionMemoryDir, "raw.jsonl");
            System.IO.File.Exists(arquivo).Should().BeTrue();

            string bruto = System.IO.File.ReadAllText(arquivo);
            bruto.Should().Contain("primeira pergunta");
            bruto.Should().Contain("segunda pergunta", "o turno pulado não pode levar os outros junto");
        }

        [Fact]
        public async Task ArquivoAnexado_ChegaAoPromptDoModelo()
        {
            // O caminho tem de estar no que o provider recebe, e não só na lista da interface:
            // era essa a lacuna — anexar pelo painel não informava nada ao modelo.
            string caminho = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "anexo-" + Guid.NewGuid().ToString("N") + ".md");

            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = ProviderQueResponde("ok");
            var conversation = BuildConversation(settings, provider, out _);

            try
            {
                ContextService.AddFile(caminho, ContextOrigin.AttachedByUser);

                await foreach (var _ in conversation.StreamResponseAsync("analisa esse arquivo")) { }

                string prompt = string.Concat(
                    conversation.SnapshotHistory()
                        .OfType<SystemChatMessage>()
                        .Select(m => string.Concat(m.Content.Where(c => c.Text != null).Select(c => c.Text))));

                prompt.Should().Contain(caminho);
            }
            finally
            {
                foreach (var arquivo in ContextService.ActiveFiles.ToList())
                    ContextService.RemoveFile(arquivo);
            }
        }

        [Fact]
        public async Task Titulo_SaiUmaVezSo_DepoisDoPrimeiroTurno()
        {
            // Renomear a conversa a cada turno é pior que um nome imperfeito: o item muda de
            // nome embaixo do usuário enquanto ele lê a lista. E cada titulação derruba o
            // cache de prefixo, que fica mais caro de reconstruir a cada turno que passa.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("ok");
            provider.CompleteReply = "Ajuste no painel lateral";

            var conversation = BuildConversation(settings, provider, out _);

            var avisos = new List<string>();
            conversation.OnTitleChanged += t => avisos.Add(t);

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }
            conversation.Title.Should().Be("Ajuste no painel lateral");

            await foreach (var _ in conversation.StreamResponseAsync("e agora?")) { }
            await foreach (var _ in conversation.StreamResponseAsync("e depois?")) { }

            provider.CompleteCalls.Should().Be(1, "titula uma vez, não a cada turno");
            avisos.Should().ContainSingle();
        }

        [Fact]
        public async Task TituloImpublicavel_DeixaAConversaSemNome()
        {
            // Um modelo que responde um parágrafo em vez de um título não pode pendurar esse
            // parágrafo no cabeçalho. Sem nome, quem arquiva cai na heurística antiga.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("ok");
            provider.CompleteReply = new string('x', ChatTitler.MaxCaracteres + 20);

            var conversation = BuildConversation(settings, provider, out _);

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }

            conversation.Title.Should().BeNull();
        }

        [Fact]
        public async Task FalhaAoTitular_NaoDerrubaOTurno()
        {
            // A titulação roda depois de o usuário já ter a resposta. Nada nela pode escapar.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("resposta ao usuário");
            provider.CompleteThrows = new InvalidOperationException("modelo fora do ar");

            var conversation = BuildConversation(settings, provider, out _);

            var texto = "";
            await foreach (var item in conversation.StreamResponseAsync("oi"))
            {
                if (item is ChatStreamItem.Text t) texto += t.Value;
            }

            texto.Should().Contain("resposta ao usuário");
            conversation.Title.Should().BeNull();
        }

        [Fact]
        public async Task ResetHistory_ComecaSessaoNovaSemOsCapitulosDaAnterior()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await ConversarAteCompactar(conversation);
            conversation.Chapters.Should().NotBeEmpty();

            conversation.ResetHistory();

            conversation.Chapters.Should().BeEmpty("memória de outra conversa costuraria assuntos sem relação");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Promoção: atos e fatos duráveis
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Conversa até o primeiro ato nascer. Mesmo princípio do ConversarAteCompactar: espera
        /// o gatilho em vez de calcular quantos turnos ele exigiria, porque a escala de tokens
        /// por nível já mudou uma vez e deixou testes passando sem nunca acionar o que cobriam.
        /// </summary>
        private static async Task<int> ConversarAteFecharAto(
            ConversationService conversation, int tetoDeTurnos = 240)
        {
            string pergunta = string.Concat(Enumerable.Repeat("uma frase qualquer para gastar tokens. ", 40));

            for (int i = 0; i < tetoDeTurnos; i++)
            {
                await foreach (var _ in conversation.StreamResponseAsync($"{pergunta} pergunta {i}")) { }
                if (conversation.Acts.Count > 0) return i + 1;
            }

            return -1;
        }

        [Fact]
        public async Task QuatroCapitulos_FechamUmAto()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "O agente respondeu a uma sequência de perguntas.";
            var conversation = BuildConversation(settings, provider, out _);

            int turnos = await ConversarAteFecharAto(conversation);

            turnos.Should().BePositive("o ato tem de nascer antes do teto de turnos");
            conversation.Acts.Should().ContainSingle();
            conversation.Acts[0].FirstChapter.Should().Be(0);
            conversation.Acts[0].LastChapter.Should().Be(3);
            conversation.Chapters.Should().HaveCountGreaterThanOrEqualTo(4, "o ato não apaga capítulo");
        }

        [Fact]
        public async Task AtoEhGravadoEmActsJsonl()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await ConversarAteFecharAto(conversation);

            var linhas = File.ReadAllLines(Path.Combine(conversation.SessionMemoryDir, "acts.jsonl"));

            linhas.Should().ContainSingle();
            linhas[0].Should().Contain("\"Index\":0");
        }

        [Fact]
        public async Task CapitulosEmDiscoSobrevivemAoAto()
        {
            // O ato substitui os capítulos no PROMPT, não em disco. raw.jsonl e chapters.jsonl
            // continuam sendo a rede de segurança de quando o resumo se mostrar ruim.
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await ConversarAteFecharAto(conversation);

            File.ReadAllLines(Path.Combine(conversation.SessionMemoryDir, "chapters.jsonl"))
                .Length.Should().BeGreaterThanOrEqualTo(4);
        }

        [Fact]
        public async Task DepoisDoAto_OBlocoDeMemoriaMostraOAtoENaoOsCapitulosCobertos()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = ProviderQueResponde("certo");
            provider.CompleteReply = "RESUMO-DO-TRECHO";
            var conversation = BuildConversation(settings, provider, out _);

            await ConversarAteFecharAto(conversation);

            string bloco = TextOf(conversation.SnapshotHistory()[1]);

            bloco.Should().Contain("### Ato 1");
            bloco.Should().NotContain("### Capítulo 1", "esse já foi absorvido pelo ato");
            bloco.Should().NotContain("### Capítulo 4", "o último capítulo coberto também sai");
        }

        [Fact]
        public async Task DepoisDoAto_ContinuaHavendoUmaUnicaMensagemDeMemoria()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await ConversarAteFecharAto(conversation);

            conversation.SnapshotHistory().Count(m => m is SystemChatMessage)
                .Should().Be(2, "prompt base + UM bloco de memória, sempre");
        }

        [Fact]
        public void FatosDuraveis_EntramNoPromptDesdeOPrimeiroTurno()
        {
            // Fato só é durável se estiver lá antes da primeira compactação: é a única faixa da
            // memória que atravessa sessões, e esperar um capítulo para exibi-la a inutilizaria.
            string raiz = Path.Combine(_dir, "memory");
            Directory.CreateDirectory(raiz);
            File.WriteAllText(Path.Combine(raiz, "facts.md"),
                "# Fatos duráveis\n\n- Carlo escreve em Português (Brasil).\n");

            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            var historico = conversation.SnapshotHistory();

            historico[1].Should().BeOfType<SystemChatMessage>();
            TextOf(historico[1]).Should().Contain("Carlo escreve em Português (Brasil).");
        }

        [Fact]
        public void SemFatosNemCapitulos_NaoNasceMensagemDeMemoriaVazia()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.SnapshotHistory().Count(m => m is SystemChatMessage).Should().Be(1);
        }

        [Fact]
        public async Task FatosSobrevivemAoResetDeSessao()
        {
            string raiz = Path.Combine(_dir, "memory");
            Directory.CreateDirectory(raiz);
            File.WriteAllText(Path.Combine(raiz, "facts.md"), "- fato que atravessa sessões\n");

            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }
            conversation.ResetHistory();

            TextOf(conversation.SnapshotHistory()[1]).Should().Contain("fato que atravessa sessões");
        }

        [Fact]
        public async Task ResetHistory_EsqueceOsAtosDaSessaoAnterior()
        {
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await ConversarAteFecharAto(conversation);
            conversation.Acts.Should().NotBeEmpty();

            conversation.ResetHistory();

            conversation.Acts.Should().BeEmpty("ato de outra conversa costuraria assuntos sem relação");
        }

        [Fact]
        public void FatoEditadoComOAppAberto_EhLidoNaProximaMontagem()
        {
            // facts.md é do usuário: ele pode abrir o arquivo no meio da conversa.
            string raiz = Path.Combine(_dir, "memory");
            Directory.CreateDirectory(raiz);
            File.WriteAllText(Path.Combine(raiz, "facts.md"), "- versão antiga\n");

            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            File.WriteAllText(Path.Combine(raiz, "facts.md"), "- versão corrigida à mão\n");
            conversation.ResetHistory();

            TextOf(conversation.SnapshotHistory()[1]).Should().Contain("versão corrigida à mão");
        }

        [Fact]
        public void PromocaoNuncaEscreveNoFactsMdRealDoUsuario()
        {
            // Mesmo guard do raw.jsonl: sem raiz redirecionada, um teste gravaria fatos
            // inventados na memória permanente do usuário — e "durável" quer dizer que ele
            // teria de apagá-los à mão.
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.FactsPath.Should().StartWith(_dir);
        }
        // ─────────────────────────────────────────────────────────────────────
        // Compactação que falha: descanso e teto
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ResumoQueEstouraOTempo_NaoEhTentadoDeNovoTodoTurno()
        {
            // Contra o Ollama real, um resumidor lento estourava o tempo e voltava a tentar no
            // turno seguinte, e no seguinte — oito tentativas, todas perdidas, cada uma cobrada
            // do usuário porque a compactação segura o portão. Sem descanso, uma máquina lenta
            // transforma toda mensagem daí em diante numa espera do timeout inteiro.
            //
            // É o CANCELAMENTO que precisa de descanso, e não um erro qualquer do provider: um
            // erro fecha capítulo degradado e os turnos saem do contexto, então não se repete.
            // O cancelamento deixa tudo vivo, e o gatilho volta a disparar no turno seguinte.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteThrows = new OperationCanceledException("estourou o tempo");
            var conversation = BuildConversation(settings, provider, out _);

            string pergunta = string.Concat(Enumerable.Repeat("uma frase qualquer para gastar tokens. ", 40));

            int primeiraTentativa = -1;
            for (int i = 0; i < 40; i++)
            {
                await foreach (var _ in conversation.StreamResponseAsync($"{pergunta} pergunta {i}")) { }
                if (primeiraTentativa < 0 && provider.CompleteCalls > 0) primeiraTentativa = i;
            }

            primeiraTentativa.Should().BeGreaterThanOrEqualTo(0, "o gatilho tem de ter disparado");
            conversation.Chapters.Should().BeEmpty("resumo cancelado não fecha capítulo");

            int turnosDepois = 40 - primeiraTentativa;
            provider.CompleteCalls.Should().BeLessThan(turnosDepois,
                "com descanso de 3 turnos, a tentativa não pode acontecer em todos eles");
        }

        [Fact]
        public async Task ResumidorForaDoAr_FechaCapituloDegradadoESeguraVida()
        {
            // O outro lado da moeda do teste acima. Erro do provider NÃO deixa os turnos vivos:
            // o capítulo nasce com a nota no lugar do resumo e os artefatos intactos, e o
            // contexto encolhe do mesmo jeito. Por isso este caso não precisa de descanso.
            var settings = BuildSettings(sendSystemPrompt: false);
            var provider = ProviderQueResponde("certo");
            provider.CompleteThrows = new InvalidOperationException("conexão recusada");
            var conversation = BuildConversation(settings, provider, out _);

            await ConversarAteCompactar(conversation);

            conversation.Chapters.Should().ContainSingle();
            conversation.Chapters[0].Summary.Should().Contain("indisponível");
        }

        [Fact]
        public async Task CapituloNaoPassaDoTetoDeTurnos()
        {
            // Sem teto, a tentativa seguinte de uma compactação que falhou vem com MAIS turnos
            // que a anterior: 5, 6, 7, ..., cada uma mais cara, e nenhuma converge.
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, ProviderQueResponde("certo"), out _);

            await ConversarAteCompactar(conversation);

            var capitulo = conversation.Chapters[0];
            int turnosCobertos = capitulo.LastTurn - capitulo.FirstTurn + 1;

            turnosCobertos.Should().BeLessThanOrEqualTo(8);
        }

 
        // ──────────────────────────────────────────────────────────
        // A conversa nascida de um e-mail — gravada, marcada, e fora da LISTA
        // ──────────────────────────────────────────────────────────

        private ConversationService NovaConversaDeEnsaio() =>
            BuildConversation(BuildSettings(true), new FakeProvider(), out _);

        [Fact]
        public void ChaveDoEmail_NASCE_Vazia()
        {
            // A conversa comum é o caso normal; a de e-mail é que pede o vínculo.
            NovaConversaDeEnsaio().ChaveDoEmail.Should().BeEmpty();
        }

        [Fact]
        public void ChaveDoEmail_LIGA_ESeApagaNaConversaSeguinte()
        {
            // O vínculo é da conversa que termina no ResetHistory. Conversa nova nasce comum —
            // senão abrir um e-mail sumiria com todas as conversas dali em diante.
            var conversa = NovaConversaDeEnsaio();

            conversa.VincularAEmail("eu@x.com|thr:1");
            conversa.ChaveDoEmail.Should().Be("eu@x.com|thr:1");

            conversa.ResetHistory();
            conversa.ChaveDoEmail.Should().BeEmpty();
        }

        [Fact]
        public void ChaveDoEmail_SeApaga_MesmoSemTranscricao()
        {
            // Abrir um e-mail e desistir antes do primeiro turno deixa a conversa vazia. Se a
            // limpeza dependesse de haver transcrição, o vínculo ficaria preso e a conversa
            // SEGUINTE — essa de verdade — sumiria da lista do painel.
            var conversa = NovaConversaDeEnsaio();

            conversa.VincularAEmail("eu@x.com|thr:1");
            conversa.ResetHistory();

            conversa.ChaveDoEmail.Should().BeEmpty();
        }

        [Fact]
        public async Task OTurnoDeEmail_EH_Gravado_EReencontravelPelaThread()
        {
            // O DEFEITO QUE ISTO TRAVA: a primeira versão não gravava a conversa de e-mail, e a
            // resposta da IA passou a existir só no raw.jsonl — sem nenhuma porta de volta pela
            // interface. Tirar da LISTA não pode virar tirar da EXISTÊNCIA.
            string chave = "eu@x.com|thr:" + Guid.NewGuid().ToString("N");

            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.VincularAEmail(chave);

            await foreach (var _ in conversation.StreamResponseAsync("sobre o e-mail", _ => { })) { }

            var achada = ChatHistoryService.ConversaDoEmail(chave);

            achada.Should().NotBeNull("a conversa de e-mail é gravada como qualquer outra");
            achada!.Content.Should().Contain("sobre o e-mail");
            achada.MailThreadKey.Should().Be(chave);
        }

        [Fact]
        public async Task OTurnoDeEmail_NAO_ApareceNaListaDoPainel()
        {
            string chave = "eu@x.com|thr:" + Guid.NewGuid().ToString("N");

            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.VincularAEmail(chave);

            await foreach (var _ in conversation.StreamResponseAsync("sobre o e-mail", _ => { })) { }

            ChatHistoryService.ConversasDoUsuario()
                .Should().NotContain(c => c.MailThreadKey == chave);
        }

        [Fact]
        public async Task AConversaCOMUM_APARECE_NaListaDoPainel()
        {
            // O contrapeso. Se o filtro passasse a barrar tudo, o ensaio de cima continuaria
            // verde e a lista inteira teria sumido em silêncio.
            string marca = "conversa comum " + Guid.NewGuid().ToString("N");

            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            await foreach (var _ in conversation.StreamResponseAsync(marca, _ => { })) { }

            ChatHistoryService.ConversasDoUsuario()
                .Should().Contain(c => c.Content.Contains(marca));
        }

        // ──────────────────────────────────────────────────────────
        // /compact — um comando, os dois trabalhos
        // ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Compact_SemMaterial_EXPLICA_EmVezDeCalar()
        {
            // Um "não deu" sem motivo é pior que não ter o comando.
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            string resposta = await conversation.ForcarCompactacaoAsync(userLevel: 1);

            resposta.Should().NotBeNullOrWhiteSpace();
            resposta.ToLowerInvariant().Should().Contain("nada a compactar");
        }

        [Fact]
        public async Task Compact_ANUNCIA_EAcabaNaFaixa()
        {
            // Cada resumo é uma chamada ao modelo, e nesta máquina isso é minutos. Um comando
            // manual que congela a tela em silêncio seria o mesmo defeito com outro gatilho.
            var settings = BuildSettings(sendSystemPrompt: false);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            int anuncios = 0, fins = 0;
            conversation.CompactacaoAndou += _ => anuncios++;
            conversation.CompactacaoAcabou += () => fins++;

            await conversation.ForcarCompactacaoAsync(userLevel: 1);

            anuncios.Should().BeGreaterThan(0, "a faixa precisa aparecer");
            fins.Should().Be(1, "e precisa sumir uma vez, mesmo sem ter havido o que fazer");
        }
    
        // ─────────────────────────────────────────────────────────────────────
        // Simulação do primeiro envio — o botão "Imprimir o prompt" da aba Logs
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ASimulacao_BATE_ComOQueOPrimeiroTurnoMandaDeVerdade()
        {
            // A garantia inteira do botão. A simulação refaz os passos do turno em vez de
            // chamá-lo; se o turno ganhar um passo que ela não tem, é aqui que aparece.
            var settings = BuildSettings(sendSystemPrompt: true);
            var provider = new FakeProvider();
            var conversation = BuildConversation(settings, provider, out _);

            string simulado = conversation.SimularPrimeiroEnvio("oi");

            await foreach (var _ in conversation.StreamResponseAsync("oi")) { }

            var (mensagens, ferramentas, opcoes) = provider.PrimeiroStream!.Value;

            // O que o OllamaProvider faria com o que recebeu.
            string real = OllamaNativeClient.CorpoDaRequisicao(
                settings.LoadSettings().ModelName ?? "",
                mensagens,
                ferramentas.Count > 0 ? ferramentas : null,
                opcoes.Temperature,
                stream: true,
                opcoes.NumCtx,
                opcoes.KeepAliveSeconds ?? OllamaProvider.KeepAliveLockSeconds,
                opcoes.Think,
                opcoes.NumPredict);

            mensagens.Should().HaveCountGreaterThan(1, "senão não há prompt de sistema e o ensaio compara pouco");
            simulado.Should().Be(real);
        }

        [Fact]
        public void ASimulacao_NAO_TocaAConversaAberta()
        {
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);
            conversation.AppendAssistantText("FALA-DA-CONVERSA-ABERTA-7731");

            var antes = conversation.Snapshot().Select(TextOf).ToList();
            int tokensAntes = conversation.CountTokens();

            string simulado = conversation.SimularPrimeiroEnvio();

            // Conversa NOVA: o que foi dito na aberta não entra.
            simulado.Should().NotContain("FALA-DA-CONVERSA-ABERTA-7731");
            simulado.Should().Contain("\"content\":\"oi\"");

            conversation.Snapshot().Select(TextOf).Should().Equal(antes);
            conversation.CountTokens().Should().Be(tokensAntes);
        }

        [Fact]
        public void ASimulacao_USA_AsConfiguracoesSalvasAgora()
        {
            // A conversa aberta nasceu com prompt de sistema; desligar a chave depois vale para
            // a próxima conversa — que é justamente a que a simulação monta.
            var settings = BuildSettings(sendSystemPrompt: true);
            var conversation = BuildConversation(settings, new FakeProvider(), out _);

            conversation.SimularPrimeiroEnvio().Should().Contain("\"role\":\"system\"");

            var mudado = settings.LoadSettings();
            mudado.SendSystemPrompt = false;
            settings.SaveSettings(mudado);

            string simulado = conversation.SimularPrimeiroEnvio();
            simulado.Should().NotContain("\"role\":\"system\"");
            simulado.Should().Contain("\"role\":\"user\"");
        }

        [Fact]
        public void OPromptDeSistema_DIZ_ADataDeHoje()
        {
            // Sem a data, o modelo sem raciocínio inventou "12 de maio de 2024" em 2 de 3 (15/09).
            var conversation = BuildConversation(BuildSettings(sendSystemPrompt: true), new FakeProvider(), out _);

            TextOf(conversation.Snapshot()[0]).Should().Contain(DateTime.Now.ToString("dd/MM/yyyy"));
        }
}
}