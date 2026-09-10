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
    public class AgentLoopTests : IDisposable
    {
        private readonly string _dir;
        private readonly SettingsService _settings;

        public AgentLoopTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AIB_AgentLoopTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            _settings = new SettingsService(Path.Combine(_dir, "profile.dat"));
            _settings.SaveSettings(new UserAppSettings
            {
                AiProvider = "Ollama",
                ModelName = "modelo-de-teste",
                EnableIntelligentTools = true,
                SendSystemPrompt = true,
                MessageCount = 0
            });
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Prova de vida no terminal
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task OTurnoINTEIRO_DeixaRastroNoTerminal()
        {
            // Sem isto, um turno lento era silêncio absoluto: o [STREAM-END] só imprime quando
            // o stream acaba. O rastro tem de existir do envio ao fechamento, e o fechamento
            // precisa separar o prefill da geração — são dois custos com causas diferentes.
            var loop = BuildLoop(new ScriptedProvider(null, TextTurn("pronto")));
            var store = new RecordingStore();

            await DrainAsync(loop.RunAsync(Request(store), CancellationToken.None));

            string tudo;
            lock (_pulso) tudo = string.Join("\n", _pulso);

            tudo.Should().Contain("[TURNO 1]");
            tudo.Should().Contain("reuso previsto", "é o número que explica a duração da espera");
            tudo.Should().Contain("primeiro token em", "marca o fim do prefill");
            tudo.Should().Contain("respondeu", "e o fechamento diz como o turno acabou");
        }

        [Fact]
        public async Task ATrocaDeIteracao_APARECE_NoRastro()
        {
            // Um turno com ferramenta paga um prefill por iteração. Sem o número da iteração no
            // rastro, três esperas seguidas pareceriam uma só, muito mais longa.
            var loop = BuildLoop(new ScriptedProvider(
                null,
                ToolTurn(("k0", "id0", "read_file", "{\"path\":\"a.txt\"}")),
                TextTurn("li o arquivo")));

            var store = new RecordingStore();

            await DrainAsync(loop.RunAsync(Request(store), CancellationToken.None));

            string tudo;
            lock (_pulso) tudo = string.Join("\n", _pulso);

            tudo.Should().Contain("[TURNO 1]").And.Contain("[TURNO 2]");
            tudo.Should().Contain("ferramenta read_file — executando");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Dublês
        // ─────────────────────────────────────────────────────────────────────

        private sealed class ScriptedProvider : IChatProvider
        {
            private readonly Queue<IReadOnlyList<StreamChunk>> _script = new();
            private readonly IReadOnlyList<StreamChunk>? _fallback;

            public ScriptedProvider(IReadOnlyList<StreamChunk>? fallback = null, params IReadOnlyList<StreamChunk>[] turns)
            {
                _fallback = fallback;
                foreach (var t in turns) _script.Enqueue(t);
            }

            public int Calls { get; private set; }
            public List<IReadOnlyList<ChatTool>> ToolsSeen { get; } = new();
            public List<IReadOnlyList<ChatMessage>> MessagesSeen { get; } = new();

            public string Name => "Fake";
            public string Model => "fake";

            public async IAsyncEnumerable<StreamChunk> StreamAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                Calls++;
                ToolsSeen.Add(tools);
                MessagesSeen.Add(messages);

                var chunks = _script.Count > 0
                    ? _script.Dequeue()
                    : _fallback ?? new StreamChunk[] { new StreamChunk.Done(StreamFinishReason.Stop, "stop") };

                foreach (var c in chunks) yield return c;
            }

            public Task<ChatCompletionResult> CompleteAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                CancellationToken ct)
                => Task.FromResult(new ChatCompletionResult("ok", null, null));

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }

        private sealed class FixedProviderFactory : IChatProviderFactory
        {
            private readonly IChatProvider _provider;
            public FixedProviderFactory(IChatProvider provider) => _provider = provider;
            public IChatProvider GetProvider(UserAppSettings settings) => _provider;
        }

        /// <summary>Store de teste: registra a ordem exata das escritas e das podas.</summary>
        private sealed class RecordingStore : IMessageStore
        {
            public List<ChatMessage> Messages { get; } = new();
            public List<string> ToolResultOrder { get; } = new();
            public int TrimCalls { get; private set; }
            public int NotifyCalls { get; private set; }

            public IReadOnlyList<ChatMessage> Snapshot() => Messages.ToArray();

            public void AppendAssistantToolCalls(IReadOnlyList<ChatToolCall> calls, string? fala = null)
                => Messages.Add(ChatMessage.CreateAssistantMessage(calls));

            public void AppendToolResult(string toolCallId, string result)
            {
                ToolResultOrder.Add(toolCallId);
                Messages.Add(ChatMessage.CreateToolMessage(toolCallId, result));
            }

            public void AppendAssistantText(string text) => Messages.Add(ChatMessage.CreateAssistantMessage(text));

            public void Trim(int userLevel) => TrimCalls++;

            public int CountTokens() => 0;

            public void NotifyTokenCount(int userLevel, int? cachedTokens = null) => NotifyCalls++;
        }

        private static IReadOnlyList<StreamChunk> ToolTurn(params (string key, string id, string name, string args)[] calls)
        {
            var list = new List<StreamChunk>();
            foreach (var c in calls)
                list.Add(new StreamChunk.ToolCallDelta(c.key, c.id, c.name, c.args));
            list.Add(new StreamChunk.Done(StreamFinishReason.ToolCalls, "tool_calls"));
            return list;
        }

        private static IReadOnlyList<StreamChunk> TextTurn(string text) => new StreamChunk[]
        {
            new StreamChunk.TextDelta(text, TextChannel.Final),
            new StreamChunk.Done(StreamFinishReason.Stop, "stop")
        };

        /// <summary>Linhas do pulso do último laço montado. Diagnóstico dos ensaios.</summary>
        private readonly List<string> _pulso = new();

        private AgentLoop BuildLoop(IChatProvider provider)
            => new AgentLoop(new ToolRegistry(), new FixedProviderFactory(provider), _settings,
                             new TokenCounter(), linha => { lock (_pulso) _pulso.Add(linha); });

        private static AgentTurnRequest Request(IMessageStore store)
            => new AgentTurnRequest(store, Array.Empty<ChatTool>(), 1, ChatRequestOptions.Default, false);

        private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerable<AgentEvent> events)
        {
            var list = new List<AgentEvent>();
            await foreach (var e in events) list.Add(e);
            return list;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Testes
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Teto_DeIteracoes_TerminaComIterationLimitReached()
        {
            // Modelo teimoso: pede ferramenta em toda iteração e nunca responde.
            var provider = new ScriptedProvider(ToolTurn(("k0", "id0", "ferramenta_inexistente", "{}")));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            var completed = events.OfType<AgentEvent.Completed>().Single();
            completed.Outcome.Should().Be(TurnOutcome.IterationLimitReached);
            completed.IterationsUsed.Should().Be(AgentLoop.MaxIterations);
            provider.Calls.Should().Be(AgentLoop.MaxIterations);
        }

        [Fact]
        public async Task OTetoCONFIGURADO_MandaMaisQueOPadrao()
        {
            // O teto virou configuração. Se o laço continuasse lendo a constante, a chave
            // gravaria e não mudaria nada — e o aviso de corte anunciaria 18 para quem tivesse
            // pedido 4, errando justamente na frase que explica por que a resposta parou.
            var atual = _settings.LoadSettings();
            atual.MaxTurnIterations = 4;
            _settings.SaveSettings(atual);
            _settings.InvalidateCache();

            var provider = new ScriptedProvider(ToolTurn(("k0", "id0", "ferramenta_inexistente", "{}")));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            var completed = events.OfType<AgentEvent.Completed>().Single();
            completed.Outcome.Should().Be(TurnOutcome.IterationLimitReached);
            completed.IterationsUsed.Should().Be(4);
            provider.Calls.Should().Be(4, "quatro passos pedidos, quatro passos rodados");
        }

        [Fact]
        public async Task TurnoComFerramenta_TambemPodaOHistorico()
        {
            var provider = new ScriptedProvider(
                null,
                ToolTurn(("k0", "id0", "ferramenta_inexistente", "{}")),
                TextTurn("pronto"));
            var store = new RecordingStore();

            await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            // Uma poda na iteração de ferramenta e outra na de texto: antes o trim só era
            // alcançável no ramo de texto puro e uma conversa cheia de ferramentas crescia
            // sem limite.
            store.TrimCalls.Should().Be(2);
            store.NotifyCalls.Should().Be(2);
        }

        [Fact]
        public async Task ChamadasParalelas_PreservamAOrdemDeChegada()
        {
            var provider = new ScriptedProvider(
                null,
                ToolTurn(
                    ("k0", "id_a", "ferramenta_a", "{}"),
                    ("k1", "id_b", "ferramenta_b", "{}"),
                    ("k2", "id_c", "ferramenta_c", "{}")),
                TextTurn("fim"));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            store.ToolResultOrder.Should().Equal("id_a", "id_b", "id_c");

            var technical = events.OfType<AgentEvent.Technical>().Select(t => t.Value).ToList();
            technical.Should().Contain(v => v.Contains("[PARALELO] Executando 3 ferramentas em paralelo:"));
            technical.Should().Contain(v => v.StartsWith("[FERRAMENTA] Nome: ferramenta_a | Args: {}"));
        }

        [Fact]
        public async Task DeltasComMesmaChave_SaoUmaUnicaChamada()
        {
            var provider = new ScriptedProvider(
                null,
                new StreamChunk[]
                {
                    new StreamChunk.ToolCallDelta("oa:0", "id_a", "ferramenta_a", "{\"pa"),
                    new StreamChunk.ToolCallDelta("oa:0", null, null, "th\":\"x\"}"),
                    new StreamChunk.Done(StreamFinishReason.ToolCalls, "tool_calls")
                },
                TextTurn("fim"));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            store.ToolResultOrder.Should().Equal("id_a");
            events.OfType<AgentEvent.Technical>()
                .Should().Contain(t => t.Value.Contains("Args: {\"path\":\"x\"}"));
        }

        [Fact]
        public async Task TextoDeRaciocinio_NuncaViraRespostaNemFerramenta()
        {
            // Defeito antigo: o texto cru era varrido por regex, então uma ação escrita
            // DENTRO de <think> (e portanto rejeitada pelo modelo) era executada de verdade.
            // Agora o canal já chega classificado e o orquestrador não tem regex nenhuma.
            var provider = new ScriptedProvider(
                null,
                new StreamChunk[]
                {
                    new StreamChunk.TextDelta("Action: ferramenta_a(rm -rf)", TextChannel.Reasoning),
                    new StreamChunk.TextDelta("Nada a fazer.", TextChannel.Final),
                    new StreamChunk.Done(StreamFinishReason.Stop, "stop")
                });
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            store.ToolResultOrder.Should().BeEmpty();
            string userText = string.Concat(events.OfType<AgentEvent.Text>().Select(t => t.Value));
            userText.Should().Be("Nada a fazer.");
            events.OfType<AgentEvent.Completed>().Single().Outcome.Should().Be(TurnOutcome.Answered);
        }

        [Fact]
        public async Task Raciocinio_SaiComoEventoProprio_NaoComoTecnico()
        {
            // O raciocínio é o primeiro sinal de que o modelo está gerando, e chega muito
            // antes da primeira palavra. A tela usa isso para acender o indicador de
            // digitação; misturado aos logs de ferramenta no evento técnico, não havia como
            // distinguir "modelo pensando" de "orquestrador imprimindo linha de log".
            var provider = new ScriptedProvider(
                null,
                new StreamChunk[]
                {
                    new StreamChunk.TextDelta("hmm, deixa eu ver", TextChannel.Reasoning),
                    new StreamChunk.TextDelta("Pronto.", TextChannel.Final),
                    new StreamChunk.Done(StreamFinishReason.Stop, "stop")
                });
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            events.OfType<AgentEvent.Reasoning>().Select(r => r.Value)
                .Should().Equal("hmm, deixa eu ver");

            // E não pode vazar para o balão nem para o técnico.
            events.OfType<AgentEvent.Technical>()
                .Should().NotContain(t => t.Value.Contains("deixa eu ver"));
            string userText = string.Concat(events.OfType<AgentEvent.Text>().Select(t => t.Value));
            userText.Should().Be("Pronto.");
        }

        [Fact]
        public async Task StreamVazio_EmiteEmptyResponse()
        {
            var provider = new ScriptedProvider(
                null,
                new StreamChunk[] { new StreamChunk.Done(StreamFinishReason.Stop, "stop") });
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            events.OfType<AgentEvent.Completed>().Single().Outcome.Should().Be(TurnOutcome.EmptyResponse);
            store.TrimCalls.Should().Be(1);
        }

        [Fact]
        public async Task RaciocinioBruto_NaoVaiAoHistorico_NemAoUsuario()
        {
            // MUDOU DE POLITICA: o <think> ficava no historico sempre. Agora depende da chave
            // ThinkingInHistory, que nasce DESLIGADA — modelos de raciocinio sao treinados
            // esperando o bloco ausente do historico, e devolve-lo vai contra o treino.
            //
            // O usuario nunca viu o raciocinio e continua nao vendo: isso nao mudou.
            string historyText = await RaciocinioNoHistorico(devolver: false);

            historyText.Should().Be("São 4.");
            historyText.Should().NotContain("think");
        }

        [Fact]
        public async Task ComAChaveLigada_ORaciocinio_VOLTA_AoModelo()
        {
            string historyText = await RaciocinioNoHistorico(devolver: true);

            historyText.Should().Be("<think>vou somar</think>São 4.");
        }

        /// <summary>Roda um turno so de texto e devolve o que foi parar no historico.</summary>
        private async Task<string> RaciocinioNoHistorico(bool devolver)
        {
            var atual = _settings.LoadSettings();
            atual.ThinkingInHistory = devolver;
            _settings.SaveSettings(atual);
            _settings.InvalidateCache();

            var provider = new ScriptedProvider(
                null,
                new StreamChunk[]
                {
                    new StreamChunk.TextDelta("<think>vou somar</think>", TextChannel.Reasoning),
                    new StreamChunk.TextDelta("São 4.", TextChannel.Final),
                    new StreamChunk.Done(StreamFinishReason.Stop, "stop", "<think>vou somar</think>São 4.")
                });
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            string userText = string.Concat(events.OfType<AgentEvent.Text>().Select(t => t.Value));
            userText.Should().Be("São 4.", "o usuario nunca ve o canal de raciocinio");

            var appended = store.Messages.OfType<AssistantChatMessage>().Single();
            return string.Concat(appended.Content.Where(p => p?.Text != null).Select(p => p.Text));
        }

        [Fact]
        public async Task SemTextoCru_OHistoricoRecebeOCanalFinal()
        {
            var provider = new ScriptedProvider(null, TextTurn("resposta simples"));
            var store = new RecordingStore();

            await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            var appended = store.Messages.OfType<AssistantChatMessage>().Single();
            string historyText = string.Concat(appended.Content.Where(p => p?.Text != null).Select(p => p.Text));
            historyText.Should().Be("resposta simples");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Ida e volta da ferramenta, erro de provider, gating e cancelamento
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ResultadoDaFerramenta_VoltaParaOModeloNaIteracaoSeguinte()
        {
            // O ciclo ReAct só funciona se a ToolMessage entrar no histórico ANTES da
            // próxima chamada ao provider. Sem isso o modelo repete a mesma ferramenta.
            var provider = new ScriptedProvider(
                null,
                ToolTurn(("k0", "id0", "ferramenta_inexistente", "{}")),
                TextTurn("terminei"));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            provider.Calls.Should().Be(2);

            // A primeira iteração viu um histórico vazio; a segunda já viu a chamada e o resultado.
            provider.MessagesSeen[0].Should().BeEmpty();
            var secondTurn = provider.MessagesSeen[1];
            secondTurn.OfType<AssistantChatMessage>().Should().ContainSingle(m => m.ToolCalls.Count == 1);

            var toolMessage = secondTurn.OfType<ToolChatMessage>().Should().ContainSingle().Subject;
            toolMessage.ToolCallId.Should().Be("id0");
            string toolText = string.Concat(toolMessage.Content.Where(p => p?.Text != null).Select(p => p.Text));
            toolText.Should().NotBeNullOrWhiteSpace("o resultado da ferramenta é o que alimenta a próxima decisão");

            events.OfType<AgentEvent.Completed>().Single().Outcome.Should().Be(TurnOutcome.Answered);
        }

        [Fact]
        public async Task FerramentaComNomeDesconhecido_VoltaComoResultadoDeErro_ENaoDerrubaOTurno()
        {
            var provider = new ScriptedProvider(
                null,
                ToolTurn(("k0", "id0", "ferramenta_que_nao_existe", "{}")),
                TextTurn("entendi, não existe"));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            var toolMessage = store.Messages.OfType<ToolChatMessage>().Should().ContainSingle().Subject;
            string toolText = string.Concat(toolMessage.Content.Where(p => p?.Text != null).Select(p => p.Text));
            toolText.Should().StartWith("ERRO:");

            events.OfType<AgentEvent.Completed>().Single().Outcome.Should().Be(TurnOutcome.Answered);
        }

        [Fact]
        public async Task NivelInsuficiente_OModeloRecebeAcessoNegadoComoResultado()
        {
            var provider = new ScriptedProvider(
                null,
                ToolTurn(("k0", "id0", "read_file", "{\"path\":\"x\"}")),
                TextTurn("sem permissão então"));
            var store = new RecordingStore();

            var request = new AgentTurnRequest(store, Array.Empty<ChatTool>(), 0, ChatRequestOptions.Default, false);
            await DrainAsync(BuildLoop(provider).RunAsync(request, CancellationToken.None));

            var toolMessage = store.Messages.OfType<ToolChatMessage>().Should().ContainSingle().Subject;
            string toolText = string.Concat(toolMessage.Content.Where(p => p?.Text != null).Select(p => p.Text));
            toolText.Should().StartWith("ACESSO NEGADO");
        }

        [Fact]
        public async Task ProviderQueLanca_PropagaAExcecao_EmVezDeFingirSucesso()
        {
            var provider = new ThrowingProvider(new InvalidOperationException("modelo fora do ar"));
            var store = new RecordingStore();

            Func<Task> act = async () => await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            // Silenciar aqui produziria um turno "bem-sucedido" e vazio. A exceção sobe até a
            // ChatWindow, que já a transforma em balão de erro visível.
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("modelo fora do ar");
            store.Messages.Should().BeEmpty("um turno que falhou não pode deixar resposta parcial no histórico");
        }

        [Fact]
        public async Task ProviderQueLancaNaSegundaIteracao_NaoDesfazOQueJaFoiGravado()
        {
            var provider = new ThrowingProvider(
                new InvalidOperationException("caiu no meio"),
                throwOnCall: 2,
                firstTurn: ToolTurn(("k0", "id0", "ferramenta_inexistente", "{}")));
            var store = new RecordingStore();

            Func<Task> act = async () => await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            await act.Should().ThrowAsync<InvalidOperationException>();

            // A ida e volta da primeira iteração continua íntegra: assistant com tool_calls
            // seguido da ToolMessage de mesmo id. Um par quebrado invalida toda requisição seguinte.
            store.Messages.OfType<AssistantChatMessage>().Should().ContainSingle();
            store.Messages.OfType<ToolChatMessage>().Should().ContainSingle();
            store.ToolResultOrder.Should().Equal("id0");
        }

        [Fact]
        public async Task FerramentasDesligadas_OProviderNaoRecebeNenhumaDefinicao()
        {
            _settings.SaveSettings(new UserAppSettings
            {
                AiProvider = "Ollama",
                ModelName = "modelo-de-teste",
                EnableIntelligentTools = false,
                SendSystemPrompt = true
            });

            var provider = new ScriptedProvider(null, TextTurn("chat puro"));
            var store = new RecordingStore();

            var request = new AgentTurnRequest(
                store,
                new[] { FakeChatTool("read_file") },
                1,
                ChatRequestOptions.Default,
                false);

            await DrainAsync(BuildLoop(provider).RunAsync(request, CancellationToken.None));

            provider.ToolsSeen.Should().ContainSingle().Which.Should().BeEmpty();
        }

        [Fact]
        public async Task FerramentasLigadas_OProviderRecebeAsDefinicoesDoPedido()
        {
            var provider = new ScriptedProvider(null, TextTurn("ok"));
            var store = new RecordingStore();

            var request = new AgentTurnRequest(
                store,
                new[] { FakeChatTool("read_file") },
                1,
                ChatRequestOptions.Default,
                false);

            await DrainAsync(BuildLoop(provider).RunAsync(request, CancellationToken.None));

            provider.ToolsSeen.Should().ContainSingle()
                .Which.Select(t => t.FunctionName).Should().Equal("read_file");
        }

        [Fact]
        public async Task TokenCancelado_AntesDeComecar_NaoChamaOProvider()
        {
            var provider = new ScriptedProvider(null, TextTurn("nunca"));
            var store = new RecordingStore();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> act = async () => await DrainAsync(BuildLoop(provider).RunAsync(Request(store), cts.Token));

            await act.Should().ThrowAsync<OperationCanceledException>();
            provider.Calls.Should().Be(0);
        }

        [Fact]
        public async Task TokenCanceladoEntreIteracoes_InterrompeOLaco()
        {
            // Modelo teimoso: sem cancelamento rodaria as 18 iterações.
            var provider = new ScriptedProvider(ToolTurn(("k0", "id0", "ferramenta_inexistente", "{}")));
            var store = new RecordingStore();
            using var cts = new CancellationTokenSource();

            Func<Task> act = async () =>
            {
                await foreach (var e in BuildLoop(provider).RunAsync(Request(store), cts.Token))
                {
                    if (e is AgentEvent.Technical t && t.Value.Contains("Resultado")) cts.Cancel();
                }
            };

            await act.Should().ThrowAsync<OperationCanceledException>();
            provider.Calls.Should().BeLessThan(AgentLoop.MaxIterations);
        }

        [Fact]
        public async Task ChamadaSemArgumentos_ViraObjetoVazio_NuncaStringVazia()
        {
            // BinaryData.FromString("") produz tool_calls inválidas que o provider rejeita.
            var provider = new ScriptedProvider(
                null,
                new StreamChunk[]
                {
                    new StreamChunk.ToolCallDelta("k0", "id0", "ferramenta_inexistente", null),
                    new StreamChunk.Done(StreamFinishReason.ToolCalls, "tool_calls")
                },
                TextTurn("fim"));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            events.OfType<AgentEvent.Technical>()
                .Should().Contain(t => t.Value.Contains("Args: {}"));

            // O segundo turno tambem anexa um assistant (o texto final): interessa o primeiro.
            var assistant = store.Messages.OfType<AssistantChatMessage>()
                .First(m => m.ToolCalls.Count > 0);
            assistant.ToolCalls.Single().FunctionArguments.ToString().Should().Be("{}");
        }

        [Fact]
        public async Task Usage_ViraEventoDeContagemDeTokens()
        {
            var provider = new ScriptedProvider(
                null,
                new StreamChunk[]
                {
                    new StreamChunk.TextDelta("resposta", TextChannel.Final),
                    new StreamChunk.Usage(PromptEvalCount: 10, EvalCount: 4),
                    new StreamChunk.Done(StreamFinishReason.Stop, "stop")
                });
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            var usage = events.OfType<AgentEvent.TokenUsage>().Should().ContainSingle().Subject;
            usage.Max.Should().Be(LevelService.GetMaxTokensForLevel(1));
            usage.Total.Should().BeGreaterThan(0);
        }

        private static ChatTool FakeChatTool(string name) => ChatTool.CreateFunctionTool(
            functionName: name,
            functionDescription: "ferramenta de teste",
            functionParameters: BinaryData.FromString(
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}"));

        /// <summary>Provider que estoura numa chamada escolhida, para testar propagação de erro.</summary>
        private sealed class ThrowingProvider : IChatProvider
        {
            private readonly Exception _exception;
            private readonly int _throwOnCall;
            private readonly IReadOnlyList<StreamChunk>? _firstTurn;
            private int _calls;

            public ThrowingProvider(Exception exception, int throwOnCall = 1, IReadOnlyList<StreamChunk>? firstTurn = null)
            {
                _exception = exception;
                _throwOnCall = throwOnCall;
                _firstTurn = firstTurn;
            }

            public string Name => "Throwing";
            public string Model => "throwing";

            public async IAsyncEnumerable<StreamChunk> StreamAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                _calls++;
                if (_calls >= _throwOnCall) throw _exception;
                foreach (var c in _firstTurn ?? Array.Empty<StreamChunk>()) yield return c;
            }

            public Task<ChatCompletionResult> CompleteAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                CancellationToken ct) => throw _exception;

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }


        /// <summary>
        /// Provider que imita o OllamaProvider real diante do cancelamento: para de ler e
        /// encerra o stream com Done, SEM lançar. É exatamente o que o provider de produção
        /// faz — o laço do OllamaNativeClient testa <c>ct.IsCancellationRequested</c> e dá
        /// <c>break</c>, e nem ele nem <c>WithCancellation</c> lançam por conta própria.
        /// </summary>
        private sealed class GracefulCancelProvider : IChatProvider
        {
            public string Name => "Graceful";
            public string Model => "graceful";
            public int Calls { get; private set; }

            public async IAsyncEnumerable<StreamChunk> StreamAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                Calls++;
                for (int i = 0; i < 100; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    yield return new StreamChunk.TextDelta("parte ", TextChannel.Final);
                }
                yield return new StreamChunk.Done(StreamFinishReason.Stop, "stop");
            }

            public Task<ChatCompletionResult> CompleteAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                CancellationToken ct) => Task.FromResult(new ChatCompletionResult("", null, null));

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }

        [Fact]
        public async Task CancelamentoNoMeioDoStream_NaoPodeVirarSucessoSilencioso()
        {
            var provider = new GracefulCancelProvider();
            var store = new RecordingStore();
            using var cts = new CancellationTokenSource();

            var events = new List<AgentEvent>();

            Func<Task> act = async () =>
            {
                await foreach (var e in BuildLoop(provider).RunAsync(Request(store), cts.Token))
                {
                    events.Add(e);
                    if (events.Count == 3) cts.Cancel();
                }
            };

            await act.Should().ThrowAsync<OperationCanceledException>(
                "parar a geração não é o modelo ter respondido: a ChatWindow precisa distinguir os dois");

            events.OfType<AgentEvent.Completed>()
                .Should().NotContain(c => c.Outcome == TurnOutcome.Answered,
                    "uma resposta truncada por Stop não pode ser reportada como resposta completa");
        }

        [Fact]
        public async Task CancelamentoNoMeioDoStream_NaoTravaEEncerraOTurno()
        {
            // Companheiro ATIVO do teste acima: independentemente do defeito de classificação,
            // o cancelamento tem de cortar o stream em vez de deixar o turno rodando.
            var provider = new GracefulCancelProvider();
            var store = new RecordingStore();
            using var cts = new CancellationTokenSource();

            var events = new List<AgentEvent>();
            try
            {
                await foreach (var e in BuildLoop(provider).RunAsync(Request(store), cts.Token))
                {
                    events.Add(e);
                    if (events.Count == 3) cts.Cancel();
                }
            }
            catch (OperationCanceledException)
            {
                // Comportamento desejado; hoje não acontece. Ver o teste com Skip acima.
            }

            events.OfType<AgentEvent.Text>().Should().HaveCountLessThan(100,
                "o token tem que interromper a emissão de texto");
            provider.Calls.Should().Be(1, "o laço não pode partir para uma nova iteração após o cancelamento");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Cadência do contador de tokens (regressão do refactor)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Stream longo SEM nenhum chunk de Usage — que é o caso real: Ollama e OpenAI só
        /// reportam contadores de uso na última linha do stream.
        /// </summary>
        private static IReadOnlyList<StreamChunk> LongTextTurn(int deltas)
        {
            var list = new List<StreamChunk>();
            for (int i = 0; i < deltas; i++)
                list.Add(new StreamChunk.TextDelta($"palavra{i} ", TextChannel.Final));
            list.Add(new StreamChunk.Done(StreamFinishReason.Stop, "stop"));
            return list;
        }

        [Fact]
        public async Task ContadorDeTokens_AtualizaDuranteOStream_NaoSoNoFim()
        {
            // REGRESSÃO: o AgentLoop emitia TokenUsage apenas dentro do ramo StreamChunk.Usage.
            // Como os providers só mandam Usage no fim, o contador da UI ficava parado a resposta
            // inteira e pulava de uma vez no encerramento.
            const int deltas = 50;
            var provider = new ScriptedProvider(null, LongTextTurn(deltas));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));
            var usages = events.OfType<AgentEvent.TokenUsage>().ToList();

            int esperadoMinimo = deltas / AgentLoop.TokenUiRefreshEveryChunks;
            usages.Count.Should().BeGreaterThanOrEqualTo(esperadoMinimo,
                "o contador precisa ser reemitido a cada TokenUiRefreshEveryChunks chunks, não só no fim");

            // Chega antes do fim do turno: prova que é ao vivo, não um flush terminal.
            int primeiroUsage = events.FindIndex(e => e is AgentEvent.TokenUsage);
            int ultimoTexto = events.FindLastIndex(e => e is AgentEvent.Text);
            primeiroUsage.Should().BeLessThan(ultimoTexto,
                "a primeira atualização tem de acontecer com texto ainda chegando");

            // E o total cresce junto com o texto acumulado, em vez de repetir o mesmo número.
            usages.Select(u => u.Total).Should().BeInAscendingOrder();
            usages.Select(u => u.Total).Distinct().Count().Should().BeGreaterThan(1,
                "um contador que repete o mesmo valor não está medindo o texto que chegou");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Fronteira de fala (multi-balão)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Turno que fala ANTES de pedir a ferramenta — o caso do multi-balão.</summary>
        private static IReadOnlyList<StreamChunk> FalaEToolTurn(string fala, string key, string id, string nome)
            => new StreamChunk[]
            {
                new StreamChunk.TextDelta(fala, TextChannel.Final),
                new StreamChunk.ToolCallDelta(key, id, nome, "{}"),
                new StreamChunk.Done(StreamFinishReason.ToolCalls, "tool_calls")
            };

        [Fact]
        public async Task FalaSeguidaDeFerramenta_FechaOSegmentoAntesDeAnunciarAFerramenta()
        {
            var provider = new ScriptedProvider(
                TextTurn("Pronto."),
                FalaEToolTurn("Vou ler o arquivo.", "k0", "id0", "ferramenta_inexistente"));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            int segmento = events.FindIndex(e => e is AgentEvent.TurnSegment);
            int anuncioFerramenta = events.FindIndex(
                e => e is AgentEvent.Technical t && t.Value.Contains("[FERRAMENTA] Nome:"));

            segmento.Should().BeGreaterThanOrEqualTo(0, "a fala terminou e o agente partiu para a ferramenta");
            segmento.Should().BeLessThan(anuncioFerramenta,
                "o balão fecha ANTES do indicador de ferramenta, senão o 🔧 aparece dentro da fala");
        }

        [Fact]
        public async Task IteracaoSoDeFerramenta_NaoAbreBalaoVazio()
        {
            // Modelo que chama a ferramenta sem dizer nada antes: não há fala para fechar.
            var provider = new ScriptedProvider(
                TextTurn("Pronto."),
                ToolTurn(("k0", "id0", "ferramenta_inexistente", "{}")));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            events.OfType<AgentEvent.TurnSegment>().Should().BeEmpty(
                "sem texto acumulado não existe balão a fechar");
        }

        [Fact]
        public async Task TurnoSemFerramenta_NaoEmiteFronteira()
        {
            var provider = new ScriptedProvider(null, TextTurn("Resposta direta."));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            events.OfType<AgentEvent.TurnSegment>().Should().BeEmpty(
                "uma resposta de um só fôlego continua sendo um balão só");
        }

        [Fact]
        public async Task ContadorDeTokens_SempreFechaOTurnoComOValorExato()
        {
            // A amostragem por chunk deixa resto: 25 deltas com cadência 10 pararia em 20.
            // O turno tem de terminar com uma emissão final, senão a UI congela num valor parcial.
            var provider = new ScriptedProvider(null, LongTextTurn(25));
            var store = new RecordingStore();

            var events = await DrainAsync(BuildLoop(provider).RunAsync(Request(store), CancellationToken.None));

            int ultimoUsage = events.FindLastIndex(e => e is AgentEvent.TokenUsage);
            int ultimoTexto = events.FindLastIndex(e => e is AgentEvent.Text);
            ultimoUsage.Should().BeGreaterThan(ultimoTexto,
                "a última emissão do contador vem depois do último texto, fechando com o valor exato");
        }
    }
}
