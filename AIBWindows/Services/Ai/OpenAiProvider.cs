using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Ai;

/// <summary>
/// Provider de chat sobre o SDK oficial da OpenAI (serve também endpoints compatíveis
/// com a API OpenAI). Concentra tudo que era o caminho "else" de
/// <c>OpenAIService.StreamResponseAsync</c>: montagem das opções, streaming, agregação
/// de tool calls e classificação de canal do texto.
/// </summary>
public sealed class OpenAiProvider : IChatProvider
{
    private readonly ChatClient _client;
    private readonly IToolCallHealer _healer;
    private readonly bool _verboseLogging;

    public OpenAiProvider(ChatClient client, string model, IToolCallHealer healer, bool verboseLogging)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _healer = healer ?? throw new ArgumentNullException(nameof(healer));
        Model = model ?? "";
        _verboseLogging = verboseLogging;
    }

    public string Name => "OpenAI";

    public string Model { get; }

    /// <summary>No OpenAI não há prefill de modelo: o serviço já está quente do outro lado.</summary>
    public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;

    // ─────────────────────────────────────────────────────────────────────────
    // Streaming
    // ─────────────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var chatOptions = BuildOptions(tools, options);
        var splitter = new ChannelSplitter();

        bool anyToolCall = false;
        string? rawFinishReason = null;
        int updateCount = 0;

        var updates = _client.CompleteChatStreamingAsync(messages, chatOptions, ct);
        await foreach (var update in updates.WithCancellation(ct).ConfigureAwait(false))
        {
            updateCount++;
            if (update.FinishReason.HasValue) rawFinishReason = update.FinishReason.Value.ToString();

            string text = JoinContent(update.ContentUpdate);
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var delta in splitter.Push(text)) yield return delta;
            }

            foreach (var tc in update.ToolCallUpdates)
            {
                anyToolCall = true;

                // O Index do SDK é estável entre os chunks de uma mesma chamada — é ele
                // que costura os fragmentos de argumento na chamada certa.
                yield return new StreamChunk.ToolCallDelta(
                    "oa:" + tc.Index.ToString(CultureInfo.InvariantCulture),
                    tc.ToolCallId,
                    tc.FunctionName,
                    tc.FunctionArgumentsUpdate?.ToString());
            }

            if (update.Usage != null && update.Usage.InputTokenDetails != null)
            {
                // A OpenAI reporta cache de prompt explicitamente, então este provider sabe
                // responder quantos tokens foram reaproveitados.
                int cached = update.Usage.InputTokenDetails.CachedTokenCount;
                yield return new StreamChunk.Usage(null, cached, cached);
            }
        }

        foreach (var delta in splitter.Flush(anyToolCall)) yield return delta;

        if (_verboseLogging)
        {
            Console.WriteLine($"[STREAM-END][OpenAI] updates={updateCount} finish={rawFinishReason ?? "none"} mode={splitter.Mode} final={splitter.FinalText.Length}ch tools={(anyToolCall ? "sim" : "nao")} yielded={splitter.AnythingEmittedAsFinal}");
        }

        // Cura de chamada escrita como prosa: só quando o modelo NÃO usou function calling
        // e existem ferramentas ativas. O texto analisado é apenas o do canal final —
        // conteúdo de <think> nunca chega aqui.
        if (!anyToolCall && tools.Count > 0 && _healer.TryHeal(splitter.FinalText, tools, out var healed))
        {
            Console.WriteLine($"[FALLBACK REGEX] Ação em texto detectada: {healed.MatchedText}");
            yield return new StreamChunk.ToolCallDelta(
                "heal:0",
                "fallback_" + Guid.NewGuid().ToString("N"),
                healed.ToolName,
                healed.ArgumentsJson);
            yield return new StreamChunk.Done(StreamFinishReason.ToolCalls, "healed");
            yield break;
        }

        yield return new StreamChunk.Done(
            MapFinishReason(rawFinishReason, anyToolCall),
            rawFinishReason,
            splitter.RawText);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Chamada única (warmup, AskStateless)
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        CancellationToken ct)
    {
        var chatOptions = BuildOptions(tools, options);
        var completion = await _client.CompleteChatAsync(messages, chatOptions, ct).ConfigureAwait(false);

        var value = completion.Value;
        var sb = new StringBuilder();
        if (value?.Content != null)
        {
            foreach (var part in value.Content)
            {
                if (part != null && !string.IsNullOrEmpty(part.Text)) sb.Append(part.Text);
            }
        }

        int? promptEval = value?.Usage?.InputTokenCount;
        int? cached = value?.Usage?.InputTokenDetails?.CachedTokenCount;
        return new ChatCompletionResult(sb.ToString(), promptEval, cached);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Monta as opções da requisição. As definições de ferramenta são anexadas em TODOS
    /// os caminhos: antes elas nunca eram enviadas no caminho OpenAI, então o function
    /// calling nativo jamais acontecia e 100% das execuções caíam no fallback de regex.
    /// Lista vazia = ferramentas desligadas (o opt-out EnableIntelligentTools é aplicado
    /// pelo chamador, igual ao caminho Ollama).
    /// </summary>
    private static ChatCompletionOptions BuildOptions(IReadOnlyList<ChatTool> tools, ChatRequestOptions options)
    {
        var chatOptions = new ChatCompletionOptions { Temperature = options.Temperature };
        for (int i = 0; i < tools.Count; i++) chatOptions.Tools.Add(tools[i]);
        return chatOptions;
    }

    private static string JoinContent(IReadOnlyList<ChatMessageContentPart> parts)
    {
        if (parts == null || parts.Count == 0) return "";
        if (parts.Count == 1) return parts[0]?.Text ?? "";

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part != null && !string.IsNullOrEmpty(part.Text)) sb.Append(part.Text);
        }
        return sb.ToString();
    }

    private static StreamFinishReason MapFinishReason(string? raw, bool anyToolCall)
    {
        if (anyToolCall) return StreamFinishReason.ToolCalls;
        if (string.IsNullOrEmpty(raw)) return StreamFinishReason.Unknown;

        if (raw.Equals("Stop", StringComparison.OrdinalIgnoreCase)) return StreamFinishReason.Stop;
        if (raw.Equals("ToolCalls", StringComparison.OrdinalIgnoreCase)) return StreamFinishReason.ToolCalls;
        if (raw.Equals("Length", StringComparison.OrdinalIgnoreCase)) return StreamFinishReason.Length;
        return StreamFinishReason.Unknown;
    }
}
