using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services;

public class ChatUpdateDto
{
    public string? Text { get; set; }
    public string? FinishReason { get; set; }
    public List<ToolCallUpdateDto> ToolCallUpdates { get; set; } = new();

    // Usage stats
    public int? PromptEvalCount { get; set; }
    public int? EvalCount { get; set; }

    /// <summary>Tempo de avaliação do prompt, em milissegundos. O Ollama reporta em nanossegundos.</summary>
    public double? PromptEvalMillis { get; set; }

    /// <summary>
    /// Raciocínio vindo do campo SEPARADO message.thinking. Modelos de raciocínio (deepseek-r1,
    /// qwen com thinking) não embutem &lt;think&gt; no content — mandam neste campo, e o content
    /// fica vazio enquanto pensam. Sem ler aqui, o raciocínio some sem deixar rastro.
    /// </summary>
    public string? Thinking { get; set; }
}

public class ToolCallUpdateDto
{
    public int Index { get; set; }
    public string? ToolCallId { get; set; }
    public string? FunctionName { get; set; }
    public string? FunctionArgumentsUpdate { get; set; }
}

public class OllamaNativeClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;

    /// <summary>Objeto de argumentos vazio, usado quando o JSON gravado é inválido.</summary>
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement.Clone();

    public OllamaNativeClient(string apiUrl, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (httpClient == null)
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _apiUrl = apiUrl.Replace("/v1", "").TrimEnd('/') + "/api/chat";
    }

    public async IAsyncEnumerable<ChatUpdateDto> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> history,
        IEnumerable<ChatTool>? tools,
        float temperature,
        bool debug,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        int numCtx = 16384,
        int? keepAliveSeconds = null)
    {
        var requestObj = new
        {
            model = model,
            messages = FormatMessages(history),
            stream = true,
            // keep_alive precisa viajar em TODA requisição de chat: o Ollama reaplica o valor
            // recebido e, quando o campo é omitido, volta ao default de 5 minutos — desfazendo
            // em silêncio a trava de VRAM feita pelo aquecimento (keep_alive=-1).
            keep_alive = keepAliveSeconds,
            options = new { temperature = temperature, num_ctx = numCtx },
            tools = FormatTools(tools)
        };

        var json = JsonSerializer.Serialize(requestObj, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        if (debug) Console.WriteLine($"\n[PROVIDER_DEBUG_REQUEST]:\n{json}\n");
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl) { Content = content };
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream);

        // O índice da tool call é global ao stream inteiro, NÃO por linha NDJSON: o Ollama
        // reinicia o array tool_calls em 0 a cada linha, e indexar por posição de array
        // fundia duas chamadas distintas em uma só, corrompida.
        int toolCallIndex = 0;

        // A condição do laço olha só o cancelamento: StreamReader.EndOfStream é uma
        // propriedade SÍNCRONA que faz Stream.Read bloqueante no socket e ignora o token.
        // Aqui só existe await ReadLineAsync(ct); linha nula é fim de stream.
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (debug) Console.WriteLine($"[PROVIDER_DEBUG_RESPONSE]: {line}");

            // Uma linha NDJSON malformada não pode derrubar o turno inteiro: sem esta guarda a
            // JsonException sobe pelo iterador assíncrono e aborta a resposta já em andamento.
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[OLLAMA] Linha NDJSON ignorada (JSON inválido): {ex.Message}");
                continue;
            }

            using var _doc = doc;
            var root = doc.RootElement;

            var dto = new ChatUpdateDto();

            if (root.TryGetProperty("message", out var msgElement))
            {
                if (msgElement.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
                {
                    dto.Text = contentElement.GetString();
                }

                if (msgElement.TryGetProperty("thinking", out var thinkingElement) && thinkingElement.ValueKind == JsonValueKind.String)
                {
                    dto.Thinking = thinkingElement.GetString();
                }

                if (msgElement.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCallsElement.EnumerateArray())
                    {
                        if (tc.TryGetProperty("function", out var funcElement))
                        {
                            var tcDto = new ToolCallUpdateDto
                            {
                                Index = toolCallIndex,
                                ToolCallId = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                                FunctionName = funcElement.GetProperty("name").GetString()
                            };

                            if (funcElement.TryGetProperty("arguments", out var argsElement))
                            {
                                tcDto.FunctionArgumentsUpdate = argsElement.ValueKind == JsonValueKind.Object
                                    ? argsElement.GetRawText()
                                    : argsElement.GetString();
                            }
                            dto.ToolCallUpdates.Add(tcDto);
                            toolCallIndex++;
                        }
                    }
                }
            }

            if (root.TryGetProperty("done", out var doneElement) && doneElement.GetBoolean())
            {
                dto.FinishReason = "stop";

                if (root.TryGetProperty("prompt_eval_count", out var pEval))
                    dto.PromptEvalCount = pEval.GetInt32();
                if (root.TryGetProperty("eval_count", out var eval))
                    dto.EvalCount = eval.GetInt32();
                if (root.TryGetProperty("prompt_eval_duration", out var pDur))
                    dto.PromptEvalMillis = pDur.GetInt64() / 1_000_000.0;
            }

            yield return dto;
        }
    }

    public async Task<ChatUpdateDto> CompleteChatAsync(
        string model,
        IReadOnlyList<ChatMessage> history,
        IEnumerable<ChatTool>? tools,
        float temperature,
        bool debug,
        CancellationToken ct,
        int numCtx = 16384,
        int? keepAliveSeconds = null)
    {
        var requestObj = new
        {
            model = model,
            messages = FormatMessages(history),
            stream = false,
            keep_alive = keepAliveSeconds,
            options = new { temperature = temperature, num_ctx = numCtx },
            tools = FormatTools(tools)
        };

        var json = JsonSerializer.Serialize(requestObj, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        if (debug) Console.WriteLine($"\n[PROVIDER_DEBUG_REQUEST (Warmup)]:\n{json}\n");
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync(_apiUrl, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (debug) Console.WriteLine($"[PROVIDER_DEBUG_RESPONSE (Warmup)]:\n{responseJson}\n");
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var dto = new ChatUpdateDto();

        if (root.TryGetProperty("message", out var msgElement) && msgElement.TryGetProperty("content", out var contentElement))
        {
            dto.Text = contentElement.GetString() ?? "";
        }

        if (root.TryGetProperty("prompt_eval_count", out var pEval))
            dto.PromptEvalCount = pEval.GetInt32();
        if (root.TryGetProperty("eval_count", out var eval))
            dto.EvalCount = eval.GetInt32();
        if (root.TryGetProperty("prompt_eval_duration", out var pDur))
            dto.PromptEvalMillis = pDur.GetInt64() / 1_000_000.0;

        return dto;
    }

    private List<object> FormatMessages(IReadOnlyList<ChatMessage> history)
    {
        var list = new List<object>();
        foreach (var msg in history)
        {
            string role = msg switch
            {
                SystemChatMessage => "system",
                UserChatMessage => "user",
                AssistantChatMessage => "assistant",
                ToolChatMessage => "tool",
                _ => "user"
            };

            if (msg is ToolChatMessage tcm)
            {
                list.Add(new { role = role, content = tcm.Content.Count > 0 ? tcm.Content[0].Text : "" });
                continue;
            }

            if (msg is AssistantChatMessage acm && acm.ToolCalls != null && acm.ToolCalls.Count > 0)
            {
                var toolCalls = new List<object>();
                foreach (var tc in acm.ToolCalls)
                {
                    toolCalls.Add(new
                    {
                        function = new
                        {
                            name = tc.FunctionName,
                            arguments = ParseArgumentsSafe(tc.FunctionArguments?.ToString())
                        }
                    });
                }
                list.Add(new { role = role, content = "", tool_calls = toolCalls });
                continue;
            }

            string content = "";
            if (msg.Content != null && msg.Content.Count > 0)
                content = msg.Content[0].Text;

            list.Add(new { role = role, content = content });
        }
        return list;
    }

    /// <summary>
    /// Lê os argumentos de uma tool call já gravada no histórico. Argumento vazio, texto solto
    /// ou JSON quebrado degradam para {} em vez de lançar: uma única chamada malformada fazia
    /// TODA requisição seguinte estourar JsonException, inutilizando a sessão inteira.
    /// </summary>
    private static JsonElement ParseArgumentsSafe(string? rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments)) return EmptyArguments;

        try
        {
            using var doc = JsonDocument.Parse(rawArguments);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.Clone()
                : EmptyArguments;
        }
        catch (JsonException)
        {
            return EmptyArguments;
        }
    }

    private object? FormatTools(IEnumerable<ChatTool>? tools)
    {
        if (tools == null || !tools.Any()) return null;

        var list = new List<object>();
        foreach (var t in tools)
        {
            try
            {
                var funcObj = new Dictionary<string, object>();
                funcObj["name"] = t.FunctionName;
                if (!string.IsNullOrEmpty(t.FunctionDescription))
                    funcObj["description"] = t.FunctionDescription;

                if (t.FunctionParameters != null)
                {
                    var parametersJson = t.FunctionParameters.ToString();
                    funcObj["parameters"] = JsonDocument.Parse(parametersJson).RootElement;
                }

                list.Add(new
                {
                    type = "function",
                    function = funcObj
                });
            }
            catch { }
        }
        return list;
    }
}
