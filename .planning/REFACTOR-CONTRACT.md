# REFACTOR CONTRACT — AIB Windows (cognitive core)

**Status:** authoritative. Every later phase codes against this file.
**Restore point:** `0a46cdd`.
**Scope:** `AIBWindows/Services/**`, `AIBWindows/App.xaml.cs`, plumbing-only edits in `AIBWindows/Views/*.xaml.cs`.
**Frozen:** every `.xaml` file. No visual, animation, layout or timing change (RULE 1).

Language rule: code comments and user-facing strings are **pt-BR**; identifiers are English.
This document is English because `.planning/**` is English.

---

## 0. Target shape

```
App (composition root, owns lifetime — the app lives in the tray)
 ├─ HttpClient                 (single, Timeout = InfiniteTimeSpan)
 ├─ SettingsService            (single instance, in-memory cache, invalidated on save)
 ├─ ToolRegistry               (single instance)
 ├─ ChatProviderFactory        (rebuilds the provider only when provider/model/url change)
 ├─ ConversationService        (SOLE owner of history; all access synchronized)
 │   ├─ AgentLoop              (ReAct orchestration; knows nothing about HTTP or stream parsing)
 │   │   ├─ IChatProvider      (OllamaProvider | OpenAiProvider)
 │   │   └─ ToolRegistry
 │   └─ WarmupService          (heartbeat / keep_alive = -1, on a throwaway message list)
 └─ ChatWindow                 (receives ConversationService + SettingsService)
```

Rules that hold across all phases:

* No DI container. Manual constructor injection only.
* No new NuGet packages.
* `AIBWindows/Services/OpenAIService.cs` is deleted in Phase 4, not before.
* `OllamaNativeClient`'s public ctor and method names stay source-compatible with
  `AIB.Tests/OllamaNativeClientTests.cs` (see §2.4).

Namespaces: existing types keep `AIB.Services`. New provider-layer types live in
`AIBWindows/Services/Ai/` with namespace **`AIB.Services.Ai`**. New orchestration types live in
`AIBWindows/Services/Agent/` with namespace **`AIB.Services.Agent`**.

---

## 1. Transport-neutral value types

File: `AIBWindows/Services/Ai/StreamChunk.cs` — namespace `AIB.Services.Ai`.

```csharp
/// <summary>Canal do texto emitido pelo provider. Classificar é trabalho do provider.</summary>
public enum TextChannel
{
    /// <summary>Resposta final — vai para o usuário.</summary>
    Final = 0,
    /// <summary>Raciocínio interno (&lt;think&gt;, canal analysis/commentary) — só console.</summary>
    Reasoning = 1
}

public enum StreamFinishReason
{
    /// <summary>O modelo terminou a resposta em texto.</summary>
    Stop = 0,
    /// <summary>O modelo pediu uma ou mais ferramentas.</summary>
    ToolCalls = 1,
    /// <summary>O stream acabou por limite de tokens do provider.</summary>
    Length = 2,
    /// <summary>O stream acabou sem motivo declarado.</summary>
    Unknown = 3
}

/// <summary>
/// Unidade única do stream de um provider. A pergunta "isso é texto ou tool call?"
/// é respondida AQUI, uma vez por provider — nunca no orquestrador.
/// </summary>
public abstract record StreamChunk
{
    private StreamChunk() { }

    /// <summary>Pedaço de texto já classificado por canal.</summary>
    public sealed record TextDelta(string Text, TextChannel Channel) : StreamChunk;

    /// <summary>
    /// Pedaço de uma tool call. <paramref name="CallKey"/> é a chave de agregação:
    /// deltas com a mesma CallKey pertencem à MESMA chamada e são concatenados na ordem
    /// de chegada; CallKeys distintas são chamadas distintas. Ver §2.1.
    /// </summary>
    public sealed record ToolCallDelta(
        string CallKey,
        string? ToolCallId,
        string? FunctionName,
        string? ArgumentsJsonFragment) : StreamChunk;

    /// <summary>Contadores de uso reportados pelo provider (podem chegar várias vezes).</summary>
    public sealed record Usage(int? PromptEvalCount, int? EvalCount) : StreamChunk;

    /// <summary>Último chunk de um stream. Emitido exatamente uma vez, sempre.</summary>
    public sealed record Done(StreamFinishReason Reason, string? RawFinishReason) : StreamChunk;
}
```

**Emission contract (binding on every provider):**

1. `Done` is emitted **exactly once**, as the last chunk, on every non-throwing enumeration.
2. `TextDelta` with `Channel == Final` carries text already passed through
   `ChatTemplateSanitizer.Strip` (§2.2). The orchestrator never strips anything.
3. `TextDelta` with `Channel == Reasoning` is raw (not stripped) — it is console-only diagnostics.
4. A provider that produced any `ToolCallDelta` must report `Done(StreamFinishReason.ToolCalls, …)`.
5. Empty-string `TextDelta` must not be emitted.

File: `AIBWindows/Services/Ai/ChatRequestOptions.cs`

```csharp
/// <summary>Parâmetros de requisição independentes de provider.</summary>
/// <param name="Temperature">Temperatura. Default 0.1f — igual ao valor atual em produção.</param>
/// <param name="NumCtx">Janela de contexto pedida ao Ollama (ignorada pelo OpenAI).</param>
/// <param name="KeepAliveSeconds">keep_alive do Ollama; -1 trava na VRAM. null = não enviar.</param>
public sealed record ChatRequestOptions(
    float Temperature = 0.1f,
    int NumCtx = 16384,
    int? KeepAliveSeconds = null)
{
    public static ChatRequestOptions Default { get; } = new();
}
```

File: `AIBWindows/Services/Ai/ChatCompletionResult.cs`

```csharp
/// <summary>Resultado de uma chamada não-streaming (warmup e AskStateless).</summary>
public sealed record ChatCompletionResult(string Text, int? PromptEvalCount, int? EvalCount);
```

---

## 2. Provider layer

### 2.1 `IChatProvider`

File: `AIBWindows/Services/Ai/IChatProvider.cs`

```csharp
public interface IChatProvider
{
    /// <summary>Nome do provider para log/diagnóstico ("Ollama", "OpenAI").</summary>
    string Name { get; }

    /// <summary>Modelo efetivamente configurado neste provider.</summary>
    string Model { get; }

    /// <summary>
    /// Streaming de uma única resposta do modelo. NUNCA muta <paramref name="messages"/>.
    /// Deve ser 100% assíncrono: nenhuma leitura bloqueante de socket (ver §9).
    /// </summary>
    IAsyncEnumerable<StreamChunk> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        CancellationToken ct);

    /// <summary>Chamada não-streaming. Usada pelo warmup e por AskStatelessAsync.</summary>
    Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        CancellationToken ct);

    /// <summary>
    /// Prefill de carregamento do modelo. No Ollama: POST /api/generate com keep_alive=-1.
    /// No OpenAI: no-op. Nunca lança — falha vira log.
    /// </summary>
    Task WarmupAsync(CancellationToken ct);
}
```

`tools` is always non-null; pass `Array.Empty<ChatTool>()` to disable tools. A provider must send
tool definitions on **every** path when the list is non-empty — this is the fix for "tools never
attached on the OpenAI path" (`chatOptions.Tools` was never populated in `StreamResponseAsync`).

**CallKey contract.** `CallKey` is opaque to the orchestrator and unique *per logical tool call
within one `StreamAsync` enumeration*.

* `OpenAiProvider`: `CallKey = "oa:" + toolCallUpdate.Index`. The OpenAI SDK's `Index` is stable
  across chunks of one response, so this preserves today's correct accumulation.
* `OllamaProvider`: Ollama sends **whole** tool calls inside one NDJSON line and restarts the
  array index at 0 on every line. Indexing by that array position merges distinct calls (current
  bug). The provider therefore keeps a per-enumeration counter `int emittedCalls` and assigns
  `CallKey = "ol:" + (emittedCalls++)` to each element of each `tool_calls` array it sees,
  emitting the complete `FunctionName` and `ArgumentsJsonFragment` in a single `ToolCallDelta`.
  The counter is a local of the iterator, never a field of the provider instance.

The orchestrator accumulates by `CallKey` in **first-appearance order** (§4.2).

### 2.2 Text classification (the channel state machine, moved)

File: `AIBWindows/Services/Ai/ChannelSplitter.cs` — namespace `AIB.Services.Ai`.

This is the current `mode == 0/1/2` state machine, lifted out of `StreamResponseAsync` and made a
per-stream object. One instance per `StreamAsync` enumeration.

```csharp
/// <summary>
/// Separa o texto cru do modelo em canal Final e canal Reasoning, lidando com
/// &lt;think&gt;…&lt;/think&gt; e com os markers de canal estilo Harmony (gemma4).
/// Mantém um carry buffer para markers partidos entre chunks.
/// Uma instância por stream — tem estado.
/// </summary>
public sealed class ChannelSplitter
{
    /// <summary>Consome um pedaço cru e devolve os deltas já classificados, na ordem.</summary>
    public IReadOnlyList<StreamChunk.TextDelta> Push(string rawChunk);

    /// <summary>
    /// Fecha o stream: processa o carry retido e aplica o flush em camadas
    /// (WaitingFinal → o buffer É a resposta; InsideThink não fechado → vaza o think
    /// como resposta se nada foi emitido e não houve tool call).
    /// </summary>
    public IReadOnlyList<StreamChunk.TextDelta> Flush(bool anyToolCallSeen);

    /// <summary>Todo o texto classificado como Final neste stream. Entrada do healer.</summary>
    public string FinalText { get; }

    /// <summary>Diagnóstico para o log [STREAM-END].</summary>
    public int Mode { get; }
    public bool AnythingEmittedAsFinal { get; }
}
```

Behavioural requirements, all preserved from the current code:

* markers watched: `<think>`, `</think>`, `<channel|>`, `<|channel|>`, `<|message|>`;
* dynamic carry via the existing `FindTrailingMarkerPrefix` algorithm (moved, unchanged);
* `Final` output is passed through `ChatTemplateSanitizer.Strip` before being returned;
* `Reasoning` output is returned unstripped;
* on `Flush`, mode 2 with a non-empty wait buffer emits the buffer as `Final`;
* on `Flush`, mode 1 with a non-empty think buffer emits it as `Final` **only if** nothing was
  emitted as Final yet **and** `anyToolCallSeen == false`.

One deliberate divergence from today, and the only one: the current mid-stream carry path sets
`mode = 0` after `</think>` while the final-carry path sets `mode = 2` for the same input.
`ChannelSplitter` uses **`mode = 0`** (the mid-stream behaviour) in both places; the end-of-stream
flush already covers the "model never opened a final channel" case.

File: `AIBWindows/Services/Ai/ChatTemplateSanitizer.cs`

```csharp
/// <summary>Remove tokens de chat-template que vazam crus de alguns modelos.</summary>
public static class ChatTemplateSanitizer
{
    /// <summary>Lista movida sem alteração de OpenAIService.ChatTemplateTokens.</summary>
    public static IReadOnlyList<string> Tokens { get; }

    public static string Strip(string text);
}
```

### 2.3 The regex healer (intentional feature — kept, fixed)

Lives **inside the provider layer**, behind a seam, and runs only at end-of-stream over
`ChannelSplitter.FinalText`. The orchestrator has no knowledge of it.

File: `AIBWindows/Services/Ai/IToolCallHealer.cs`

```csharp
/// <summary>Uma tool call reconstruída a partir de texto que o modelo escreveu como prosa.</summary>
/// <param name="ToolName">Nome da ferramenta, já validado contra as tools ativas.</param>
/// <param name="ArgumentsJson">JSON de argumentos, SEMPRE produzido por JsonSerializer.</param>
/// <param name="MatchedText">Trecho que casou — só para log técnico.</param>
public sealed record HealedToolCall(string ToolName, string ArgumentsJson, string MatchedText);

/// <summary>
/// Cura a alucinação de modelos pequenos que escrevem a chamada de ferramenta como texto
/// em vez de usar function calling. Recurso INTENCIONAL (doc 03 §3.1).
/// </summary>
public interface IToolCallHealer
{
    /// <summary>
    /// Tenta extrair uma tool call de <paramref name="finalChannelText"/>.
    /// CONTRATO: só recebe texto já classificado como canal FINAL — nunca conteúdo de
    /// &lt;think&gt;. Retorna false se não casar, se a ferramenta não existir em
    /// <paramref name="activeTools"/>, ou se os argumentos não puderem ser mapeados
    /// para o schema real da ferramenta.
    /// </summary>
    bool TryHeal(
        string finalChannelText,
        IReadOnlyList<ChatTool> activeTools,
        out HealedToolCall healed);
}
```

File: `AIBWindows/Services/Ai/RegexToolCallHealer.cs`

```csharp
public sealed class RegexToolCallHealer : IToolCallHealer
{
    // Padrão preservado: Action: nome_tool(...) / Ação: nome_tool(...)
    private static readonly Regex TextActionRegex = new(
        @"(?:Action|Ação|action):\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\(([^)]*)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool TryHeal(string finalChannelText, IReadOnlyList<ChatTool> activeTools, out HealedToolCall healed);
}
```

Argument-binding algorithm, in order. The three defects being fixed are (a) scanning think
content, (b) string-interpolated JSON, (c) invented parameter names:

1. No regex match → `false`.
2. Tool name not found (case-insensitive) in `activeTools` → `false`.
3. Read the target tool's schema from `ChatTool.FunctionParameters` and collect the `properties`
   names plus the `required` array.
4. Trimmed capture group 2:
   * empty → `ArgumentsJson = "{}"`, valid only when `required` is empty, otherwise `false`;
   * starts with `{` and parses as a JSON object → re-serialize with `JsonSerializer`, keeping
     only properties present in the schema; if any `required` name is missing → `false`;
   * matches one or more `name=value` / `name: value` pairs whose names are all in the schema →
     build a `Dictionary<string, object?>` and serialize;
   * otherwise treat it as a single scalar and bind it to `required[0]`; if `required` is empty,
     bind to the first declared property; if the schema declares no property at all → `false`.
     Surrounding single or double quotes are stripped from the scalar.
5. `ArgumentsJson` is **always** produced by
   `JsonSerializer.Serialize(IDictionary<string, object?>)`. String interpolation into JSON is
   forbidden anywhere in this type.

Provider integration (identical in both providers):

* accumulate `ChannelSplitter.FinalText` during the stream;
* at end of stream, if **zero** native `ToolCallDelta` were emitted **and** `tools` is non-empty,
  call `TryHeal(FinalText, tools, out var healed)`;
* on success emit
  `new StreamChunk.ToolCallDelta("heal:0", "fallback_" + Guid.NewGuid().ToString("N"), healed.ToolName, healed.ArgumentsJson)`
  followed by `Done(StreamFinishReason.ToolCalls, "healed")`;
* on failure emit `Done` with the provider's real finish reason.

Accepted, documented behaviour change: prose preceding a healed call may already have reached the
user. That is also true today — the current code only `continue`s *after* earlier chunks were
yielded. Think-block content can no longer reach the healer, which is the actual defect.

### 2.4 `OllamaNativeClient` (kept — best-built class in the project)

File: `AIBWindows/Services/OllamaNativeClient.cs` — **modified**, not replaced.

Mandatory changes, and nothing else:

1. `while (!reader.EndOfStream && …)` becomes a fully async loop:
   ```csharp
   string? line;
   while (!ct.IsCancellationRequested &&
          (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
   ```
   `StreamReader.EndOfStream` performs a **synchronous blocking socket read**; today it runs on
   the UI thread (§9). This is the single most important line in the refactor.
2. `List<ChatMessage> history` → `IReadOnlyList<ChatMessage> history` on `StreamChatAsync`,
   `CompleteChatAsync` and `FormatMessages`. Source-compatible with the existing test, which
   passes a `List<ChatMessage>`.
3. Add `int numCtx = 16384` and `int? keepAliveSeconds = null` as optional trailing parameters to
   both `StreamChatAsync` and `CompleteChatAsync`, so `ChatRequestOptions` can reach the wire.
   **Constraint:** `AIB.Tests/OllamaNativeClientTests` asserts that the two request bodies have
   identical root and `options` property sets. Any root property added to one request object must
   be added to the other, under the same null-suppression rule.
4. Ctor signature, class name, `ChatUpdateDto` and `ToolCallUpdateDto` stay exactly as they are.

`ChatUpdateDto` remains the Ollama wire DTO. After this refactor it is an implementation detail of
`OllamaNativeClient` + `OllamaProvider`; nothing else may reference it.

### 2.5 Concrete providers

File: `AIBWindows/Services/Ai/OllamaProvider.cs`

```csharp
public sealed class OllamaProvider : IChatProvider
{
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
        bool verboseLogging);

    public string Name => "Ollama";
    public string Model { get; }

    public IAsyncEnumerable<StreamChunk> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, ChatRequestOptions options, CancellationToken ct);
    public Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, ChatRequestOptions options, CancellationToken ct);
    public Task WarmupAsync(CancellationToken ct);
}
```

`WarmupAsync` posts `{ model, keep_alive = -1, options = { num_ctx = 16384 } }` to
`{baseUrl}/api/generate` — the existing payload, unchanged, including its explanatory comment
block. It catches its own exceptions and logs `[WARMUP ERRO]`.

File: `AIBWindows/Services/Ai/OpenAiProvider.cs`

```csharp
public sealed class OpenAiProvider : IChatProvider
{
    public OpenAiProvider(ChatClient client, string model, IToolCallHealer healer, bool verboseLogging);

    public string Name => "OpenAI";
    public string Model { get; }

    public IAsyncEnumerable<StreamChunk> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, ChatRequestOptions options, CancellationToken ct);
    public Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, ChatRequestOptions options, CancellationToken ct);
    public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
}
```

`OpenAiProvider` builds `ChatCompletionOptions { Temperature = options.Temperature }` and **adds
every tool in `tools` to `chatOptions.Tools`** — the missing line that made the OpenAI path
tool-blind. It maps `Usage.InputTokenDetails.CachedTokenCount` into `StreamChunk.Usage.EvalCount`,
preserving the current mapping.

### 2.6 Provider factory

Files: `AIBWindows/Services/Ai/IChatProviderFactory.cs`, `AIBWindows/Services/Ai/ChatProviderFactory.cs`

```csharp
public interface IChatProviderFactory
{
    /// <summary>
    /// Devolve o provider correspondente às settings atuais. Reaproveita a instância
    /// anterior enquanto provider/modelo/URL não mudarem (substitui EnsureClient).
    /// </summary>
    IChatProvider GetProvider(UserAppSettings settings);
}

public sealed class ChatProviderFactory : IChatProviderFactory
{
    public ChatProviderFactory(HttpClient httpClient, IToolCallHealer healer);

    public IChatProvider GetProvider(UserAppSettings settings);
}
```

Rules moved here from `EnsureClient` and `AskStatelessAsync`, unchanged in behaviour:

* provider selection is `settings.AiProvider == "Ollama"` → `OllamaProvider`, else `OpenAiProvider`;
* Ollama defaults: url `http://127.0.0.1:11434/v1`, key `ollama`, `localhost` → `127.0.0.1`;
* `ApiKey == "use-vault"` → `CredentialService.RetrieveCredential("openai", "ApiKey")`, and an
  `ERRO`-prefixed result falls back to `"placeholder"`;
* empty key → `"placeholder"`;
* `OpenAIClientOptions.NetworkTimeout = Timeout.InfiniteTimeSpan`; endpoint set when url non-empty;
* cache key is the tuple `(AiProvider, ModelName, ApiUrl)`; a change rebuilds and logs
  `[AI] Cliente inicializado: {model} @ {url}`;
* `GetProvider` is thread-safe (`lock`), because warmup and a turn can race at startup.

---

## 3. History ownership

### 3.1 `IMessageStore`

File: `AIBWindows/Services/Agent/IMessageStore.cs` — namespace `AIB.Services.Agent`.

The seam that lets `AgentLoop` run against either the live conversation or a throwaway list.
`AgentLoop` **never** holds a `List<ChatMessage>`.

```csharp
/// <summary>
/// Dono de uma lista de mensagens. Toda leitura devolve CÓPIA; toda escrita é atômica.
/// Implementações: ConversationService (viva, sincronizada) e EphemeralMessageStore (warmup).
/// </summary>
public interface IMessageStore
{
    /// <summary>Cópia coerente do histórico. O que sai daqui nunca é mutado por terceiros.</summary>
    IReadOnlyList<ChatMessage> Snapshot();

    /// <summary>Anexa a mensagem assistant que carrega as tool_calls da iteração.</summary>
    void AppendAssistantToolCalls(IReadOnlyList<ChatToolCall> calls);

    /// <summary>Anexa o ToolChatMessage correspondente a uma tool_call já anexada.</summary>
    void AppendToolResult(string toolCallId, string result);

    /// <summary>Anexa a resposta final em texto (blocos &lt;think&gt; preservados de propósito).</summary>
    void AppendAssistantText(string text);

    /// <summary>Poda por orçamento de tokens do nível. Nunca remove o índice 0.</summary>
    void Trim(int userLevel);

    /// <summary>Contagem atual de tokens do histórico.</summary>
    int CountTokens();

    /// <summary>Publica o contador para a UI. No store efêmero é no-op.</summary>
    void NotifyTokenCount(int userLevel, int? cachedTokens = null);
}
```

File: `AIBWindows/Services/Agent/EphemeralMessageStore.cs`

```csharp
/// <summary>
/// Store descartável do warmup: começa como cópia do histórico real, recebe a mensagem
/// fantasma de heartbeat e é jogado fora inteiro. Nunca toca o histórico vivo — é isso
/// que elimina o RemoveRange que apagava mensagens de uma requisição concorrente.
/// </summary>
public sealed class EphemeralMessageStore : IMessageStore
{
    public EphemeralMessageStore(IReadOnlyList<ChatMessage> seed, TokenCounter tokenCounter);

    /// <summary>Anexa a mensagem de usuário do heartbeat.</summary>
    public void AppendUserMessage(string text);

    // Trim e NotifyTokenCount são no-ops.
}
```

### 3.2 `ConversationService`

File: `AIBWindows/Services/ConversationService.cs` — namespace `AIB.Services`.

Sole owner of the live history. The public surface deliberately mirrors today's `OpenAIService`
so the wiring phase stays tiny.

```csharp
public sealed class ConversationService : IMessageStore
{
    public ConversationService(
        SettingsService settingsService,
        ToolRegistry toolRegistry,
        AgentLoop agentLoop,
        TokenCounter tokenCounter,
        IChatProviderFactory providerFactory);

    // ── Eventos (mesmas assinaturas de hoje) ──────────────────────────────
    public event Action<int, int, int?>? OnTokenCountChanged;
    public event Action<bool>? OnWarmupStateChanged;

    public ToolRegistry Registry { get; }
    public int CurrentTokenCount { get; }

    /// <summary>Salva a sessão atual e recria o system prompt (SOUL/skills/home dir).</summary>
    public void ResetHistory();

    /// <summary>
    /// Injeta um chat recuperado da sidebar. Substitui os dois History.Add() que a
    /// ChatWindow fazia direto na lista.
    /// </summary>
    public void AppendRecoveredContext(string title, string content);

    /// <summary>
    /// Turno completo do usuário. Mesma assinatura de OpenAIService.StreamResponseAsync
    /// para manter a ChatWindow como plumbing-only.
    /// </summary>
    public IAsyncEnumerable<string> StreamResponseAsync(
        string userMessage,
        Action<string>? onTechnicalContent = null);

    public void CancelGeneration();

    /// <summary>Aquecimento em background. Chamado pelo App, nunca por um construtor.</summary>
    public Task StartWarmupAsync();

    /// <summary>Resposta única e sem estado (Shadow Assistant, utilitários).</summary>
    public Task<string> AskStatelessAsync(string systemPrompt, string userPrompt, string? overrideModel = null);

    /// <summary>Cópia do histórico para persistência/diagnóstico. Somente leitura.</summary>
    public IReadOnlyList<ChatMessage> SnapshotHistory();

    // IMessageStore — ver §3.1
}
```

Removed from the public surface: `List<ChatMessage> History { get; }`. Nothing outside may hold
the live list. `SendMessageStreamAsync` is dropped (it was an unused alias).
The `SYSTEM_PROMPT` constant and the `ResetHistory` body (skills listing, home dir, the
`SendSystemPrompt` setting, `ChatHistoryService.SaveCurrentSession`) move here verbatim.

**Synchronization strategy**

* `private readonly List<ChatMessage> _history = new();`
* `private readonly object _gate = new();` — every read and every write of `_history` happens
  inside `lock (_gate)`. No exceptions, including `ResetHistory`, `CountTokens` and `Trim`.
* `Snapshot()` is `lock (_gate) { return _history.ToArray(); }` — an immutable copy. Providers and
  `ChatHistoryService` only ever see copies.
* No `await` inside a `lock`. Token counting is synchronous, and `Trim` is synchronous: the async
  `TrimHistoryAsync` signature existed only for a summarizer that was already removed.
* `private readonly SemaphoreSlim _turnGate = new(1, 1);` — `StreamResponseAsync` and
  `AskStatelessAsync` take it, so two turns never interleave into the history. Released in a
  `finally`, including on cancellation.
* `CancelGeneration` only cancels the CTS; it never touches `_history`.

**Trimming and token counting**

* `Trim(int userLevel)` is the current `TrimHistoryAsync` body minus `async`: sliding window from
  index 1, hard-lock on index 0 (system prompt), removal of orphaned `ToolChatMessage`s that
  followed a removed assistant-with-tool_calls, `safetyCounter < 200`.
* `AgentLoop` calls `store.Trim(userLevel)` **at the end of every iteration**, not only on the
  plain-text branch. That is the fix for "trimming only reachable on the plain-text branch".
* `NotifyTokenCount` raises `OnTokenCountChanged(total, LevelService.GetMaxTokensForLevel(level), cached)`.

File: `AIBWindows/Services/TokenCounter.cs`

```csharp
/// <summary>Contagem de tokens do histórico. Instância única — o tokenizer é caro de criar.</summary>
public sealed class TokenCounter
{
    public TokenCounter();                                        // TiktokenTokenizer.CreateForModel("gpt-4o")
    public int CountText(string text);
    public int CountMessages(IEnumerable<ChatMessage> messages);  // corpo atual de CalculateCurrentTokens
}
```

`TokenCounter` is safe for concurrent `Count*` calls (the tokenizer is stateless); callers must
still hold `_gate` when the enumerable is the live list.

### 3.3 `WarmupService`

File: `AIBWindows/Services/Agent/WarmupService.cs`

```csharp
/// <summary>
/// Aquecimento intencional (doc 03 §1): trava o modelo na VRAM com keep_alive=-1 e
/// compila a gramática das tools com um heartbeat fantasma. NÃO é disparado por
/// construtor — o App chama explicitamente depois que a UI existe.
/// </summary>
public sealed class WarmupService
{
    public WarmupService(
        SettingsService settingsService,
        ToolRegistry toolRegistry,
        IChatProviderFactory providerFactory,
        TokenCounter tokenCounter);

    /// <summary>Disparado no início e no fim do aquecimento (true/false).</summary>
    public event Action<bool>? OnWarmupStateChanged;

    /// <summary>Métricas do prefill, para o contador de tokens da UI.</summary>
    public event Action<int, int, int?>? OnTokenCountChanged;

    /// <summary>Roda o aquecimento inteiro. Nunca lança.</summary>
    public Task RunAsync(IReadOnlyList<ChatMessage> historySeed, CancellationToken ct);
}
```

`RunAsync` builds an `EphemeralMessageStore(historySeed, tokenCounter)`, appends the
`[SYSTEM_HEARTBEAT]` user message **into the ephemeral store**, calls `provider.WarmupAsync`, then
`provider.CompleteAsync` with the active tools and
`ChatRequestOptions.Default with { KeepAliveSeconds = -1 }`, logs the result, and drops the store.
There is no `RemoveRange`, no `finally` cleanup of the live history, and no code path in which the
live history is written by warmup. It returns early when `settings.AiProvider != "Ollama"`, as
today.

`ConversationService` owns a `WarmupService`, forwards both of its events to its own, and exposes
`StartWarmupAsync()`, which passes `SnapshotHistory()` as the seed.

---

## 4. `AgentLoop`

File: `AIBWindows/Services/Agent/AgentLoop.cs` — namespace `AIB.Services.Agent`.
Event types: `AIBWindows/Services/Agent/AgentEvent.cs`.

### 4.1 Types

```csharp
/// <summary>Como o turno terminou. Visível para o chamador — nunca um sucesso silencioso.</summary>
public enum TurnOutcome
{
    /// <summary>O modelo entregou uma resposta final.</summary>
    Answered = 0,
    /// <summary>O teto de iterações ReAct estourou com o modelo ainda pedindo ferramentas.</summary>
    IterationLimitReached = 1,
    /// <summary>O stream terminou sem texto e sem tool call (modelo devolveu vazio).</summary>
    EmptyResponse = 2
}

/// <summary>Evento do turno. A ConversationService traduz para o que a UI consome.</summary>
public abstract record AgentEvent
{
    private AgentEvent() { }

    /// <summary>Texto de canal final — vai para o balão do usuário.</summary>
    public sealed record Text(string Value) : AgentEvent;

    /// <summary>Conteúdo técnico: think, logs de ferramenta, diagnóstico do stream.</summary>
    public sealed record Technical(string Value) : AgentEvent;

    /// <summary>Contador de tokens durante o stream.</summary>
    public sealed record TokenUsage(int Total, int Max, int? Cached) : AgentEvent;

    /// <summary>Fim do turno. Emitido exatamente uma vez, por último.</summary>
    public sealed record Completed(TurnOutcome Outcome, int IterationsUsed) : AgentEvent;
}

/// <summary>Parâmetros de um turno. AgentLoop não guarda estado entre turnos.</summary>
public sealed record AgentTurnRequest(
    IMessageStore Store,
    IReadOnlyList<ChatTool> Tools,
    int UserLevel,
    ChatRequestOptions Options,
    bool VerboseLogging);

public sealed class AgentLoop
{
    /// <summary>Teto de iterações ReAct. 18 — valor atual, mantido.</summary>
    public const int MaxIterations = 18;

    public AgentLoop(ToolRegistry toolRegistry, IChatProviderFactory providerFactory, SettingsService settingsService);

    /// <summary>
    /// Orquestração ReAct. Não conhece HTTP, não faz parsing de stream, não remove
    /// template token, não roda regex — só decide o que fazer com StreamChunks.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> RunAsync(AgentTurnRequest request, CancellationToken ct);
}
```

### 4.2 Iteration algorithm (normative)

For `iteration = 1 .. MaxIterations`:

1. `var settings = _settingsService.LoadSettings();` — **once per iteration**, served from the
   cache (§5). `var provider = _providerFactory.GetProvider(settings);`
2. `var tools = settings.EnableIntelligentTools ? request.Tools : Array.Empty<ChatTool>();`
3. `var messages = request.Store.Snapshot();` — a copy; the provider gets nothing else.
4. Enumerate `provider.StreamAsync(messages, tools, request.Options, ct)`:
   * `TextDelta { Channel: Final }` → yield `AgentEvent.Text`, and append to a local `finalText`;
   * `TextDelta { Channel: Reasoning }` → yield `AgentEvent.Technical`;
   * `ToolCallDelta` → accumulate into `calls`, a `List<ToolCallAccumulator>` plus a
     `Dictionary<string,int>` from `CallKey` to list position. First appearance defines order;
     `ToolCallId` and `FunctionName` are written only while still empty; `ArgumentsJsonFragment`
     is appended to a `StringBuilder`;
   * `Usage` → recompute and yield `AgentEvent.TokenUsage`
     (`baselineTokens + streamedTokens`, `LevelService.GetMaxTokensForLevel(level)`, cached);
   * `Done` → record the finish reason and stop enumerating.
5. If `calls.Count > 0`:
   * `store.AppendAssistantToolCalls(...)` with every call, in order, built via
     `ChatToolCall.CreateFunctionToolCall(id, name, BinaryData.FromString(args))`;
     a call whose accumulated arguments are empty gets `"{}"`;
   * yield `AgentEvent.Technical` per call (`[FERRAMENTA] Nome: … | Args: …`) and the
     `[PARALELO] Executando N ferramentas em paralelo:` line when `calls.Count > 1`. These
     technical strings must stay byte-identical: `ChatWindow` regex-matches them to draw the
     "🔧 Usando ferramenta" line;
   * execute all calls with `Task.WhenAll` over `_toolRegistry.ExecuteToolAsync(name, args, level)`;
   * for each result, in original order: yield `AgentEvent.Technical`
     (`[FERRAMENTA] Resultado (nome): …`), then `store.AppendToolResult(id, result)`, then the
     `ContextService.AddRecentFile` bookkeeping, then `_toolRegistry.Refresh()` when the tool was
     `materialize_skill`;
   * `store.Trim(level)`; `store.NotifyTokenCount(level)`; continue the loop.
6. Else if `finalText` is non-empty: `store.AppendAssistantText(finalText)`; `store.Trim(level)`;
   `store.NotifyTokenCount(level)`; yield `AgentEvent.Completed(TurnOutcome.Answered, iteration)`;
   **return**.
7. Else: `store.Trim(level)`; `store.NotifyTokenCount(level)`;
   yield `AgentEvent.Completed(TurnOutcome.EmptyResponse, iteration)`; **return**.

If the loop ends without returning, the cap was exhausted while the model was still asking for
tools:

```csharp
yield return new AgentEvent.Completed(TurnOutcome.IterationLimitReached, MaxIterations);
```

**Cap exhaustion is never a silent success.** `ConversationService.StreamResponseAsync` translates
that event into a visible final chunk, yielded to the caller like any other text:

```
⚠️ Limite de 18 etapas atingido — a tarefa ficou incompleta. Peça para continuar.
```

and mirrors it to `onTechnicalContent` as
`[LOOP] Teto de {MaxIterations} iterações atingido — turno encerrado incompleto.`
`TurnOutcome.EmptyResponse` yields nothing extra: `ChatWindow` already renders
`*Ação executada com sucesso.*` when the accumulated text is blank, and that bubble is frozen UI.

Cancellation: `OperationCanceledException` propagates out of `RunAsync` and out of
`ConversationService.StreamResponseAsync`, exactly as today, so `ChatWindow`'s existing
`catch (Exception ex)` keeps producing the same error bubble. Provider exceptions likewise
propagate untouched.

`AgentLoop` holds **no** mutable field. Everything per-turn lives in locals of the iterator.
`ToolCallAccumulator` moves here as a private nested class.

---

## 5. `SettingsService`

File: `AIBWindows/Services/SettingsService.cs` — modified.

```csharp
public sealed class UserAppSettings
{
    // …campos existentes, inalterados…

    /// <summary>Cópia rasa. Todos os campos são string ou tipo de valor, então é cópia real.</summary>
    public UserAppSettings Clone() => (UserAppSettings)MemberwiseClone();
}

public sealed class SettingsService
{
    /// <summary>Usa o caminho corrente do DirectoryService, resolvido a cada operação.</summary>
    public SettingsService() : this(null) { }

    /// <summary>Caminho fixo — usado por testes.</summary>
    public SettingsService(string? settingsPath);

    /// <summary>Caminho efetivo neste momento.</summary>
    public string SettingsPath { get; }

    /// <summary>Settings do disco, servidas do cache. Devolve sempre uma cópia.</summary>
    public UserAppSettings LoadSettings();

    /// <summary>Grava criptografado e substitui o cache.</summary>
    public void SaveSettings(UserAppSettings settings);

    /// <summary>Descarta o cache. O próximo LoadSettings volta ao disco.</summary>
    public void InvalidateCache();

    public Task<List<string>> GetOllamaModelsAsync(string baseUrl);
}
```

Mandatory changes:

1. **Delete** `private static readonly string SettingsPath = DirectoryService.SettingsPath;`. It
   froze at type-load, so `DirectoryService.ApplyFromSettings` could never retarget it. Replace
   with `private string ResolvePath() => _explicitPath ?? DirectoryService.SettingsPath;`
   evaluated on every IO call, plus `public string SettingsPath => ResolvePath();`.
2. Cache fields: `private UserAppSettings? _cache;`, `private string? _cachedPath;`,
   `private readonly object _gate = new();`.
   `LoadSettings` returns `_cache.Clone()` when `_cache != null && _cachedPath == ResolvePath()`;
   otherwise it reads the file, DPAPI-decrypts, deserializes, stores `_cache` and `_cachedPath`,
   and returns a clone. This kills the 6+ DPAPI decryptions per message.
3. `SaveSettings` writes the file, then sets `_cache = settings.Clone()` and
   `_cachedPath = ResolvePath()` under `_gate`.
4. The legacy plaintext-migration fallback and the "file missing → write defaults" path are kept
   verbatim; both populate the cache on the way out.
5. `_httpClient` becomes `private static readonly HttpClient` — it is used only by
   `GetOllamaModelsAsync`, and the class was allocating one per instance.

Cache correctness depends on there being **one** `SettingsService` per process. §7 makes that true
by passing the App-owned instance into every window.

---

## 6. `ToolRegistry`, `ITool`, `LevelService`

Unchanged in this refactor. `ToolRegistry` is constructed **once**, in `App`, and injected — the
`new ToolRegistry()` at `OpenAIService.cs:59` disappears with the file. `RequiredLevel` gating
(doc 03 §3.2) stays exactly as it is, applied in `GetActiveTools(userLevel)` and again inside
`ExecuteToolAsync`.

---

## 7. Composition root and view plumbing

### 7.1 `App.xaml.cs` (modified)

```csharp
public partial class App : System.Windows.Application
{
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private SettingsService _settingsService = null!;
    private ToolRegistry _toolRegistry = null!;
    private TokenCounter _tokenCounter = null!;
    private IToolCallHealer _healer = null!;
    private IChatProviderFactory _providerFactory = null!;
    private AgentLoop _agentLoop = null!;
    private ConversationService _conversation = null!;

    private TaskbarIcon? _notifyIcon;
    private ChatWindow? _chatWindow;
}
```

`_settingsService` stops being a field initializer (`= new()`) so that it is built after
`DirectoryService.EnsureDirectories()`. `OnStartup` becomes, in order:

```csharp
GibberishVoiceService.Initialize();
DirectoryService.EnsureDirectories();
_settingsService = new SettingsService();          // antes de qualquer LoadSettings

// …caminho de CLI (e.Args) inalterado…

var settings = _settingsService.LoadSettings();
DirectoryService.ApplyFromSettings(settings);
_settingsService.InvalidateCache();                // o caminho de settings pode ter mudado

// …migração D-11 da ApiKey, inalterada, usando _settingsService…

_toolRegistry    = new ToolRegistry();
_tokenCounter    = new TokenCounter();
_healer          = new RegexToolCallHealer();
_providerFactory = new ChatProviderFactory(_httpClient, _healer);
_agentLoop       = new AgentLoop(_toolRegistry, _providerFactory, _settingsService);
_conversation    = new ConversationService(_settingsService, _toolRegistry, _agentLoop,
                                           _tokenCounter, _providerFactory);

_chatWindow = new ChatWindow(_conversation, _settingsService);

// …tray icon, menu de contexto e hotkey, inalterados…

_ = _conversation.StartWarmupAsync();              // depois da UI existir, nunca de um ctor
```

`ShowFirstRunWindow` becomes `new FirstRunWindow(_settingsService)`. `OnExit` disposes
`_httpClient` alongside `_notifyIcon`.

### 7.2 `ChatWindow.xaml.cs` (plumbing only — RULE 1)

Exactly these edits, nothing else:

```csharp
private readonly ConversationService _conversation;   // era OpenAIService _openAIService
private readonly SettingsService _settingsService;

public ChatWindow(ConversationService conversation, SettingsService settingsService)
{
    InitializeComponent();
    _settingsService = settingsService;               // era new SettingsService()
    _conversation = conversation;                     // era new OpenAIService(_settingsService)
    _conversation.OnTokenCountChanged += UpdateTokenCounterUI;
    _conversation.OnWarmupStateChanged += HandleWarmupState;
    _shadowService = new ShadowAssistantService(_conversation, _settingsService);
    // …resto do construtor inalterado…
}
```

Call-site renames, all mechanical:

| today | after |
|---|---|
| `_openAIService.CurrentTokenCount` | `_conversation.CurrentTokenCount` |
| `_openAIService.ResetHistory()` | `_conversation.ResetHistory()` |
| `_openAIService.Registry.GetCategorizedTools()` | `_conversation.Registry.GetCategorizedTools()` |
| `_openAIService.CancelGeneration()` | `_conversation.CancelGeneration()` |
| `_openAIService.StreamResponseAsync(text, tech => …)` | `_conversation.StreamResponseAsync(text, tech => …)` |
| `_openAIService.History.Add(user)` + `.Add(assistant)` (lines 79-80) | `_conversation.AppendRecoveredContext(session.Title, session.Content)` |
| `new SettingsWindow()` (line 885) | `new SettingsWindow(_settingsService)` |

`AddAgentBubble`, `AddUserBubble`, `UpdateLoadingState`, `AddTypingIndicator`, `RepositionWindow`,
`Window_Deactivated`, `InputBox_PreviewKeyDown` and every animation body are **untouched**.
`UpdateTokenCounterUI` keeps its `(int current, int max, int? cached = null)` signature, and
`HandleWarmupState` keeps its `Dispatcher.BeginInvoke` body.

### 7.3 Other views and services

* `SettingsWindow.xaml.cs`: `public SettingsWindow(SettingsService settingsService)`; drop `= new()`.
* `FirstRunWindow.xaml.cs`: `public FirstRunWindow(SettingsService settingsService)`; drop `= new()`.
* `ShadowAssistantService.cs`: ctor parameter type `OpenAIService` → `ConversationService`. The
  class is a stub; the body stays empty.
* No other file may construct a `SettingsService`.

---

## 8. File-by-file plan

Legend: **C** created, **M** modified, **D** deleted.

### Phase 1 — foundation (no user-visible behaviour change)
| file | op | content |
|---|---|---|
| `AIBWindows/Services/SettingsService.cs` | M | instance path, cache, `InvalidateCache`, `UserAppSettings.Clone`, static `HttpClient` (§5) |
| `AIBWindows/Services/TokenCounter.cs` | C | extracted from `OpenAIService.CalculateCurrentTokens` (§3.2) |
| `AIB.Tests/SettingsServiceTests.cs` | C | path follows `DirectoryService`; cache hit avoids re-read; save refreshes cache; `Clone` isolation |

Gate: `dotnet build` + `dotnet test` green. `OpenAIService` still compiles untouched.

### Phase 2 — provider layer (built, not yet wired)
| file | op | content |
|---|---|---|
| `AIBWindows/Services/Ai/StreamChunk.cs` | C | §1 |
| `AIBWindows/Services/Ai/ChatRequestOptions.cs` | C | §1 |
| `AIBWindows/Services/Ai/ChatCompletionResult.cs` | C | §1 |
| `AIBWindows/Services/Ai/IChatProvider.cs` | C | §2.1 |
| `AIBWindows/Services/Ai/ChatTemplateSanitizer.cs` | C | §2.2 |
| `AIBWindows/Services/Ai/ChannelSplitter.cs` | C | §2.2 |
| `AIBWindows/Services/Ai/IToolCallHealer.cs` | C | §2.3 |
| `AIBWindows/Services/Ai/RegexToolCallHealer.cs` | C | §2.3 |
| `AIBWindows/Services/Ai/OllamaProvider.cs` | C | §2.5 |
| `AIBWindows/Services/Ai/OpenAiProvider.cs` | C | §2.5 |
| `AIBWindows/Services/Ai/IChatProviderFactory.cs` | C | §2.6 |
| `AIBWindows/Services/Ai/ChatProviderFactory.cs` | C | §2.6 |
| `AIBWindows/Services/OllamaNativeClient.cs` | M | async read loop, `IReadOnlyList`, `numCtx`/`keepAliveSeconds` (§2.4) |
| `AIB.Tests/ChannelSplitterTests.cs` | C | think/final split; marker split across chunks; both flush fallbacks |
| `AIB.Tests/RegexToolCallHealerTests.cs` | C | think content never healed; JSON built by serializer; scalar binds to the real schema name; unknown tool refused |
| `AIB.Tests/OllamaProviderTests.cs` | C | two tool calls on two NDJSON lines produce two distinct `CallKey`s |

Gate: build + test green; `OpenAIService` is still the live path.

### Phase 3 — orchestration (built, not yet wired)
| file | op | content |
|---|---|---|
| `AIBWindows/Services/Agent/IMessageStore.cs` | C | §3.1 |
| `AIBWindows/Services/Agent/EphemeralMessageStore.cs` | C | §3.1 |
| `AIBWindows/Services/Agent/AgentEvent.cs` | C | §4.1 |
| `AIBWindows/Services/Agent/AgentLoop.cs` | C | §4 |
| `AIBWindows/Services/Agent/WarmupService.cs` | C | §3.3 |
| `AIBWindows/Services/ConversationService.cs` | C | §3.2 |
| `AIB.Tests/AgentLoopTests.cs` | C | fake provider: cap exhaustion emits `IterationLimitReached`; trim runs on a tool-call turn; parallel calls keep order |
| `AIB.Tests/ConversationServiceTests.cs` | C | snapshot isolation; warmup does not mutate live history; system prompt survives trim |

Gate: build + test green.

### Phase 4 — wiring and demolition
| file | op | content |
|---|---|---|
| `AIBWindows/App.xaml.cs` | M | composition root (§7.1) |
| `AIBWindows/Views/ChatWindow.xaml.cs` | M | plumbing only (§7.2) |
| `AIBWindows/Views/SettingsWindow.xaml.cs` | M | injected `SettingsService` |
| `AIBWindows/Views/FirstRunWindow.xaml.cs` | M | injected `SettingsService` |
| `AIBWindows/Services/ShadowAssistantService.cs` | M | ctor takes `ConversationService` |
| `AIBWindows/Services/OpenAIService.cs` | **D** | every responsibility now has a home |

Gate: build + test green, plus a manual smoke run (tray → Ctrl+Shift+Space → send a message →
tool call → answer) with no visual difference.

### Phase 5 — documentation
| file | op | content |
|---|---|---|
| `documentação/01_Architecture_Overview.md` | M | composition root is `App`, not `ChatWindow` |
| `documentação/03_Core_Services.md` | M | `OpenAIService` → `ConversationService`/`AgentLoop`/`IChatProvider`; healer moved into the provider; drop `ScreenshotService`; mark the confirmation modal as inactive |

No `.xaml` file appears in any phase.

---

## 9. Threading contract

**May touch the Dispatcher / UI thread**

* `ChatWindow`, `SettingsWindow`, `FirstRunWindow`, `ContextSidebar`, `ShadowWidget` — UI code only.
* Nothing else. Ever.

**May not touch the Dispatcher, and must not assume a synchronization context**

* `ConversationService`, `AgentLoop`, `WarmupService`, every `IChatProvider`, `ChannelSplitter`,
  `RegexToolCallHealer`, `ChatProviderFactory`, `OllamaNativeClient`, `ToolRegistry`, every
  `ITool`, `SettingsService`, `TokenCounter`.
* Every `await` in those types uses `.ConfigureAwait(false)`, and every `await foreach` uses
  `.WithCancellation(ct).ConfigureAwait(false)`.

**How chunks cross the boundary**

1. **Yielded results.** `ChatWindow` does
   `await foreach (var chunk in _conversation.StreamResponseAsync(...))` with no `ConfigureAwait`,
   so the WPF `SynchronizationContext` is captured and each yielded string resumes on the UI
   thread. That is today's mechanism and it is preserved — no `Dispatcher.Invoke` is added to the
   service layer for this path.
2. **Callbacks and events.** `onTechnicalContent`, `OnTokenCountChanged` and
   `OnWarmupStateChanged` can fire from a pool thread once the service layer uses
   `ConfigureAwait(false)`. `ConversationService` therefore captures
   `SynchronizationContext.Current` in its constructor
   (`private readonly SynchronizationContext? _uiContext`) and raises **all three** through
   `_uiContext.Post(...)` when it is non-null, falling back to a direct call when it is null
   (tests, CLI mode). `ChatWindow.UpdateTokenCounterUI` and `HandleWarmupState` keep their current
   bodies.
3. **Blocking IO.** `OllamaNativeClient.cs:74`'s `StreamReader.EndOfStream` is a synchronous
   blocking socket read that today executes on the UI thread through the captured context and
   freezes the window while the model thinks. §2.4 replaces it with `await ReadLineAsync(ct)`.
   **No synchronous socket, file or process read may exist below `ConversationService`.**
4. **Shared state.** The only shared mutable state is `ConversationService._history`, guarded by
   `_gate`; `SettingsService._cache`, guarded by its own `_gate`; and `ChatProviderFactory`'s
   cached provider, guarded by its own `lock`. Warmup runs on a pool thread against an
   `EphemeralMessageStore` and never writes under `_gate`.

---

## 10. Where each intentional behaviour now lives

| documented behaviour | new home |
|---|---|
| regex tool-call fallback (doc 03 §3.1) | `RegexToolCallHealer`, invoked by each provider at end-of-stream over final-channel text only (§2.3) |
| warmup / heartbeat / `keep_alive = -1` (doc 03 §1) | `WarmupService` + `IChatProvider.WarmupAsync`, on an `EphemeralMessageStore`, started by `App` (§3.3) |
| `RequiredLevel` tool gating (doc 03 §3.2) | `ToolRegistry` — unchanged (§6) |
| token-budget trimming with system-prompt hard lock (doc 03 §1.1) | `ConversationService.Trim`, called by `AgentLoop` on **every** iteration (§4.2 steps 5-7) |
| `<think>` preserved in history, hidden from the user | `ChannelSplitter` classifies; reasoning goes only to `onTechnicalContent`; `AgentLoop` appends the final text to history |
| parallel tool execution | `AgentLoop` step 5, `Task.WhenAll`, original order preserved |
| ReAct cap of 18 | `AgentLoop.MaxIterations`, now reported as `TurnOutcome.IterationLimitReached` and shown to the user (§4.2) |
| smart clipboard, ghosting, bubble animations, loading dots, window positioning | `ChatWindow` — untouched (RULE 1) |
