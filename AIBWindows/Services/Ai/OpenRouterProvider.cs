using System;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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
/// nunca acende —, o erro no meio do stream, o custo em <c>usage.cost</c>, o roteamento em
/// <c>provider</c> e os <c>reasoning_details</c> que precisam voltar ao modelo entre ferramentas.
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
    private readonly bool _semColetaDeDados;

    /// <summary>
    /// O raciocínio de cada volta que pediu ferramenta, pelo id da primeira chamada. Volta ao
    /// modelo em TODA requisição em que aquela mensagem ainda estiver no histórico. Modelos de
    /// raciocínio com ferramentas (Claude, Gemini, o1, DeepSeek) retomam o fio a partir dele; sem
    /// ele, cada volta recomeça a pensar do zero — ou a API recusa, nos que assinam o raciocínio.
    /// <para>
    /// Antes ia só às voltas do turno corrente, e isso quebrava o cache: no turno seguinte a mesma
    /// mensagem saía SEM o raciocínio, o prefixo mudava ali, e tudo dali para a frente era pago a
    /// preço cheio de novo — num histórico com ferramentas, quase o prompt inteiro. O preço de
    /// mandar sempre é carregar tokens de raciocínio antigo; Anthropic e DeepSeek descartam do
    /// lado deles o raciocínio de turnos passados, e o que sobra de custo é cobrado como cache,
    /// não como entrada nova. Trocar um prefixo estável por alguns tokens a mais compensa.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, RaciocinioGuardado> _raciocinioPorChamada = new(StringComparer.Ordinal);

    /// <summary>O JSON do raciocínio e a última requisição que o levou.</summary>
    private sealed class RaciocinioGuardado
    {
        public RaciocinioGuardado(string json, long vistoEm) { Json = json; VistoEm = vistoEm; }
        public string Json { get; }
        public long VistoEm;
    }

    /// <summary>Contador de requisições montadas. Serve de relógio para a poda.</summary>
    private long _requisicoes;

    private readonly object _poda = new();

    /// <summary>
    /// Acima disto o mapa é podado, dos menos recentemente ENVIADOS para os mais, até
    /// <see cref="AlvoDaPoda"/>.
    /// <para>
    /// Antes o mapa era zerado ao passar de 256, e zerar tirava também o raciocínio de mensagens
    /// ainda vivas — o mesmo prefixo quebrado, só que de surpresa. Aqui uma entrada só sai se
    /// outras 1.536 foram enviadas depois dela. O histórico vivo cabe na janela e é compactado
    /// muito antes de ter 1.536 voltas com ferramenta, e cada requisição da conversa renova todas
    /// as dele — então o que sai é de conversa encerrada ou compactada. Idade por requisição não
    /// serviria: a triagem de e-mail usa o mesmo provider e faria as da conversa envelhecerem sem
    /// que elas tivessem saído do histórico.
    /// </para>
    /// </summary>
    private const int TetoDoMapaDeRaciocinio = 2048;

    private const int AlvoDaPoda = 1536;

    public OpenRouterProvider(HttpClient http, string baseUrl, string chave, string model,
                              IToolCallHealer healer, bool verboseLogging, bool semColetaDeDados = true)
    {
        _http = http;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? ProvedoresDeIa.UrlDoOpenRouter : baseUrl).TrimEnd('/');
        _chave = chave;
        Model = model;
        _healer = healer;
        _verboseLogging = verboseLogging;
        _semColetaDeDados = semColetaDeDados;
    }

    public string Name => ProvedoresDeIa.OpenRouter;

    public string Model { get; }

    /// <summary>
    /// Esperas antes de cada nova tentativa de uma requisição recusada por motivo passageiro
    /// (429, 5xx, rede). O tamanho da lista é o número de tentativas extras. Settable para ensaio.
    /// </summary>
    public IReadOnlyList<TimeSpan> EsperasEntreTentativas { get; set; } =
        new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12) };

    /// <summary>
    /// Silêncio máximo do stream. O OpenRouter manda um comentário de keep-alive enquanto o modelo
    /// trabalha; passar disto sem receber nem isso é conexão morta, e não modelo pensando.
    /// <para>
    /// No Ollama não existe prazo assim de propósito: lá um prefill frio fica minutos em
    /// silêncio legítimo. Aqui o silêncio não tem essa desculpa.
    /// </para>
    /// </summary>
    public TimeSpan PrazoDeSilencio { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>Teto de uma espera pedida pelo servidor em Retry-After.</summary>
    private static readonly TimeSpan TetoDoRetryAfter = TimeSpan.FromSeconds(30);

    // ─────────────────────────────────────────────────────────────────────────
    // Corpo da requisição
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O corpo JSON de uma requisição. Público para ensaio: é aqui que o raciocínio desligado
    /// vira <c>{"enabled":false}</c>, e é isso que o ensaio confere sem rede.
    /// </summary>
    /// <param name="doCatalogo">
    /// O modelo no catálogo, quando se sabe. Com ele, só vão os parâmetros que o modelo aceita, e
    /// o roteamento passa a exigir provedores que honrem todos. Sem ele, vai tudo como antes e
    /// sem a exigência — que, às cegas, poderia recusar o modelo inteiro por um campo opcional.
    /// </param>
    public string MontarCorpo(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options, bool stream, ModeloDoOpenRouter? doCatalogo = null)
    {
        bool Aceita(string p) => doCatalogo?.Aceita(p) ?? true;

        var corpo = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = stream
        };

        if (Aceita("temperature")) corpo["temperature"] = options.Temperature;

        corpo["messages"] = Mensagens(messages);

        if (tools.Count > 0)
        {
            var ferramentas = new JsonArray();
            foreach (var t in tools) ferramentas.Add(JsonNode.Parse(ModelReaderWriter.Write(t).ToString()));
            corpo["tools"] = ferramentas;
        }

        // O uso vem sempre no fim do stream, com o custo junto. Sem isto não há conta do turno.
        if (stream) corpo["stream_options"] = new JsonObject { ["include_usage"] = true };
        corpo["usage"] = new JsonObject { ["include"] = true };

        if (options.NumPredict is int teto && Aceita("max_tokens")) corpo["max_tokens"] = teto;

        // Sem campo: deixa no padrão do modelo. Com esforço: pede aquele esforço. Desligado —
        // pela configuração ou por quem chama com Think=false, como o resumidor e a triagem —
        // manda desligar: resumir não ganha nada com raciocínio, e aqui ele é pago por token.
        // Modelo que não raciocina não recebe o campo: com require_parameters, mandar
        // "desligado" a quem não tem o que desligar recusaria a requisição.
        string? raciocinio = options.Raciocinio;
        if (raciocinio == null && options.Think == false) raciocinio = PerfilDeProvedor.RaciocinioDesligado;

        if (doCatalogo == null || doCatalogo.Raciocina)
        {
            switch (raciocinio)
            {
                case PerfilDeProvedor.RaciocinioDesligado:
                    corpo["reasoning"] = new JsonObject { ["enabled"] = false };
                    break;
                case "low" or "medium" or "high":
                    corpo["reasoning"] = new JsonObject { ["effort"] = raciocinio };
                    break;
            }
        }

        // Roteamento. require_parameters: o mesmo modelo é servido por vários provedores, e um
        // que não honra tools ou reasoning os ignora EM SILÊNCIO — a IA simplesmente deixa de
        // usar ferramentas. data_collection deny: só provedores que não guardam nem treinam com o
        // prompt, que aqui leva arquivos e e-mails.
        var roteamento = new JsonObject();
        if (doCatalogo != null) roteamento["require_parameters"] = true;
        if (_semColetaDeDados) roteamento["data_collection"] = "deny";
        if (roteamento.Count > 0) corpo["provider"] = roteamento;

        return corpo.ToJsonString();
    }

    /// <summary>
    /// As mensagens no formato da API, com as duas coisas que o SDK não sabe pôr: o raciocínio
    /// devolvido entre ferramentas e as marcas de cache.
    /// </summary>
    private JsonArray Mensagens(IReadOnlyList<ChatMessage> messages)
    {
        var mensagens = new JsonArray();
        int ultimoUsuario = -1;

        for (int i = 0; i < messages.Count; i++)
        {
            mensagens.Add(JsonNode.Parse(ModelReaderWriter.Write(messages[i]).ToString()));
            if (messages[i] is UserChatMessage) ultimoUsuario = i;
        }

        // Em TODA mensagem de assistente cujo raciocínio se guardou, e não só nas deste turno: a
        // mensagem tem de sair idêntica em toda requisição enquanto estiver no histórico, ou o
        // cache se perde a partir dela. Ver _raciocinioPorChamada.
        long agora = Interlocked.Increment(ref _requisicoes);

        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not AssistantChatMessage { ToolCalls.Count: > 0 } assistente) continue;

            foreach (var chamada in assistente.ToolCalls)
            {
                if (chamada?.Id == null || !_raciocinioPorChamada.TryGetValue(chamada.Id, out var guardado)) continue;

                Interlocked.Exchange(ref guardado.VistoEm, agora);
                mensagens[i]!["reasoning_details"] = JsonNode.Parse(guardado.Json);
                break;
            }
        }

        if (UsaMarcasDeCache(Model)) MarcarCache(messages, mensagens, ultimoUsuario);

        return mensagens;
    }

    /// <summary>
    /// Se o modelo precisa de marcas explícitas para cachear o prompt. Anthropic e Gemini
    /// precisam; OpenAI, DeepSeek, Grok e a maioria cacheiam o prefixo sozinhos e ignoram a marca.
    /// </summary>
    public static bool UsaMarcasDeCache(string modelo) =>
        modelo.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase)
        || modelo.StartsWith("google/gemini", StringComparison.OrdinalIgnoreCase);

    /// <summary>Quantos pontos de cache a Anthropic aceita numa requisição.</summary>
    public const int TetoDeMarcasDeCache = 4;

    /// <summary>
    /// Marca até quatro pontos de cache: a primeira mensagem de sistema (a alma, que quase nunca
    /// muda), a última de sistema (a memória, que muda a cada compactação), a última fala do
    /// usuário e a ÚLTIMA mensagem da requisição.
    /// <para>
    /// A última mensagem é o ponto que importa num turno com ferramentas. Com a marca só na fala
    /// do usuário, cada ida de ferramenta relia a preço cheio tudo o que as voltas anteriores do
    /// mesmo turno tinham acrescentado — e um turno de sete voltas reenvia esse miolo sete vezes.
    /// Marcada a última, a volta seguinte acha em cache tudo até ali. A fala do usuário continua
    /// marcada como rede: a Anthropic só procura acerto até uns 20 blocos antes de cada marca, e
    /// uma volta com muitas ferramentas em paralelo passaria disso.
    /// </para>
    /// <para>
    /// Mensagem de assistente só com tool_calls não tem texto onde pôr a marca; aí vale a anterior
    /// que tenha.
    /// </para>
    /// </summary>
    private static void MarcarCache(IReadOnlyList<ChatMessage> messages, JsonArray mensagens, int ultimoUsuario)
    {
        // O conteúdo em texto vira lista de partes em TODA mensagem de sistema, usuário e
        // ferramenta, marcada ou não. Converter só a marcada faria a mesma mensagem sair como
        // lista numa requisição (quando era a última) e como texto na seguinte — e a mudança de
        // forma arrisca o prefixo que a marca existe para guardar.
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is SystemChatMessage or UserChatMessage or ToolChatMessage && mensagens[i] is JsonObject m)
                EmPartes(m);
        }

        var alvos = new SortedSet<int>();

        int primeiroSistema = -1, ultimoSistema = -1;
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not SystemChatMessage) continue;
            if (primeiroSistema < 0) primeiroSistema = i;
            ultimoSistema = i;
        }

        if (primeiroSistema >= 0) alvos.Add(primeiroSistema);
        if (ultimoSistema >= 0) alvos.Add(ultimoSistema);
        if (ultimoUsuario >= 0) alvos.Add(ultimoUsuario);

        for (int i = mensagens.Count - 1; i >= 0; i--)
        {
            if (mensagens[i] is not JsonObject candidata || EmPartes(candidata) == null) continue;
            alvos.Add(i);
            break;
        }

        foreach (int i in alvos.Take(TetoDeMarcasDeCache))
        {
            if (mensagens[i] is not JsonObject msg || EmPartes(msg) is not JsonArray partes) continue;

            if (partes[^1] is JsonObject ultima && (string?)ultima["type"] == "text")
                ultima["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        }
    }

    /// <summary>
    /// O conteúdo da mensagem como lista de partes, convertendo texto simples. Nulo quando não há
    /// texto onde pôr uma marca — assistente só com tool_calls, conteúdo vazio.
    /// </summary>
    private static JsonArray? EmPartes(JsonObject msg)
    {
        if (msg["content"] is JsonValue v && v.TryGetValue<string>(out var texto))
        {
            if (string.IsNullOrEmpty(texto)) return null;
            var partes = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = texto } };
            msg["content"] = partes;
            return partes;
        }

        return msg["content"] is JsonArray existentes && existentes.Count > 0
               && existentes[^1] is JsonObject ultima && (string?)ultima["type"] == "text"
            ? existentes
            : null;
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

    /// <summary>
    /// Envia, repetindo o que é passageiro: 408, 429, 5xx e falha de rede. Só ANTES da resposta
    /// começar — repetir no meio do stream duplicaria o texto já mostrado.
    /// <para>
    /// 400, 401, 402 e 404 não se repetem: são a chave, o crédito ou o modelo, e tentar de novo
    /// só atrasaria a frase que diz o que fazer.
    /// </para>
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
                Console.WriteLine($"[OpenRouter] sem resposta em {PrazoDeSilencio.TotalSeconds:0}s; tentando de novo");
            }
            catch (HttpRequestException ex)
            {
                if (ultima) throw new InvalidOperationException($"OpenRouter inacessível: {ex.Message}", ex);
                Console.WriteLine($"[OpenRouter] falha de rede ({ex.Message}); tentando de novo");
            }

            if (resp != null)
            {
                if (resp.IsSuccessStatusCode) return resp;

                int codigo = (int)resp.StatusCode;
                bool passageiro = codigo is 408 or 429 or >= 500;

                if (!passageiro || ultima)
                {
                    string erro = await MensagemDeErroAsync(resp, ct).ConfigureAwait(false);
                    resp.Dispose();
                    throw new InvalidOperationException(erro);
                }

                var pedida = resp.Headers.RetryAfter?.Delta;
                resp.Dispose();

                var espera = pedida is TimeSpan p && p > TimeSpan.Zero
                    ? (p > TetoDoRetryAfter ? TetoDoRetryAfter : p)
                    : EsperasEntreTentativas[tentativa];

                Console.WriteLine($"[OpenRouter] {codigo}; nova tentativa em {espera.TotalSeconds:0.#}s");
                await Task.Delay(espera, ct).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(EsperasEntreTentativas[tentativa], ct).ConfigureAwait(false);
        }
    }

    private string FraseDoSilencio() =>
        $"OpenRouter ficou {PrazoDeSilencio.TotalSeconds:0}s sem mandar nada; a requisição foi abandonada. Tente de novo.";

    /// <summary>Uma linha do stream, com o prazo de silêncio. Nula no fim.</summary>
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
        var doCatalogo = await CatalogoDoOpenRouter.BuscarAsync(_http, Model, ct).ConfigureAwait(false);

        using var resp = await EnviarAsync(MontarCorpo(messages, tools, options, stream: true, doCatalogo), stream: true, ct)
            .ConfigureAwait(false);

        var splitter = new ChannelSplitter();
        bool anyToolCall = false;
        string? rawFinish = null;
        int updates = 0;
        string? primeiraChamada = null;
        string? provedor = null;
        var detalhes = new SortedDictionary<int, JsonObject>();

        using var leitor = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string? linha = await LerLinhaAsync(leitor, ct).ConfigureAwait(false);
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

            JuntarDetalhes(detalhes, trecho.DetalhesDoRaciocinio);

            foreach (var c in trecho.Chamadas)
            {
                anyToolCall = true;
                if (primeiraChamada == null && !string.IsNullOrEmpty(c.Id)) primeiraChamada = c.Id;
                yield return new StreamChunk.ToolCallDelta("or:" + c.Indice, c.Id, c.Nome, c.Argumentos);
            }

            // O provedor vem em todo evento; o uso, em geral só no último. Guarda o que já se viu
            // para o uso sair com ele mesmo que o evento do uso não o repita.
            provedor = trecho.Provedor ?? provedor;
            if (trecho.Uso != null) yield return trecho.Uso with { Provedor = trecho.Uso.Provedor ?? provedor };
        }

        foreach (var d in splitter.Flush(anyToolCall)) yield return d;

        if (anyToolCall && primeiraChamada != null && detalhes.Count > 0)
            GuardarRaciocinio(primeiraChamada, detalhes.Values);

        if (_verboseLogging)
            Console.WriteLine($"[STREAM-END][OpenRouter] updates={updates} finish={rawFinish ?? "none"} mode={splitter.Mode} final={splitter.FinalText.Length}ch tools={(anyToolCall ? "sim" : "nao")} reasoning_details={detalhes.Count}");

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

    private void GuardarRaciocinio(string idDaChamada, IEnumerable<JsonObject> detalhes)
    {
        var lista = new JsonArray();
        foreach (var d in detalhes) lista.Add(d.DeepClone());
        _raciocinioPorChamada[idDaChamada] = new RaciocinioGuardado(lista.ToJsonString(), Interlocked.Read(ref _requisicoes));

        if (_raciocinioPorChamada.Count > TetoDoMapaDeRaciocinio) PodarRaciocinio();
    }

    /// <summary>
    /// Tira do mapa os raciocínios enviados há mais tempo, até <see cref="AlvoDaPoda"/>. Nunca
    /// zera: ver <see cref="TetoDoMapaDeRaciocinio"/>.
    /// </summary>
    private void PodarRaciocinio()
    {
        lock (_poda)
        {
            int sobra = _raciocinioPorChamada.Count - AlvoDaPoda;
            if (sobra <= 0) return;

            foreach (var velho in _raciocinioPorChamada
                         .OrderBy(e => Interlocked.Read(ref e.Value.VistoEm))
                         .Take(sobra)
                         .Select(e => e.Key)
                         .ToList())
                _raciocinioPorChamada.TryRemove(velho, out _);
        }
    }

    /// <summary>Quantos raciocínios estão guardados. Para ensaio da poda.</summary>
    public int RaciociniosGuardados => _raciocinioPorChamada.Count;

    /// <summary>
    /// Junta os pedaços de <c>reasoning_details</c> pelo <c>index</c>. O texto chega aos poucos,
    /// como o <c>content</c>; a assinatura e o formato chegam uma vez, em geral no último pedaço.
    /// </summary>
    public static void JuntarDetalhes(SortedDictionary<int, JsonObject> acumulado, IReadOnlyList<JsonObject>? pedacos)
    {
        if (pedacos == null) return;

        foreach (var pedaco in pedacos)
        {
            int indice = pedaco["index"] is JsonValue iv && iv.TryGetValue<int>(out var n) ? n : acumulado.Count;

            if (!acumulado.TryGetValue(indice, out var alvo))
            {
                acumulado[indice] = (JsonObject)pedaco.DeepClone();
                continue;
            }

            foreach (var (nome, valor) in pedaco)
            {
                if (valor == null) continue;

                bool concatena = nome is "text" or "summary" or "data"
                                 && valor is JsonValue sv && sv.TryGetValue<string>(out _)
                                 && alvo[nome] is JsonValue av && av.TryGetValue<string>(out _);

                alvo[nome] = concatena
                    ? (string)alvo[nome]! + (string)valor!
                    : valor.DeepClone();
            }
        }
    }

    /// <summary>Uma chamada de ferramenta em pedaços, como vem no <c>delta.tool_calls</c>.</summary>
    public sealed record PedacoDeChamada(int Indice, string? Id, string? Nome, string? Argumentos);

    /// <summary>O que um evento SSE traz, já separado.</summary>
    public sealed record Trecho(
        string? Texto, string? Raciocinio, IReadOnlyList<PedacoDeChamada> Chamadas,
        string? Fim, StreamChunk.Usage? Uso, string? Erro,
        IReadOnlyList<JsonObject>? DetalhesDoRaciocinio = null, string? Provedor = null);

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

        // Quem atendeu, no nível raiz de cada evento. É o provedor e não o modelo que guarda o
        // cache: se ele muda entre voltas, a volta seguinte paga o prompt inteiro de novo.
        string? provedor = Texto(raiz, "provider");

        string? texto = null, raciocinio = null, fim = null;
        var chamadas = new List<PedacoDeChamada>();
        List<JsonObject>? detalhes = null;

        if (raiz.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var escolha = choices[0];

            if (escolha.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String)
                fim = f.GetString();

            // O stream traz "delta"; a resposta sem stream traz "message", com os mesmos campos.
            if ((escolha.TryGetProperty("delta", out var delta) || escolha.TryGetProperty("message", out delta))
                && delta.ValueKind == JsonValueKind.Object)
            {
                texto = Texto(delta, "content");
                raciocinio = Texto(delta, "reasoning");

                if (delta.TryGetProperty("reasoning_details", out var rd) && rd.ValueKind == JsonValueKind.Array)
                {
                    detalhes = new List<JsonObject>();
                    foreach (var item in rd.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object && JsonNode.Parse(item.GetRawText()) is JsonObject o)
                            detalhes.Add(o);
                }

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

            decimal? custo = u.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var dc)
                ? dc
                : null;

            uso = new StreamChunk.Usage(Numero(u, "prompt_tokens"), Numero(u, "completion_tokens"), cache, null, custo, provedor);
        }

        return new Trecho(texto, raciocinio, chamadas, fim, uso, erro, detalhes, provedor);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Chamada única (resumo, triagem)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Chamada única, feita POR STREAM e juntada aqui. Uma resposta sem stream fica em silêncio
    /// até o fim, e aí não há como separar modelo trabalhando de conexão morta; pelo stream, o
    /// keep-alive do OpenRouter continua chegando e o prazo de silêncio vale.
    /// </summary>
    public async Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        CancellationToken ct)
    {
        var texto = new StringBuilder();
        int? entrada = null, saida = null;
        decimal? custo = null;

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
                    custo = u.CustoUsd ?? custo;
                    break;
            }
        }

        return new ChatCompletionResult(texto.ToString(), entrada, saida, custo);
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
            404 when corpo.Contains("No endpoints", StringComparison.OrdinalIgnoreCase) =>
                $"OpenRouter: nenhum provedor de '{Model}' atende às exigências desta requisição (404) — "
                + (_semColetaDeDados
                    ? "ferramentas, raciocínio ou não guardar os dados. Desligar \"Só provedores que não guardam dados\" em Configurações › Conexão LLM amplia a escolha."
                    : "ferramentas ou raciocínio. Escolha outro modelo em Configurações › Conexão LLM."),
            404 => $"OpenRouter não encontrou o modelo '{Model}' (404). Escolha outro em Configurações › Conexão LLM. {detalhe}",
            429 => "OpenRouter: limite de requisições atingido (429), mesmo depois de esperar. Tente de novo em instantes.",
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
