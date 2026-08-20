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

    public OllamaNativeClient(string apiUrl, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (httpClient == null)
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _apiUrl = apiUrl.Replace("/v1", "").TrimEnd('/') + "/api/chat";
    }

    public async IAsyncEnumerable<ChatUpdateDto> StreamChatAsync(
        string model,
        List<ChatMessage> history,
        IEnumerable<ChatTool>? tools,
        float temperature,
        bool debug,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestObj = new
        {
            model = model,
            messages = FormatMessages(history),
            stream = true,
            options = new { temperature = temperature, num_ctx = 16384 },
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

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (debug) Console.WriteLine($"[PROVIDER_DEBUG_RESPONSE]: {line}");

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var dto = new ChatUpdateDto();

            if (root.TryGetProperty("message", out var msgElement))
            {
                if (msgElement.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
                {
                    dto.Text = contentElement.GetString();
                }

                if (msgElement.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var tc in toolCallsElement.EnumerateArray())
                    {
                        if (tc.TryGetProperty("function", out var funcElement))
                        {
                            var tcDto = new ToolCallUpdateDto
                            {
                                Index = index,
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
                        }
                        index++;
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
            }

            yield return dto;
        }
    }

    public async Task<ChatUpdateDto> CompleteChatAsync(string model, List<ChatMessage> history, IEnumerable<ChatTool>? tools, float temperature, bool debug, CancellationToken ct)
    {
        var requestObj = new
        {
            model = model,
            messages = FormatMessages(history),
            stream = false,
            options = new { temperature = temperature, num_ctx = 16384 },
            tools = FormatTools(tools)
        };

        var json = JsonSerializer.Serialize(requestObj, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        if (debug) Console.WriteLine($"\n[PROVIDER_DEBUG_REQUEST (Warmup)]:\n{json}\n");
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync(_apiUrl, content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
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

        return dto;
    }

    private List<object> FormatMessages(List<ChatMessage> history)
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
                            arguments = JsonDocument.Parse(tc.FunctionArguments.ToString()).RootElement
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
