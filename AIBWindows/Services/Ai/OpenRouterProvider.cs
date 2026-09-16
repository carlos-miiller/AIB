using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Ai;

/// <summary>
/// Provider do OpenRouter (<c>/chat/completions</c>, SSE), por HTTP direto.
/// <para>
/// Não passa pelo cliente da OpenAI, e isso é o ponto. O OpenRouter fala o formato da OpenAI mas
/// acrescenta o que a AIB precisa e o cliente descarta: o parâmetro <c>reasoning</c> (ligar,
/// desligar, esforço), o raciocínio em <c>delta.reasoning</c> — sem ele o indicador de "pensando"
/// nunca acende — e o erro no meio do stream. Pelo cliente da OpenAI, os três se perdiam em
/// silêncio.
/// </para>
/// <para>
/// As mensagens e as ferramentas são serializadas pelo próprio SDK (<see cref="ModelReaderWriter"/>),
/// que já produz o formato da API. Montar o JSON à mão aqui seria uma segunda definição do
/// formato para sair de sincronia.
/// </para>
/// </summary>
public sealed class OpenRouterProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _chave;
    private readonly IToolCallHealer _healer;
    private readonly bool _verboseLogging;

    public OpenRouterProvider(HttpClient http, string baseUrl, string chave, string model,
                              IToolCallHealer healer, bool verboseLogging)
    {
        _http = http;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? ProvedoresDeIa.UrlDoOpenRouter : baseUrl).TrimEnd('/');
        _chave = chave;
        Model = model;
        _healer = healer;
        _verboseLogging = verboseLogging;
    }

    public string Name => ProvedoresDeIa.OpenRouter;

    public string Model { get; }

    // ─────────────────────────────────────────────────────────────────────────
    // Corpo da requisição
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O corpo JSON de uma requisição. Público para ensaio: é aqui que o raciocínio desligado
    /// vira <c>{"enabled":false}</c>, e é isso que o ensaio confere sem rede.
    /// </summary>
    public string MontarCorpo(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options, bool stream)
    {
        var corpo = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = stream,
            ["temperature"] = options.Temperature
        };

        var mensagens = new JsonArray();
        foreach (var m in messages) mensagens.Add(JsonNode.Parse(ModelReaderWriter.Write(m).ToString()));
        corpo["messages"] = mensagens;

        if (tools.Count > 0)
        {
            var ferramentas = new JsonArray();
            foreach (var t in tools) ferramentas.Add(JsonNode.Parse(ModelReaderWriter.Write(t).ToString()));
            corpo["tools"] = ferramentas;
        }

        if (stream) corpo["stream_options"] = new JsonObject { ["include_usage"] = true };

        if (options.NumPredict is int teto) corpo["max_tokens"] = teto;

        // Sem campo: deixa no padrão do modelo. Com esforço: pede aquele esforço. Desligado —
        // pela configuração ou por quem chama com Think=false, como o resumidor e a triagem —
        // manda desligar: resumir não ganha nada com raciocínio, e aqui ele é pago por token.
        string? raciocinio = options.Raciocinio;
        if (raciocinio == null && options.Think == false) raciocinio = PerfilDeProvedor.RaciocinioDesligado;

        switch (raciocinio)
        {
            case PerfilDeProvedor.RaciocinioDesligado:
                corpo["reasoning"] = new JsonObject { ["enabled"] = false };
                break;
            case "low" or "medium" or "high":
                corpo["reasoning"] = new JsonObject { ["effort"] = raciocinio };
                break;
        }

        return corpo.ToJsonString();
    }

    private HttpRequestMessage Requisicao(string corpo)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json")
        };

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _chave);

        // Identificação do app no painel do OpenRouter. Não carrega nada do usuário.
        req.Headers.TryAddWithoutValidation("X-Title", "AIB");
        return req;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Streaming
    // ─────────────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = Requisicao(MontarCorpo(messages, tools, options, stream: true));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await MensagemDeErroAsync(resp, ct).ConfigureAwait(false));

        var splitter = new ChannelSplitter();
        bool anyToolCall = false;
        string? rawFinish = null;
        int updates = 0;

        using var leitor = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string? linha = await leitor.ReadLineAsync(ct).ConfigureAwait(false);
            if (linha == null) break;
            if (!linha.StartsWith("data:", StringComparison.Ordinal)) continue;   // ": OPENROUTER PROCESSING", linhas vazias

            string dado = linha[5..].Trim();
            if (dado == "[DONE]") break;

            var trecho = LerTrecho(dado);
            updates++;

            if (trecho.Erro != null) throw new InvalidOperationException("OpenRouter: " + trecho.Erro);
            if (trecho.Fim != null) rawFinish = trecho.Fim;

            if (!string.IsNullOrEmpty(trecho.Raciocinio))
                foreach (var d in splitter.PushThinking(trecho.Raciocinio)) yield return d;

            if (!string.IsNullOrEmpty(trecho.Texto))
                foreach (var d in splitter.Push(trecho.Texto)) yield return d;

            foreach (var c in trecho.Chamadas)
            {
                anyToolCall = true;
                yield return new StreamChunk.ToolCallDelta("or:" + c.Indice, c.Id, c.Nome, c.Argumentos);
            }

            if (trecho.Uso != null) yield return trecho.Uso;
        }

        foreach (var d in splitter.Flush(anyToolCall)) yield return d;

        if (_verboseLogging)
            Console.WriteLine($"[STREAM-END][OpenRouter] updates={updates} finish={rawFinish ?? "none"} mode={splitter.Mode} final={splitter.FinalText.Length}ch tools={(anyToolCall ? "sim" : "nao")}");

        if (!anyToolCall && tools.Count > 0 && _healer.TryHeal(splitter.FinalText, tools, out var healed))
        {
            yield return new StreamChunk.ToolCallDelta(
                "heal:0", "fallback_" + Guid.NewGuid().ToString("N"), healed.ToolName, healed.ArgumentsJson);
            yield return new StreamChunk.Done(StreamFinishReason.ToolCalls, "healed");
            yield break;
        }

        yield return new StreamChunk.Done(
            anyToolCall ? StreamFinishReason.ToolCalls : MapFinishReason(rawFinish),
            rawFinish,
            splitter.RawText);
    }

    /// <summary>Uma chamada de ferramenta em pedaços, como vem no <c>delta.tool_calls</c>.</summary>
    public sealed record PedacoDeChamada(int Indice, string? Id, string? Nome, string? Argumentos);

    /// <summary>O que um evento SSE traz, já separado.</summary>
    public sealed record Trecho(
        string? Texto, string? Raciocinio, IReadOnlyList<PedacoDeChamada> Chamadas,
        string? Fim, StreamChunk.Usage? Uso, string? Erro);

    /// <summary>
    /// Lê um evento <c>data:</c> do stream. Público para ensaio: o formato do OpenRouter é o
    /// contrato que não dá para testar com rede.
    /// </summary>
    public static Trecho LerTrecho(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;

        string? erro = raiz.TryGetProperty("error", out var e)
            ? (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var msg) ? msg.GetString() : e.ToString())
            : null;

        string? texto = null, raciocinio = null, fim = null;
        var chamadas = new List<PedacoDeChamada>();

        if (raiz.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var escolha = choices[0];

            if (escolha.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String)
                fim = f.GetString();

            if (escolha.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                texto = Texto(delta, "content");
                raciocinio = Texto(delta, "reasoning");

                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        int indice = tc.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
                        string? nome = null, args = null;

                        if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                        {
                            nome = Texto(fn, "name");
                            args = Texto(fn, "arguments");
                        }

                        chamadas.Add(new PedacoDeChamada(indice, Texto(tc, "id"), string.IsNullOrEmpty(nome) ? null : nome, args));
                    }
                }
            }
        }

        StreamChunk.Usage? uso = null;
        if (raiz.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            int? cache = u.TryGetProperty("prompt_tokens_details", out var det) && det.ValueKind == JsonValueKind.Object
                         && det.TryGetProperty("cached_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number
                ? ct.GetInt32()
                : null;

            uso = new StreamChunk.Usage(Numero(u, "prompt_tokens"), Numero(u, "completion_tokens"), cache);
        }

        return new Trecho(texto, raciocinio, chamadas, fim, uso, erro);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Chamada única (resumo, triagem)
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        CancellationToken ct)
    {
        using var req = Requisicao(MontarCorpo(messages, tools, options, stream: false));
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await MensagemDeErroAsync(resp, ct).ConfigureAwait(false));

        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;

        if (raiz.TryGetProperty("error", out var e))
            throw new InvalidOperationException("OpenRouter: " + e);

        string texto = "";
        if (raiz.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var m))
            texto = Texto(m, "content") ?? "";

        int? entrada = null, saida = null;
        if (raiz.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            entrada = Numero(u, "prompt_tokens");
            saida = Numero(u, "completion_tokens");
        }

        return new ChatCompletionResult(texto, entrada, saida);
    }

    /// <summary>Nada a aquecer: o modelo roda do outro lado, e aquecer seria pagar por nada.</summary>
    public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O erro como o usuário consegue agir sobre ele. 401 e 402 são os dois que acontecem de
    /// verdade — chave errada e crédito acabado — e a frase da API em inglês não diz o que fazer.
    /// </summary>
    private async Task<string> MensagemDeErroAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string corpo = "";
        try { corpo = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }

        string detalhe = corpo.Length > 300 ? corpo[..300] + "…" : corpo;

        return (int)resp.StatusCode switch
        {
            401 => "OpenRouter recusou a chave (401). Confira a chave em Configurações › Conexão LLM.",
            402 => "OpenRouter: sem crédito para esta requisição (402).",
            404 => $"OpenRouter não encontrou o modelo '{Model}' (404). Escolha outro em Configurações › Conexão LLM. {detalhe}",
            429 => "OpenRouter: limite de requisições atingido (429). Tente de novo em instantes.",
            _ => $"OpenRouter respondeu {(int)resp.StatusCode}: {detalhe}"
        };
    }

    private static string? Texto(JsonElement el, string nome) =>
        el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Numero(JsonElement el, string nome) =>
        el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static StreamFinishReason MapFinishReason(string? raw) => (raw ?? "").ToLowerInvariant() switch
    {
        "stop" => StreamFinishReason.Stop,
        "tool_calls" => StreamFinishReason.ToolCalls,
        "length" => StreamFinishReason.Length,
        _ => StreamFinishReason.Unknown
    };
}
