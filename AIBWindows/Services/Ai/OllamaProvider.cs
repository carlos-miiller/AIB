using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Ai;

/// <summary>
/// Provider do Ollama nativo (/api/chat NDJSON). Concentra tudo que é específico do
/// Ollama: payload, classificação de canal, agregação de tool calls e o healer por regex.
/// Nunca toca a lista de mensagens recebida e nunca volta para o Dispatcher.
/// </summary>
public sealed class OllamaProvider : IChatProvider
{
    /// <summary>
    /// keep_alive padrão das requisições de chat. -1 mantém o modelo travado na VRAM.
    /// Omitir o campo faz o Ollama reaplicar o default de 5 minutos e desfazer a trava
    /// feita pelo aquecimento — por isso o provider envia -1 quando ninguém pediu outro valor.
    /// </summary>
    private const int KeepAliveLockSeconds = -1;

    private readonly OllamaNativeClient _client;
    private readonly string _baseUrl;
    private readonly IToolCallHealer _healer;
    private readonly HttpClient _httpClient;
    private readonly bool _verboseLogging;

    /// <param name="client">Cliente de transporte já configurado com a URL base.</param>
    /// <param name="baseUrl">URL base normalizada (sem /v1), usada pelo warmup /api/generate.</param>
    /// <param name="model">Nome do modelo.</param>
    /// <param name="healer">Curador de tool calls escritas como texto.</param>
    /// <param name="httpClient">HttpClient compartilhado, para o POST de warmup.</param>
    /// <param name="verboseLogging">Espelha UserAppSettings.VerboseConsoleLogging.</param>
    public OllamaProvider(
        OllamaNativeClient client,
        string baseUrl,
        string model,
        IToolCallHealer healer,
        HttpClient httpClient,
        bool verboseLogging)
    {
        _client = client;
        _baseUrl = baseUrl.Replace("/v1", "").TrimEnd('/');
        Model = model;
        _healer = healer;
        _httpClient = httpClient;
        _verboseLogging = verboseLogging;
    }

    public string Name => "Ollama";

    public string Model { get; }

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var splitter = new ChannelSplitter();

        // Contador local ao iterador: o Ollama manda a tool call inteira dentro de UMA linha
        // NDJSON e reinicia o índice do array a cada linha. A chave de agregação precisa ser
        // única no stream inteiro, senão duas chamadas distintas viram uma só.
        int emittedCalls = 0;

        bool anyToolCall = false;
        string? rawFinish = null;
        int updateCount = 0;
        int updatesWithContent = 0;
        int updatesWithTools = 0;
        int updatesEmpty = 0;
        int updatesWithThinking = 0;
        int rawTextChars = 0;
        bool firstUpdateLogged = false;

        var stream = _client.StreamChatAsync(
            Model,
            messages,
            tools.Count > 0 ? tools : null,
            options.Temperature,
            _verboseLogging,
            ct,
            options.NumCtx,
            options.KeepAliveSeconds ?? KeepAliveLockSeconds,
            options.Think,
            options.NumPredict);

        await foreach (var update in stream.WithCancellation(ct).ConfigureAwait(false))
        {
            updateCount++;
            if (!string.IsNullOrEmpty(update.FinishReason)) rawFinish = update.FinishReason;

            if (_verboseLogging)
            {
                int contentParts = string.IsNullOrEmpty(update.Text) ? 0 : 1;
                int toolUpdates = update.ToolCallUpdates?.Count ?? 0;
                int thinkingParts = string.IsNullOrEmpty(update.Thinking) ? 0 : 1;
                if (contentParts > 0) { updatesWithContent++; rawTextChars += update.Text!.Length; }
                if (toolUpdates > 0) updatesWithTools++;
                if (thinkingParts > 0) updatesWithThinking++;

                // "Vazio" só quando o update não trouxe NADA. Antes o thinking não era contado
                // e um modelo de raciocínio reportava "empty=107" para 107 chunks que estavam
                // cheios — o diagnóstico apontava para o lugar errado.
                if (contentParts == 0 && toolUpdates == 0 && thinkingParts == 0) updatesEmpty++;

                if (!firstUpdateLogged && (contentParts > 0 || toolUpdates > 0))
                {
                    var dbg = new StringBuilder();
                    dbg.Append($"[STREAM-DBG] update#{updateCount}: contentParts={contentParts} toolUpdates={toolUpdates}");
                    if (contentParts > 0)
                    {
                        string preview = update.Text!.Substring(0, Math.Min(40, update.Text!.Length)).Replace("\n", "\\n");
                        dbg.Append($" | part0 Kind=text TextLen={update.Text!.Length} \"{preview}\"");
                    }
                    Console.WriteLine(dbg.ToString());
                    firstUpdateLogged = true;
                }
            }

            // Raciocínio pelo campo separado ANTES do content: é a ordem em que o modelo produz,
            // e o content chega vazio enquanto ele pensa.
            if (!string.IsNullOrEmpty(update.Thinking))
            {
                foreach (var delta in splitter.PushThinking(update.Thinking!))
                    yield return delta;
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                foreach (var delta in splitter.Push(update.Text!))
                    yield return delta;
            }

            if (update.ToolCallUpdates != null)
            {
                foreach (var tc in update.ToolCallUpdates)
                {
                    anyToolCall = true;
                    yield return new StreamChunk.ToolCallDelta(
                        "ol:" + emittedCalls++,
                        tc.ToolCallId,
                        tc.FunctionName,
                        tc.FunctionArgumentsUpdate);
                }
            }

            // CachedTokens fica null de propósito: o Ollama não expõe quanto do prompt foi
            // reaproveitado. 'prompt_eval_count' é o tamanho do prompt e não se move com o
            // cache — medido com 3485 tokens: 204s no prefill frio, 343ms no cacheado, e o
            // contador devolveu 3485 nos dois. Derivar cache dele produzia número negativo
            // (os contadores ainda vêm de tokenizadores diferentes), que a UI clampava em 0
            // e exibia como "-0%". Enquanto não houver sinal real, a UI não mostra nada.
            if (update.PromptEvalCount.HasValue || update.EvalCount.HasValue)
                yield return new StreamChunk.Usage(
                    update.PromptEvalCount, update.EvalCount, null, update.PromptEvalMillis);
        }

        foreach (var delta in splitter.Flush(anyToolCall))
            yield return delta;

        if (_verboseLogging)
        {
            Console.WriteLine($"[STREAM-END] updates={updateCount} (content={updatesWithContent} think={updatesWithThinking} tools={updatesWithTools} empty={updatesEmpty}) rawText={rawTextChars}ch finish={rawFinish ?? "none"} mode={splitter.Mode} yielded={splitter.AnythingEmittedAsFinal} calls={emittedCalls}");
        }
        else
        {
            Console.WriteLine($"[STREAM-END] updates={updateCount} finish={rawFinish ?? "none"} mode={splitter.Mode} full={splitter.FinalText.Length}ch tools={emittedCalls} yielded={splitter.AnythingEmittedAsFinal}");
        }

        // Healer: só quando o modelo NÃO usou function calling nativo e há ferramentas ativas.
        // Recebe apenas o texto do canal final — conteúdo de <think> nunca chega aqui.
        if (!anyToolCall && tools.Count > 0 && _healer.TryHeal(splitter.FinalText, tools, out var healed))
        {
            yield return new StreamChunk.TextDelta(
                $"\n[FALLBACK REGEX] Ação em texto detectada: {healed.MatchedText}\n",
                TextChannel.Reasoning);

            yield return new StreamChunk.ToolCallDelta(
                "heal:0",
                "fallback_" + Guid.NewGuid().ToString("N"),
                healed.ToolName,
                healed.ArgumentsJson);

            yield return new StreamChunk.Done(StreamFinishReason.ToolCalls, "healed");
            yield break;
        }

        yield return new StreamChunk.Done(
            anyToolCall ? StreamFinishReason.ToolCalls : MapFinishReason(rawFinish),
            rawFinish,
            splitter.RawText);
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        CancellationToken ct)
    {
        var dto = await _client.CompleteChatAsync(
            Model,
            messages,
            tools.Count > 0 ? tools : null,
            options.Temperature,
            _verboseLogging,
            ct,
            options.NumCtx,
            options.KeepAliveSeconds ?? KeepAliveLockSeconds,
            options.Think,
            options.NumPredict).ConfigureAwait(false);

        return new ChatCompletionResult(dto.Text ?? "", dto.PromptEvalCount, dto.EvalCount);
    }

    public async Task WarmupAsync(CancellationToken ct)
    {
        try
        {
            Console.WriteLine($"[WARMUP] Iniciando trava de memória (Keep-Alive Infinita) para {Model}...");

            // num_ctx=16384: ajustado de 8192 após observação de que respostas vazias
            // ("Ação executada com sucesso") ocorrem quando o histórico + tool results
            // somam ~5500 tokens e gemma4 decide não gerar resposta por falta de espaço.
            // Gemma4:e2b suporta nominalmente 131k; 16k é equilíbrio entre folga e custo
            // de prefill em CPU. Se sentir lentidão excessiva, voltar para 12288 ou 8192.
            // Ollama mantém esse num_ctx para todas as chamadas enquanto keep_alive=-1.
            var payload = new
            {
                model = Model,
                keep_alive = KeepAliveLockSeconds,
                options = new { num_ctx = 16384 }
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient
                .PostAsync($"{_baseUrl}/api/generate", content, ct)
                .ConfigureAwait(false);
            Console.WriteLine("[WARMUP] Modelo trancado na memória com sucesso.");
        }
        catch (OperationCanceledException)
        {
            // Encerramento do app durante o aquecimento: não é erro, e engolir aqui esconderia
            // o cancelamento de quem espera a Task.
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARMUP ERRO] {ex.Message}");
        }
    }

    private static StreamFinishReason MapFinishReason(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return StreamFinishReason.Unknown;
        return raw.ToLowerInvariant() switch
        {
            "stop" => StreamFinishReason.Stop,
            "tool_calls" => StreamFinishReason.ToolCalls,
            "length" => StreamFinishReason.Length,
            _ => StreamFinishReason.Unknown
        };
    }
}
