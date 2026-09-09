using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services.Ai;
using OpenAI.Chat;

namespace AIB.Services.Agent;

/// <summary>
/// Orquestração ReAct. Não conhece HTTP, não faz parsing de stream, não remove token de
/// template e não roda regex — só decide o que fazer com os StreamChunks que o provider
/// entrega já classificados. Não tem campo mutável: tudo de um turno vive em locais do
/// iterador, então dois turnos simultâneos não se enxergam.
/// </summary>
public sealed class AgentLoop
{
    /// <summary>
    /// Teto de iterações ReAct — o PADRÃO, hoje ajustável em Avançado. 18 permite tarefas
    /// multi-passo (antes eram 5, restritivo demais: "ler 6 arquivos antes de decidir" já
    /// abortava). Com gemma4:e2b a coerência se mantém por cerca de 20 iterações.
    /// </summary>
    public const int MaxIterations = UserAppSettings.PadraoDeIteracoes;

    /// <summary>
    /// De quantos em quantos chunks o contador de tokens da UI é reemitido durante o stream.
    /// Recontar a cada chunk custaria uma tokenização por token emitido; amostrar mantém o
    /// contador vivo sem esse custo. Mesma cadência do comportamento anterior ao refactor.
    /// </summary>
    public const int TokenUiRefreshEveryChunks = 10;

    private static readonly IReadOnlyList<ChatTool> NoTools = Array.Empty<ChatTool>();

    private readonly ToolRegistry _toolRegistry;
    private readonly IChatProviderFactory _providerFactory;
    private readonly SettingsService _settingsService;
    private readonly TokenCounter _tokenCounter;

    /// <summary>
    /// Estado entre turnos: guarda o prompt anterior para medir o prefixo reaproveitável.
    /// O AgentLoop é de vida longa (uma instância na raiz de composição) e a ConversationService
    /// serializa os turnos, então este acumulador não é tocado por duas gerações ao mesmo tempo.
    /// </summary>
    private readonly PromptPrefixTracker _prefixTracker;

    /// <summary>
    /// Para onde o <see cref="PulsoDoTurno"/> escreve. Injetável para o ensaio poder ler as
    /// linhas em vez de depender do console do processo de teste.
    /// </summary>
    private readonly Action<string>? _escreverPulso;

    /// <param name="tokenCounter">
    /// Contador usado para estimar os tokens já gerados no stream. Opcional para manter a
    /// assinatura do contrato válida; quem compõe o app passa a instância única.
    /// </param>
    public AgentLoop(
        ToolRegistry toolRegistry,
        IChatProviderFactory providerFactory,
        SettingsService settingsService,
        TokenCounter? tokenCounter = null,
        Action<string>? escreverPulso = null)
    {
        _escreverPulso = escreverPulso;
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _tokenCounter = tokenCounter ?? new TokenCounter();
        _prefixTracker = new PromptPrefixTracker(_tokenCounter);
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var store = request.Store;
        int maxTokens = LevelService.GetMaxTokensForLevel(request.UserLevel);

        // O teto é lido UMA vez, antes do laço. Reler a cada iteração deixaria o limite mudar
        // no meio de um turno se o usuário salvasse as configurações — um turno com regra
        // trocada na metade é pior de depurar que um turno com a regra velha.
        int teto = _settingsService.LoadSettings().MaxTurnIterations;

        for (int iteration = 1; iteration <= teto; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            // Uma leitura de settings por iteração, servida do cache do SettingsService.
            var settings = _settingsService.LoadSettings();
            var provider = _providerFactory.GetProvider(settings);

            // Setting "EnableIntelligentTools": desliga todas as tool defs quando o usuário
            // quer chat puro/rápido (cada tool infla a gramática JSON e o custo de prefill).
            var tools = settings.EnableIntelligentTools ? request.Tools : NoTools;

            var messages = store.Snapshot();

            var finalText = new StringBuilder();
            string? rawAssistantText = null;
            var calls = new List<ToolCallAccumulator>();
            var callsByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            int baselineTokens = store.CountTokens();
            int chunkCount = 0;
            int? lastCachedTokens = null;
            int? lastPromptEvalCount = null;
            double? lastPromptEvalMillis = null;

            // Quanto deste prompt o KV cache do provider deve reaproveitar. Calculado por nós
            // porque o Ollama não reporta cache; o OpenAI reporta e tem prioridade.
            int predictedCached = _prefixTracker.RecordAndGetReusableTokens(messages);

            // PROVA DE VIDA no terminal. Um prefill frio de ~3500 tokens passa de três minutos
            // nesta máquina, e nesse intervalo não há chunk, evento nem linha de log: por fora
            // é indistinguível de travamento. O pulso bate a cada poucos segundos dizendo em que
            // fase o turno está e há quanto tempo.
            using var pulso = new PulsoDoTurno(
                iteration,
                provider.Model,
                baselineTokens,
                predictedCached,
                request.Options.NumCtx,
                escrever: _escreverPulso);

            await foreach (var chunk in provider
                .StreamAsync(messages, tools, request.Options, ct)
                .WithCancellation(ct)
                .ConfigureAwait(false))
            {
                // WithCancellation só repassa o token ao iterador: um provider que encerra o stream
                // graciosamente no cancelamento (break em vez de throw) não levanta nada, e o turno
                // viraria sucesso com o texto truncado. A checagem tem de ser aqui dentro.
                ct.ThrowIfCancellationRequested();

                if (chunk is StreamChunk.TextDelta text)
                {
                    if (text.Channel == TextChannel.Final)
                    {
                        pulso.Escreveu(text.Text.Length);
                        finalText.Append(text.Text);
                        yield return new AgentEvent.Text(text.Text);
                    }
                    else
                    {
                        pulso.Raciocinou(text.Text.Length);
                        yield return new AgentEvent.Reasoning(text.Text);
                    }
                }
                else if (chunk is StreamChunk.ToolCallDelta delta)
                {
                    // Turno que só chama ferramenta não emite texto nenhum: sem esta linha o
                    // prefill dele nunca seria dado por encerrado no pulso.
                    pulso.PrimeiroToken();

                    // Agregação por CallKey, em ordem de primeira aparição. A chave é opaca:
                    // quem decide o que é uma chamada distinta é o provider.
                    if (!callsByKey.TryGetValue(delta.CallKey, out int position))
                    {
                        position = calls.Count;
                        callsByKey[delta.CallKey] = position;
                        calls.Add(new ToolCallAccumulator());
                    }

                    var entry = calls[position];
                    if (string.IsNullOrEmpty(entry.Id) && !string.IsNullOrEmpty(delta.ToolCallId))
                        entry.Id = delta.ToolCallId!;
                    if (string.IsNullOrEmpty(entry.Name) && !string.IsNullOrEmpty(delta.FunctionName))
                        entry.Name = delta.FunctionName!;
                    if (delta.ArgumentsJsonFragment != null)
                        entry.Args.Append(delta.ArgumentsJsonFragment);
                }
                else if (chunk is StreamChunk.Usage usage)
                {
                    // Guarda o último uso reportado. Ollama e OpenAI só mandam estes contadores
                    // no fim do stream, então eles NÃO podem ser o gatilho da atualização da UI.
                    lastCachedTokens = usage.CachedTokens ?? lastCachedTokens;
                    lastPromptEvalCount = usage.PromptEvalCount ?? lastPromptEvalCount;
                    lastPromptEvalMillis = usage.PromptEvalMillis ?? lastPromptEvalMillis;
                }
                else if (chunk is StreamChunk.Done done)
                {
                    rawAssistantText = done.RawAssistantText;
                    break;
                }

                // Contador de tokens ao vivo: recontar a cada chunk custaria uma tokenização por
                // token emitido, então amostra a cada TokenUiRefreshEveryChunks, como antes.
                chunkCount++;
                if (chunkCount % TokenUiRefreshEveryChunks == 0)
                    yield return BuildTokenUsage();
            }

            // Amostragem sempre deixa um resto: fecha o turno com o valor exato.
            yield return BuildTokenUsage();

            AgentEvent.TokenUsage BuildTokenUsage()
            {
                int streamedTokens = _tokenCounter.CountText(finalText.ToString());
                for (int i = 0; i < calls.Count; i++)
                    streamedTokens += _tokenCounter.CountText(calls[i].Args.ToString());

                int total = baselineTokens + streamedTokens;

                // Cache reportado pelo provider tem prioridade — quando ele sabe, sabe melhor.
                // Sem relato (Ollama), cai na nossa previsão de prefixo, conferida contra o
                // custo real do prefill. Null se propaga como null: "não sei" não vira zero.
                int? cached = lastCachedTokens
                    ?? _prefixTracker.ConfirmOrDiscard(predictedCached, lastPromptEvalCount, lastPromptEvalMillis);

                return new AgentEvent.TokenUsage(total, maxTokens, cached);
            }

            // Cancelamento sinalizado durante o último chunk (ou entre o Done e aqui): sem isto o
            // turno seguiria para a gravação no histórico e reportaria Answered.
            ct.ThrowIfCancellationRequested();

            // ── Ferramentas pedidas: executa e volta para o modelo ────────────────
            if (calls.Count > 0)
            {
                // Mensagem assistant com TODAS as tool_calls de uma vez: cada id precisa
                // estar referenciado por uma ToolMessage de mesmo id logo em seguida.
                var chatToolCalls = calls
                    .Select(tc => ChatToolCall.CreateFunctionToolCall(
                        tc.Id,
                        tc.Name,
                        BinaryData.FromString(tc.ArgumentsOrEmpty())))
                    .ToList();
                store.AppendAssistantToolCalls(chatToolCalls);

                // Fecha a fala do agente ANTES de anunciar as ferramentas: o que ele disse até
                // aqui ("vou ler o arquivo") é uma fala completa, e o que vier depois da execução
                // é outra. Só emite quando houve texto — iteração puramente de ferramenta não
                // gera balão vazio.
                if (finalText.Length > 0)
                    yield return new AgentEvent.TurnSegment(iteration);

                if (calls.Count > 1)
                    yield return new AgentEvent.Technical($"\n[PARALELO] Executando {calls.Count} ferramentas em paralelo:\n");

                // Todas anunciadas ANTES de a primeira executar: elas rodam em paralelo logo
                // abaixo, e a cadeia de ações precisa mostrar as que estão em curso juntas.
                foreach (var tc in calls)
                {
                    pulso.FerramentaComecou(tc.Name);
                    yield return new AgentEvent.Technical($"[FERRAMENTA] Nome: {tc.Name} | Args: {tc.ArgumentsOrEmpty()}\n");
                    yield return new AgentEvent.ToolStarted(tc.Id, tc.Name, tc.ArgumentsOrEmpty());
                }

                // EXECUÇÃO PARALELA: dispara todas e espera o conjunto. Em CPU lenta, três
                // leituras de arquivo independentes rodam concorrentes em vez de seriadas.
                var results = await Task.WhenAll(
                    calls.Select(tc => ExecuteToolPairedAsync(tc, request.UserLevel, pulso))).ConfigureAwait(false);

                // Resultados na ordem original, mesmo que tenham terminado fora de ordem.
                foreach (var (tc, result) in results)
                {
                    pulso.FerramentaTerminou(tc.Name, Memory.ArtifactExtractor.Falhou(result));
                    yield return new AgentEvent.Technical($"[FERRAMENTA] Resultado ({tc.Name}): {result}\n");

                    // O literal sai do MESMO extrator que alimenta a memória. Dois extratores
                    // fariam a bolha e o capítulo discordarem sobre o que foi feito.
                    var artefato = Memory.ArtifactExtractor.Construir(tc.Name, tc.ArgumentsOrEmpty(), result);

                    // O fracasso vem do resultado, não do artefato: ferramenta sem extrator
                    // próprio devolve artefato nulo mesmo quando deu erro.
                    yield return new AgentEvent.ToolFinished(
                        tc.Id, tc.Name, Memory.ArtifactExtractor.Falhou(result), artefato, result);

                    store.AppendToolResult(tc.Id, result);
                    TrackRecentFile(tc);
                    if (tc.Name == "materialize_skill") _toolRegistry.Refresh();
                }

                store.Trim(request.UserLevel);
                store.NotifyTokenCount(request.UserLevel);
                pulso.Fim($"ferramenta(s) executada(s), voltando ao modelo (iteração {iteration + 1})");
                continue;
            }

            // ── Resposta final em texto ───────────────────────────────────────────
            if (finalText.Length > 0)
            {
                // Preserva blocos <think>...</think> no HISTÓRICO: o modelo enxerga o próprio
                // raciocínio nas iterações seguintes. Para o usuário eles nunca aparecem —
                // o provider já os classificou como canal de raciocínio e só o canal final
                // virou AgentEvent.Text. Sem texto cru (provider que não o publica), cai no
                // canal final, que é o que o usuário viu.
                store.AppendAssistantText(
                    string.IsNullOrEmpty(rawAssistantText) ? finalText.ToString() : rawAssistantText!);
                store.Trim(request.UserLevel);
                store.NotifyTokenCount(request.UserLevel);
                pulso.Fim("respondeu", _tokenCounter.CountText(finalText.ToString()));
                yield return new AgentEvent.Completed(TurnOutcome.Answered, iteration);
                yield break;
            }

            // ── Stream vazio: sem texto e sem ferramenta ──────────────────────────
            store.Trim(request.UserLevel);
            store.NotifyTokenCount(request.UserLevel);
            pulso.Fim("resposta VAZIA");
            yield return new AgentEvent.Completed(TurnOutcome.EmptyResponse, iteration);
            yield break;
        }

        // Teto estourado com o modelo ainda pedindo ferramentas. Nunca um sucesso silencioso.
        // Poda aqui também: esta é justamente a saída em que o histórico ficou maior, com 18
        // rodadas de tool_calls e resultados acumulados. Sem isto a próxima mensagem parte de um
        // histórico não podado e com o contador de tokens da UI defasado.
        request.Store.Trim(request.UserLevel);
        request.Store.NotifyTokenCount(request.UserLevel);
        yield return new AgentEvent.Completed(TurnOutcome.IterationLimitReached, teto);
    }

    private async Task<(ToolCallAccumulator Tc, string Result)> ExecuteToolPairedAsync(
        ToolCallAccumulator tc, int userLevel, PulsoDoTurno pulso)
    {
        // O pulso recebe a espera humana POR FERRAMENTA, e não um total do turno: elas rodam em
        // paralelo, e um total não teria como dizer qual delas ficou parada no modal.
        string result = await _toolRegistry
            .ExecuteToolAsync(tc.Name, tc.ArgumentsOrEmpty(), userLevel,
                              ms => pulso.EsperaHumana(tc.Name, ms))
            .ConfigureAwait(false);
        return (tc, result);
    }

    /// <summary>Mantém a lista de arquivos recentes acessados pelo agente.</summary>
    private static void TrackRecentFile(ToolCallAccumulator tc)
    {
        if (tc.Name != "read_file" && tc.Name != "view_file" && tc.Name != "write_to_file"
            && tc.Name != "replace_file_content" && tc.Name != "multi_replace_file_content")
            return;

        try
        {
            var dict = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(tc.ArgumentsOrEmpty());
            string? path = null;
            if (dict != null && dict.ContainsKey("AbsolutePath")) path = dict["AbsolutePath"].GetString();
            else if (dict != null && dict.ContainsKey("TargetFile")) path = dict["TargetFile"].GetString();
            else if (dict != null && dict.ContainsKey("path")) path = dict["path"].GetString();

            if (!string.IsNullOrEmpty(path)) ContextService.AddRecentFile(path);
        }
        catch { }
    }

    /// <summary>
    /// Agregador dos fragmentos de uma única tool call. Id e nome só chegam no primeiro
    /// fragmento; os argumentos chegam em pedaços e são concatenados na ordem de chegada.
    /// </summary>
    private sealed class ToolCallAccumulator
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Args = new();

        public string ArgumentsOrEmpty()
        {
            string args = Args.ToString();
            return string.IsNullOrWhiteSpace(args) ? "{}" : args;
        }
    }
}
