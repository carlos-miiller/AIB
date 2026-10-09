using System;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
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
/// Provider do Google AI Studio (Gemini API), pelo endpoint compatível com a OpenAI
/// (<c>/v1beta/openai/chat/completions</c>, SSE), por HTTP direto e com a chave do AI Studio.
/// <para>
/// O endpoint compatível, e não o nativo <c>generateContent</c>: o formato das mensagens, das
/// ferramentas e do stream é o que o <see cref="OpenRouterProvider"/> já lê
/// (<see cref="OpenRouterProvider.LerTrecho"/>), e o nativo pediria um segundo serializador e
/// um segundo leitor de stream para o mesmo modelo.
/// </para>
/// <para>
/// O que é só do Gemini fica aqui. A ASSINATURA DE PENSAMENTO: nos modelos que raciocinam, a
/// chamada de ferramenta vem com um <c>extra_content</c> assinado, e a requisição seguinte tem
/// de devolvê-lo na mesma chamada — sem ele a API recusa (400) ou o modelo recomeça a pensar do
/// zero. O RACIOCÍNIO é <c>reasoning_effort</c>, e nem todo modelo aceita desligar. E as
/// chamadas em paralelo, que o Gemini pode mandar inteiras, sem id e com o mesmo índice.
/// </para>
/// <para>
/// Escrito pela documentação e confirmado no uso em 09/10/2026, com <c>gemini-flash-latest</c>:
/// dois turnos de navegador com mais de vinte voltas de ferramenta, uma assinatura guardada e
/// devolvida em cada volta, nenhum 400, e o cache implícito acertando de 70% a 83% da entrada.
/// O raciocínio chegou como texto e acendeu o indicador. Chamadas em paralelo e a recusa de
/// desligar o raciocínio (modelos Pro) continuam só nos testes.
/// </para>
/// </summary>
public sealed class GoogleProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _chave;
    private readonly IToolCallHealer _healer;
    private readonly bool _verboseLogging;

    /// <summary>
    /// O <c>extra_content</c> de cada chamada de ferramenta, pelo id dela. Volta ao modelo em toda
    /// requisição em que a chamada ainda estiver no histórico — pela assinatura, e porque a
    /// mensagem tem de sair igual sempre para o cache do prefixo valer. Mesma razão e mesma poda
    /// do raciocínio guardado no <see cref="OpenRouterProvider"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _extraPorChamada = new(StringComparer.Ordinal);

    private const int TetoDeExtras = 4096;

    /// <summary>
    /// O modelo recusou <c>reasoning_effort: none</c> (os Pro não desligam o raciocínio). Dali em
    /// diante o campo não vai mais quando o pedido é desligar.
    /// </summary>
    private volatile bool _naoDesliga;

    public GoogleProvider(HttpClient http, string baseUrl, string chave, string model,
                          IToolCallHealer healer, bool verboseLogging)
    {
        _http = http;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? ProvedoresDeIa.UrlDoGoogle : baseUrl).TrimEnd('/');
        _chave = chave;
        Model = model;
        _healer = healer;
        _verboseLogging = verboseLogging;
    }

    public string Name => ProvedoresDeIa.Google;

    public string Model { get; }

    /// <summary>Esperas antes de cada nova tentativa (429, 5xx, rede). Settable para ensaio.</summary>
    public IReadOnlyList<TimeSpan> EsperasEntreTentativas { get; set; } =
        new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12) };

    /// <summary>Silêncio máximo do stream antes de a conexão ser dada por morta.</summary>
    public TimeSpan PrazoDeSilencio { get; set; } = TimeSpan.FromSeconds(180);

    private static readonly TimeSpan TetoDoRetryAfter = TimeSpan.FromSeconds(30);

    // ─────────────────────────────────────────────────────────────────────────
    // Corpo da requisição
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>O corpo JSON de uma requisição. Público para ensaio.</summary>
    public string MontarCorpo(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options, bool stream)
    {
        var corpo = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = stream,
            ["temperature"] = options.Temperature,
            ["messages"] = Mensagens(messages)
        };

        if (tools.Count > 0)
        {
            var ferramentas = new JsonArray();
            foreach (var t in tools) ferramentas.Add(JsonNode.Parse(ModelReaderWriter.Write(t).ToString()));
            corpo["tools"] = ferramentas;
        }

        // O uso vem no fim do stream; sem isto não há contagem de entrada nem de cache.
        if (stream) corpo["stream_options"] = new JsonObject { ["include_usage"] = true };

        if (options.NumPredict is int teto) corpo["max_tokens"] = teto;

        // Sem campo: padrão do modelo. Desligado — pela configuração ou por quem chama com
        // Think=false, como o resumidor e a triagem — pede "none", enquanto o modelo aceitar.
        string? raciocinio = options.Raciocinio;
        if (raciocinio == null && options.Think == false) raciocinio = PerfilDeProvedor.RaciocinioDesligado;

        switch (raciocinio)
        {
            case PerfilDeProvedor.RaciocinioDesligado when !_naoDesliga:
                corpo["reasoning_effort"] = "none";
                break;
            case "low" or "medium" or "high":
                corpo["reasoning_effort"] = raciocinio;
                break;
        }

        return corpo.ToJsonString();
    }

    /// <summary>As mensagens no formato da API, com o <c>extra_content</c> de volta em cada chamada.</summary>
    private JsonArray Mensagens(IReadOnlyList<ChatMessage> messages)
    {
        var mensagens = new JsonArray();

        foreach (var mensagem in messages)
        {
            var no = JsonNode.Parse(ModelReaderWriter.Write(mensagem).ToString());

            if (mensagem is AssistantChatMessage { ToolCalls.Count: > 0 } && no?["tool_calls"] is JsonArray chamadas)
            {
                foreach (var chamada in chamadas)
                {
                    if (chamada is JsonObject c && (string?)c["id"] is string id
                        && _extraPorChamada.TryGetValue(id, out var extra))
                        c["extra_content"] = JsonNode.Parse(extra);
                }
            }

            mensagens.Add(no);
        }

        return mensagens;
    }

    private HttpRequestMessage Requisicao(string corpo)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _chave);
        return req;
    }

    /// <summary>
    /// Envia, repetindo o que é passageiro (408, 429, 5xx, rede) e só antes de a resposta
    /// começar. 400, 401, 403 e 404 não se repetem: são o pedido, a chave ou o modelo.
    /// </summary>
    private async Task<HttpResponseMessage> EnviarAsync(string corpo, bool stream, CancellationToken ct)
    {
        for (int tentativa = 0; ; tentativa++)
        {
            bool ultima = tentativa >= EsperasEntreTentativas.Count;
            HttpResponseMessage? resp = null;

            using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
            prazo.CancelAfter(PrazoDeSilencio);

            try
            {
                using var req = Requisicao(corpo);
                resp = await _http.SendAsync(req,
                    stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                    prazo.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (ultima) throw new TimeoutException(FraseDoSilencio());
                Console.WriteLine($"[Google] sem resposta em {PrazoDeSilencio.TotalSeconds:0}s; tentando de novo");
            }
            catch (HttpRequestException ex)
            {
                if (ultima) throw new InvalidOperationException($"Google inacessível: {ex.Message}", ex);
                Console.WriteLine($"[Google] falha de rede ({ex.Message}); tentando de novo");
            }

            if (resp != null)
            {
                if (resp.IsSuccessStatusCode) return resp;

                int codigo = (int)resp.StatusCode;
                string texto = "";
                try { texto = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                var pedida = resp.Headers.RetryAfter?.Delta;
                resp.Dispose();

                if (!(codigo is 408 or 429 or >= 500) || ultima)
                    throw new ErroDoGoogle(codigo, texto, MensagemDeErro(codigo, texto));

                var espera = pedida is TimeSpan p && p > TimeSpan.Zero
                    ? (p > TetoDoRetryAfter ? TetoDoRetryAfter : p)
                    : EsperasEntreTentativas[tentativa];

                Console.WriteLine($"[Google] {codigo}: {Motivo(texto)}; nova tentativa em {espera.TotalSeconds:0.#}s");
                await Task.Delay(espera, ct).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(EsperasEntreTentativas[tentativa], ct).ConfigureAwait(false);
        }
    }

    /// <summary>A recusa da API, com o código e o corpo para quem precisa decidir por eles.</summary>
    private sealed class ErroDoGoogle : InvalidOperationException
    {
        public ErroDoGoogle(int codigo, string corpo, string mensagem) : base(mensagem)
        {
            Codigo = codigo;
            Corpo = corpo;
        }

        public int Codigo { get; }
        public string Corpo { get; }
    }

    /// <summary>
    /// Envia e, se o modelo recusar o raciocínio desligado, manda de novo sem o campo e lembra.
    /// Os modelos Pro não desligam; descobrir isso por tabela de nomes envelheceria a cada
    /// lançamento.
    /// </summary>
    private async Task<HttpResponseMessage> EnviarAsync(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, ChatRequestOptions options,
        CancellationToken ct)
    {
        string corpo = MontarCorpo(messages, tools, options, stream: true);

        try
        {
            return await EnviarAsync(corpo, stream: true, ct).ConfigureAwait(false);
        }
        catch (ErroDoGoogle ex) when (ex.Codigo == 400 && !_naoDesliga && RecusouDesligar(corpo, ex.Corpo))
        {
            Console.WriteLine($"[Google] {Model} não aceita desligar o raciocínio; segue no padrão do modelo.");
            _naoDesliga = true;
            return await EnviarAsync(MontarCorpo(messages, tools, options, stream: true), stream: true, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Se o 400 é sobre o raciocínio que o pedido mandou desligar.</summary>
    public static bool RecusouDesligar(string pedido, string resposta) =>
        pedido.Contains("\"reasoning_effort\":\"none\"", StringComparison.Ordinal)
        && (resposta.Contains("thinking", StringComparison.OrdinalIgnoreCase)
            || resposta.Contains("reasoning", StringComparison.OrdinalIgnoreCase));

    private string FraseDoSilencio() =>
        $"O Google ficou {PrazoDeSilencio.TotalSeconds:0}s sem mandar nada; a requisição foi abandonada. Tente de novo.";

    private async Task<string?> LerLinhaAsync(StreamReader leitor, CancellationToken ct)
    {
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        prazo.CancelAfter(PrazoDeSilencio);

        try
        {
            return await leitor.ReadLineAsync(prazo.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(FraseDoSilencio());
        }
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
        using var resp = await EnviarAsync(messages, tools, options, ct).ConfigureAwait(false);

        var splitter = new ChannelSplitter();
        var chamadas = new JuntaDeChamadas();
        string? rawFinish = null;
        int updates = 0;

        using var leitor = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string? linha = await LerLinhaAsync(leitor, ct).ConfigureAwait(false);
            if (linha == null) break;
            if (!linha.StartsWith("data:", StringComparison.Ordinal)) continue;

            string dado = linha[5..].Trim();
            if (dado == "[DONE]") break;

            // O erro no meio do stream pode vir como lista ([{"error":…}]), que não é um evento.
            if (!dado.StartsWith('{')) throw new InvalidOperationException("Google: " + Motivo(dado));

            var trecho = OpenRouterProvider.LerTrecho(dado);
            updates++;

            if (trecho.Erro != null) throw new InvalidOperationException("Google: " + trecho.Erro);
            if (trecho.Fim != null) rawFinish = trecho.Fim;

            if (!string.IsNullOrEmpty(trecho.Raciocinio))
                foreach (var d in splitter.PushThinking(trecho.Raciocinio)) yield return d;

            if (!string.IsNullOrEmpty(trecho.Texto))
                foreach (var d in splitter.Push(trecho.Texto)) yield return d;

            foreach (var delta in chamadas.Receber(dado)) yield return delta;

            if (trecho.Uso != null) yield return trecho.Uso;
        }

        foreach (var d in splitter.Flush(chamadas.Houve)) yield return d;

        foreach (var (id, extra) in chamadas.Extras) Guardar(id, extra);

        if (_verboseLogging)
            Console.WriteLine($"[STREAM-END][Google] updates={updates} finish={rawFinish ?? "none"} mode={splitter.Mode} final={splitter.FinalText.Length}ch tools={(chamadas.Houve ? "sim" : "nao")} assinaturas={chamadas.Extras.Count}");

        if (!chamadas.Houve && tools.Count > 0 && _healer.TryHeal(splitter.FinalText, tools, out var healed))
        {
            yield return new StreamChunk.ToolCallDelta(
                "heal:0", "fallback_" + Guid.NewGuid().ToString("N"), healed.ToolName, healed.ArgumentsJson);
            yield return new StreamChunk.Done(StreamFinishReason.ToolCalls, "healed");
            yield break;
        }

        yield return new StreamChunk.Done(
            chamadas.Houve ? StreamFinishReason.ToolCalls : MotivoDeFim.De(rawFinish),
            rawFinish,
            splitter.RawText);
    }

    private void Guardar(string id, string extra)
    {
        // Passou do teto: o mapa é de conversas longas já encerradas ou compactadas. Zerar custa
        // no máximo uma requisição a preço cheio na conversa viva.
        if (_extraPorChamada.Count >= TetoDeExtras) _extraPorChamada.Clear();
        _extraPorChamada[id] = extra;
    }

    /// <summary>Quantas assinaturas estão guardadas. Para ensaio.</summary>
    public int ExtrasGuardados => _extraPorChamada.Count;

    /// <summary>
    /// Junta os <c>delta.tool_calls</c> de um stream em chamadas, sem confiar no índice nem no id.
    /// <para>
    /// O Gemini costuma mandar cada chamada inteira num evento, e chamadas em paralelo podem vir
    /// com o mesmo índice ou sem id. Pela regra da OpenAI as duas viriam coladas numa só. Aqui
    /// uma chamada começa quando chega um NOME; pedaço sem nome continua a última. Sem id, a AIB
    /// dá um — a resposta da ferramenta precisa de um para voltar.
    /// </para>
    /// </summary>
    public sealed class JuntaDeChamadas
    {
        private int _n = -1;
        private string? _idAtual;

        public bool Houve => _n >= 0;

        /// <summary>O <c>extra_content</c> de cada chamada que trouxe um, pelo id.</summary>
        public Dictionary<string, string> Extras { get; } = new(StringComparer.Ordinal);

        /// <summary>Os pedaços de chamada de um evento <c>data:</c>.</summary>
        public IReadOnlyList<StreamChunk.ToolCallDelta> Receber(string json)
        {
            var saida = new List<StreamChunk.ToolCallDelta>();

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0) return saida;

            var escolha = choices[0];
            if (!(escolha.TryGetProperty("delta", out var delta) || escolha.TryGetProperty("message", out delta))
                || delta.ValueKind != JsonValueKind.Object
                || !delta.TryGetProperty("tool_calls", out var tcs) || tcs.ValueKind != JsonValueKind.Array) return saida;

            foreach (var tc in tcs.EnumerateArray())
            {
                string? nome = null, args = null;
                if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                {
                    nome = Texto(fn, "name");
                    args = Texto(fn, "arguments");
                }

                string? id = Texto(tc, "id");

                if (!string.IsNullOrEmpty(nome) || _n < 0)
                {
                    _n++;
                    _idAtual = string.IsNullOrEmpty(id) ? "gg_" + Guid.NewGuid().ToString("N") : id;
                    id = _idAtual;
                }
                else
                {
                    // Continuação: o id já foi dito no primeiro pedaço.
                    id = null;
                }

                if (tc.TryGetProperty("extra_content", out var extra) && extra.ValueKind == JsonValueKind.Object)
                    Extras[_idAtual!] = extra.GetRawText();

                saida.Add(new StreamChunk.ToolCallDelta("gg:" + _n, id, string.IsNullOrEmpty(nome) ? null : nome, args));
            }

            return saida;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Chamada única (resumo, triagem)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Chamada única, feita por stream e juntada aqui: é o que mantém o prazo de silêncio.</summary>
    public async Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        CancellationToken ct)
    {
        var texto = new StringBuilder();
        int? entrada = null, saida = null;

        await foreach (var chunk in StreamAsync(messages, tools, options, ct).ConfigureAwait(false))
        {
            switch (chunk)
            {
                case StreamChunk.TextDelta { Channel: TextChannel.Final } t:
                    texto.Append(t.Text);
                    break;

                case StreamChunk.Usage u:
                    entrada = u.PromptEvalCount ?? entrada;
                    saida = u.EvalCount ?? saida;
                    break;
            }
        }

        // O Google não relata o custo da chamada; fica nulo, como no Ollama.
        return new ChatCompletionResult(texto.ToString(), entrada, saida, null);
    }

    /// <summary>Nada a aquecer: o modelo roda do outro lado.</summary>
    public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>O erro como o usuário consegue agir sobre ele.</summary>
    private string MensagemDeErro(int codigo, string corpo) => codigo switch
    {
        400 when corpo.Contains("API key", StringComparison.OrdinalIgnoreCase) =>
            "O Google recusou a chave (400). Confira a chave em Configurações › Conexão LLM.",
        401 or 403 => $"O Google recusou a chave ou o acesso ao modelo ({codigo}). Confira a chave em Configurações › Conexão LLM. {Motivo(corpo)}",
        404 => $"O Google não encontrou o modelo '{Model}' (404). Escolha outro em Configurações › Conexão LLM.",
        429 => $"O Google recusou por limite (429), mesmo depois de esperar: {Motivo(corpo)} — no nível gratuito a cota por minuto e por dia é pequena.",
        _ => $"O Google respondeu {codigo}: {Motivo(corpo)}"
    };

    /// <summary>
    /// A frase do erro: <c>error.message</c>, venha o corpo como objeto ou como lista de um
    /// objeto — o Google manda dos dois jeitos.
    /// </summary>
    public static string Motivo(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return "sem detalhe";

        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var raiz = doc.RootElement;
            if (raiz.ValueKind == JsonValueKind.Array && raiz.GetArrayLength() > 0) raiz = raiz[0];

            if (raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("error", out var erro)
                && erro.ValueKind == JsonValueKind.Object && Texto(erro, "message") is { Length: > 0 } msg)
                return msg.Length > 400 ? msg[..400] + "…" : msg;
        }
        catch (JsonException) { }

        return corpo.Length > 300 ? corpo[..300] + "…" : corpo;
    }

    private static string? Texto(JsonElement el, string nome) =>
        el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
