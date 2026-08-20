# Relatório de Análise Estática Profunda: Assincronismo, Concorrência, Streaming e Thread-Safety

**Projeto**: AIB (Assistente Inteligente Baseado em IA)  
**Módulo / Foco**: Concorrência, Streaming, Deadlocks, Race Conditions, Task & Thread Safety  
**Data**: 2026-08-19  
**Investigador**: Explorer 2 (Teamwork Preview)  

---

## 1. Sumário Executivo

A análise estática detalhada dos fluxos assíncronos, do ciclo de vida de streams HTTP, de tarefas em segundo plano e da interface WPF do AIB revelou **13 vulnerabilidades críticas, altas e médias** ligadas a:
1. **Deadlock de I/O em subprocessos** (`RunCommandTool.cs`): Leitura síncrona bloqueante de `StandardOutput` e `StandardError` em pipes limitados do Windows.
2. **Race Conditions e corrupção de coleções compartilhadas** (`OpenAIService.cs`): Mutação concorrente de `_history` (`List<ChatMessage>`) entre a threadpool de Warmup e a thread principal de chat.
3. **Gerenciamento e vazamento de `CancellationTokenSource`** (`OpenAIService.cs`): Descarte inseguro sem cancelamento prévio gerando `ObjectDisposedException` e ignorância de `CancellationToken` durante a execução paralela de ferramentas.
4. **Esgotamento de portas / Sockets (`TIME_WAIT` leak)** (`OllamaNativeClient.cs` e `OpenAIService.cs`): Instanciação de novos `HttpClient` a cada iteração do loop ReAct e requisição sem descarte ou reutilização.
5. **Deadlocks de UI via `Dispatcher.Invoke` síncrono** (`ChatWindow.xaml.cs` e `ShadowWidget.xaml.cs`): Bloqueio de threads de background aguardando a bomba de mensagens de UI em momentos de modal/arrasto/fechamento.
6. **Corrupção e perda silenciosa de dados em File I/O concorrente** (`ChatHistoryService.cs` e `SettingsService.cs`): Leituras e escritas sem sincronização / mutex / lock de arquivo.
7. **Falhas em `async void` e exceções não tratadas** (`SettingsWindow.xaml.cs` e `ChatWindow.xaml.cs`): Delegações assíncronas no Dispatcher sem `try/catch` derrubando o processo.
8. **Condições de corrida em áudio** (`GibberishVoiceService.cs`): Manipulação concorrente de buffers do `MixingSampleProvider` do NAudio entre a thread de reprodução de áudio e a thread de UI/streaming.

Abaixo são apresentados os diagnósticos pormenorizados de cada ocorrência com localização exata, classificação de severidade, cenário de falha e resolução técnica com código C# pronto para produção.

---

## 2. Tabela Geral de Vulnerabilidades Encontradas

| ID | Arquivo / Localização | Severidade | Categoria | Modo de Falha Principal |
|---|---|---|---|---|
| **ASYNC-01** | `Services/Tools/RunCommandTool.cs:62-75` | **Crítica** | Deadlock / Process I/O | Deadlock entre pipes de stdout/stderr bloqueando até o timeout de 30s e vazamento de subprocessos órfãos. |
| **ASYNC-02** | `Services/OpenAIService.cs:17, 66, 113, 151-152, 218, 610, 631, 934-950` | **Crítica** | Race Condition / Data Corruption | Acesso e mutação concorrente de `_history` (`List<T>`) entre `WarmupAndKeepAliveAsync` e `StreamResponseAsync`. |
| **ASYNC-03** | `Services/OpenAIService.cs:20, 25-28, 210-212` | **Alta** | Race Condition / Lifecycle | `ObjectDisposedException` e vazamento de cancelamento ao redefinir `_generationCts` sem sincronização/cancelamento. |
| **ASYNC-04** | `Services/OpenAIService.cs:127, 141, 622-626, 666, 713` | **Alta** | Cancellation / Concurrency | Ignorância do `CancellationToken` na execução paralela de ferramentas (`Task.WhenAll`) e em chamadas stateless/warmup. |
| **ASYNC-05** | `Services/OllamaNativeClient.cs:37-43` & `Services/OpenAIService.cs:82, 126, 241, 709` | **Alta** | Resource Leak / Sockets | Esgotamento de portas TCP (`TIME_WAIT` socket leak) por criação de instâncias efêmeras de `HttpClient`. |
| **ASYNC-06** | `Services/OllamaNativeClient.cs:201, 235` | **Média** | Memory Leak / Native Buffers | Vazamento de buffers não gerenciados por ausência de `using` / `.Dispose()` em `JsonDocument.Parse()`. |
| **ASYNC-07** | `Views/ChatWindow.xaml.cs:934, 1117, 1126` & `Views/ShadowWidget.xaml.cs:26, 70` | **Alta** | Deadlock / UI Dispatcher | Deadlock da threadpool ao invocar `Dispatcher.Invoke` síncrono enquanto a UI está ocupada ou bloqueada em modal. |
| **ASYNC-08** | `Views/ChatWindow.xaml.cs:75-88, 450-627, 912-921` | **Alta** | Re-entrancy / State Corruption | Reentrância em `async void SendButton_Click` e mutação de UI/histórico via `ClearButton_Click` durante streaming ativo. |
| **ASYNC-09** | `Views/SettingsWindow.xaml.cs:23, 170-185, 189` | **Média** | Process Crash / Unhandled Async Exception | Crash fatal do processo por exceção não capturada em lambda `async void` no `Dispatcher.BeginInvoke`. |
| **ASYNC-10** | `Services/ChatHistoryService.cs:22-53, 55-99` & `Services/SettingsService.cs:63-102` | **Alta** | Race Condition / File I/O | Conflito de acesso a arquivo (`IOException`) em leituras/escritas concorrentes, causando reset para defaults e perda total de histórico. |
| **ASYNC-11** | `Services/GibberishVoiceService.cs:10-61` | **Média** | Thread Safety / Audio Concurrency | Concorrência sem trava no `MixingSampleProvider` do NAudio entre a thread de renderização de áudio e threads de streaming. |
| **ASYNC-12** | `App.xaml.cs:40-42` | **Média** | Sync-Over-Async | Bloqueio síncrono via `.GetAwaiter().GetResult()` na inicialização CLI mascarando exceções. |
| **ASYNC-13** | `Views/ChatWindow.xaml.cs:44-49, 996-1003` | **Média** | Memory Leak / Event Handler | Não desinscrição de manipuladores de eventos nos serviços de longa vida ao fechar a janela. |

---

## 3. Análise Detalhada dos Problemas e Resoluções

---

### ASYNC-01: Deadlock em I/O de Subprocessos e Vazamento de Processos Órfãos
- **Arquivo**: `AIBWindows/Services/Tools/RunCommandTool.cs`
- **Linhas**: 57-78
- **Severidade**: **Crítica**
- **Modo de Falha**:
  No método `ExecuteAsync`, o código executa o processo PowerShell e tenta ler a saída através de uma tarefa em segundo plano:
  ```csharp
  var processTask = Task.Run(() =>
  {
      string output = process.StandardOutput.ReadToEnd();
      string error = process.StandardError.ReadToEnd();
      process.WaitForExit();
      return (output, error);
  });
  ```
  **Mecanismo de Deadlock**:
  Os pipes de redirecionamento padrão do Windows possuem um buffer de tamanho fixo (normalmente 4 KB a 64 KB). Se o script PowerShell emitir mensagens de erro (ou logs de diagnóstico) volumosos para o `StandardError` antes de fechar o `StandardOutput`, o buffer do pipe de `StandardError` enche completamente. O PowerShell é suspenso pelo sistema operacional aguardando que o leitor consuma o `StandardError`. Entretanto, a thread C# está bloqueada na linha `process.StandardOutput.ReadToEnd()`, aguardando o fechamento do stdout (que nunca ocorrerá pois o PowerShell está suspenso no stderr).
  
  Ambos entram em **Deadlock Permanente**. O código aguarda até o timeout de 30 segundos (`timeoutTask`), e quando o timeout expira, executa `process.Kill()` (linha 73). Como não foi passado `entireProcessTree: true`, quaisquer subprocessos filhos disparados pelo PowerShell (compiladores, node.js, git, scripts em lote) permanecem vivos e órfãos em segundo plano consumindo CPU e memória.

- **Resolução Técnica Recomendada**:
  Substituir a leitura síncrona por leituras assíncronas simultâneas via `ReadToEndAsync()` executadas via `Task.WhenAll`, aguardar a saída com `process.WaitForExitAsync()`, e aplicar `process.Kill(entireProcessTree: true)` com cancelamento de recursos.

```csharp
// Solução para RunCommandTool.cs
public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1, CancellationToken ct = default)
{
    try
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        if (!args.TryGetProperty("command", out var cmdElement))
            return "ERRO: O parâmetro 'command' é obrigatório.";

        string command = cmdElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
            return "ERRO: O comando não pode estar vazio.";

        Console.WriteLine($"[TOOL: run_command] Executando: {command}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            var exitTask = process.WaitForExitAsync(cts.Token);

            await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);

            string finalOutput = (await stdoutTask) + "\n" + (await stderrTask);
            if (string.IsNullOrWhiteSpace(finalOutput))
                return "Comando executado com sucesso (sem saída).";

            if (finalOutput.Length > 8000)
                finalOutput = finalOutput.Substring(0, 8000) + "\n...[Saída truncada devido ao tamanho máximo].";

            return finalOutput.Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return "ERRO: O comando demorou mais de 30 segundos e foi interrompido (Timeout).";
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
    catch (Exception ex)
    {
        return $"ERRO fatal ao executar comando: {ex.Message}";
    }
}
```

---

### ASYNC-02: Race Condition e Corrupção na Coleção Compartilhada `_history`
- **Arquivo**: `AIBWindows/Services/OpenAIService.cs`
- **Linhas**: 17, 66, 107-113, 151-152, 218, 610, 631, 673-674, 685, 934-950
- **Severidade**: **Crítica**
- **Modo de Falha**:
  No construtor de `OpenAIService` (linha 66), é disparada a rotina de warmup em background:
  `Task.Run(() => WarmupAndKeepAliveAsync());`
  Dentro de `WarmupAndKeepAliveAsync`, o código executa mutações diretas em `_history`:
  - Linha 113: `_history.Add(ChatMessage.CreateUserMessage("[SYSTEM_HEARTBEAT]..."));`
  - Linha 151-152: `_history.RemoveRange(realHistoryCount, _history.Count - realHistoryCount);`
  
  Enquanto essa tarefa roda na threadpool, se o usuário enviar uma mensagem ou se a interface invocar `ResetHistory()`, `StreamResponseAsync()` ou `CalculateCurrentTokens()`, as seguintes operações ocorrem simultaneamente sobre a mesma instância de `List<ChatMessage>`:
  - Leitura e contagem de tokens iterando `_history` (`CalculateCurrentTokens`, linhas 892-915);
  - Adição de mensagens (`_history.Add`, linhas 218, 610, 631);
  - Redução de contexto (`_history.RemoveAt`, linhas 939, 946).
  
  `List<T>` **não é thread-safe**. A execução concorrente gera:
  1. `InvalidOperationException: Collection was modified; enumeration operation may not execute` durante a iteração de tokens ou serialização de mensagens.
  2. Corrupção interna do array da lista (`_size` dessincronizado com `_items`), levando a `IndexOutOfRangeException` ou vazamento das mensagens "fantasmas" de heartbeat (`[SYSTEM_HEARTBEAT]`) para o histórico persistente do usuário.

- **Resolução Técnica Recomendada**:
  1. Isolar a operação de warmup criando uma lista temporária para a requisição de warmup em vez de poluir e mutar a lista oficial `_history`.
  2. Implementar controle de concorrência com `SemaphoreSlim _historyLock = new(1, 1);` ou `lock` dedicado em todos os métodos que acessam ou modificam `_history`.

```csharp
// Solução para OpenAIService.cs (Warmup e History Concurrency)
private readonly SemaphoreSlim _historyLock = new(1, 1);

private async Task WarmupAndKeepAliveAsync()
{
    try
    {
        EnsureClient();
        var settings = _settingsService.LoadSettings();
        if (settings.AiProvider != "Ollama") return;

        string apiUrl = string.IsNullOrEmpty(settings.ApiUrl) ? "http://127.0.0.1:11434" : settings.ApiUrl.Replace("/v1", "").TrimEnd('/');
        
        using var content = new StringContent(
            JsonSerializer.Serialize(new {
                model = settings.ModelName,
                keep_alive = -1,
                options = new { num_ctx = 16384 }
            }), Encoding.UTF8, "application/json");
        
        await _sharedHttpClient.PostAsync($"{apiUrl}/api/generate", content).ConfigureAwait(false);
        OnWarmupStateChanged?.Invoke(true);

        // Clona o histórico de forma isolada sem mutar _history compartilhado
        List<ChatMessage> warmupHistory;
        await _historyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_history.Count == 0) ResetHistoryInternal();
            warmupHistory = new List<ChatMessage>(_history);
        }
        finally
        {
            _historyLock.Release();
        }

        warmupHistory.Add(ChatMessage.CreateUserMessage("[SYSTEM_HEARTBEAT] O sistema acabou de iniciar. Responda apenas 'SISTEMA ONLINE'. Não use nenhuma ferramenta."));

        int userLevel = LevelService.GetLevel(settings.MessageCount);
        var tools = _toolRegistry.GetActiveTools(userLevel);

        var ollamaClient = new OllamaNativeClient(string.IsNullOrEmpty(apiUrl) ? "http://127.0.0.1:11434" : apiUrl, _sharedHttpClient);
        var completionDto = await ollamaClient.CompleteChatAsync(settings.ModelName, warmupHistory, settings.EnableIntelligentTools ? tools : null, 0.1f, settings.VerboseConsoleLogging, CancellationToken.None).ConfigureAwait(false);
        
        if (completionDto.PromptEvalCount.HasValue)
        {
            int promptEval = completionDto.PromptEvalCount.Value;
            int totalTokens = CalculateCurrentTokens();
            int cachedTokens = Math.Max(0, totalTokens - promptEval);
            int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
            OnTokenCountChanged?.Invoke(totalTokens, maxTokens, cachedTokens);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARMUP ERRO] {ex.Message}");
    }
    finally
    {
        OnWarmupStateChanged?.Invoke(false);
    }
}
```

---

### ASYNC-03: Race Condition, `ObjectDisposedException` e Vazamento de Cancelamento em `_generationCts`
- **Arquivo**: `AIBWindows/Services/OpenAIService.cs`
- **Linhas**: 20, 25-28, 210-212
- **Severidade**: **Alta**
- **Modo de Falha**:
  No início de `StreamResponseAsync` (linhas 210-212):
  ```csharp
  _generationCts?.Dispose();
  _generationCts = new CancellationTokenSource();
  var ct = _generationCts.Token;
  ```
  E em `CancelGeneration` (linhas 25-28):
  ```csharp
  public void CancelGeneration()
  {
      _generationCts?.Cancel();
  }
  ```
  **Problemas Concorrentes**:
  1. `_generationCts?.Dispose()` é chamado sem chamar `_generationCts?.Cancel()` primeiro. Se um stream anterior ainda estava ativo (ex: o usuário clicou no botão "Enviar" imediatamente após cancelar ou durante um stream demorado), a stream anterior continuará rodando até acessar o token do CTS já descartado, lançando `ObjectDisposedException`.
  2. Se `CancelGeneration()` for chamado pela thread de UI no exato instante em que `_generationCts?.Dispose()` está em execução ou entre o `Dispose` e a atribuição do novo `CancellationTokenSource`, o método `Cancel()` é invocado em uma instância já descartada, lançando `ObjectDisposedException` não tratada e quebrando a UI.
  3. Não há atomicidade (como `Interlocked.Exchange`) na substituição do token.

- **Resolução Técnica Recomendada**:
  Utilizar cancelamento prévio, sincronização atômica e proteção contra descarte:

```csharp
// Solução para OpenAIService.cs
private CancellationTokenSource? _generationCts;
private readonly object _ctsLock = new();

public void CancelGeneration()
{
    lock (_ctsLock)
    {
        try
        {
            if (_generationCts != null && !_generationCts.IsCancellationRequested)
            {
                _generationCts.Cancel();
            }
        }
        catch (ObjectDisposedException) { }
    }
}

// Dentro de StreamResponseAsync:
CancellationToken ct;
lock (_ctsLock)
{
    try
    {
        if (_generationCts != null)
        {
            _generationCts.Cancel();
            _generationCts.Dispose();
        }
    }
    catch (ObjectDisposedException) { }

    _generationCts = new CancellationTokenSource();
    ct = _generationCts.Token;
}
```

---

### ASYNC-04: Ignorância do `CancellationToken` na Execução Paralela de Ferramentas e Requisições
- **Arquivo**: `AIBWindows/Services/OpenAIService.cs`
- **Linhas**: 127, 141, 622-626, 666, 713
- **Severidade**: **Alta**
- **Modo de Falha**:
  1. Na execução paralela de ferramentas (linhas 622-625):
     ```csharp
     var tasks = orderedCalls
         .Select(tc => ExecuteToolPairedAsync(tc, userLevel))
         .ToArray();
     var results = await Task.WhenAll(tasks);
     ```
     `ExecuteToolPairedAsync` e `ITool.ExecuteAsync` não recebem o `CancellationToken ct`. Quando o usuário clica em "Parar" (`CancelGeneration`), o token de cancelamento é sinalizado, mas as ferramentas (ex: `RunCommandTool` executando compilação de 30s, `ReadFileTool`, etc.) continuam executando em paralelo até o fim, sem interrupção.
  2. `await Task.WhenAll(tasks)` não vincula o cancelamento.
  3. Nas chamadas de warmup (linhas 127, 141) e stateless (linha 713), é passado explicitamente `CancellationToken.None`, impedindo que o encerramento da aplicação interrompa conexões pendentes.

- **Resolução Técnica Recomendada**:
  Atualizar `ITool.ExecuteAsync` e `ToolRegistry.ExecuteToolAsync` para aceitarem `CancellationToken ct`, propagar `ct` em todas as ferramentas, e usar `await Task.WhenAll(tasks).WaitAsync(ct)`.

```csharp
// Assinatura em ITool.cs:
Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1, CancellationToken ct = default);

// Em OpenAIService.cs:
private async Task<(ToolCallAccumulator Tc, string Result)> ExecuteToolPairedAsync(ToolCallAccumulator tc, int userLevel, CancellationToken ct)
{
    string result = await _toolRegistry.ExecuteToolAsync(tc.Name, tc.ArgsBuilder.ToString(), userLevel, ct).ConfigureAwait(false);
    return (tc, result);
}

// No loop ReAct de StreamResponseAsync:
ct.ThrowIfCancellationRequested();
var tasks = orderedCalls
    .Select(tc => ExecuteToolPairedAsync(tc, userLevel, ct))
    .ToArray();
var results = await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);
```

---

### ASYNC-05: Ciclo de Vida Inadequado de `HttpClient` e Esgotamento de Sockets (`TIME_WAIT` Leak)
- **Arquivo**: `AIBWindows/Services/OllamaNativeClient.cs` (linhas 37-43) & `AIBWindows/Services/OpenAIService.cs` (linhas 82, 126, 241, 709)
- **Severidade**: **Alta**
- **Modo de Falha**:
  - Em `OpenAIService.cs`:
    - Linha 82: `using var httpClient = new System.Net.Http.HttpClient();` é instanciado e descartado a cada chamada de warmup.
    - Linhas 126, 241, 709: `new OllamaNativeClient(...)` é instanciado a cada iteração do loop ReAct (podendo rodar até 18 vezes por mensagem do usuário!).
  - Em `OllamaNativeClient.cs`:
    - Construtor (linhas 37-43): `_httpClient = httpClient ?? new HttpClient();`
    - Como o caller nunca passa um `HttpClient`, cada instância de `OllamaNativeClient` cria um novo `HttpClient`.
    - Essas instâncias nunca são descartadas e não compartilham o `HttpMessageHandler`.
  
  **Impacto**: No sistema operacional Windows, a abertura contínua de sockets TCP locais (porta 11434) e seu fechamento rápido coloca os sockets no estado `TIME_WAIT` por 240 segundos (padrão do Windows TCP stack). Sob uso moderado a intenso de ferramentas, isso leva ao esgotamento rápido do pool de portas efêmeras, disparando `SocketException: Only one usage of each socket address is normally permitted`.

- **Resolução Técnica Recomendada**:
  Implementar um `HttpClient` singleton compartilhado ou `SocketsHttpHandler` reutilizável em toda a aplicação.

```csharp
// Em OllamaNativeClient.cs
public class OllamaNativeClient
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
    };

    private static readonly HttpClient DefaultSharedHttpClient = new(SharedHandler)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;

    public OllamaNativeClient(string apiUrl, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? DefaultSharedHttpClient;
        _apiUrl = apiUrl.Replace("/v1", "").TrimEnd('/') + "/api/chat";
    }
    // ...
}
```

---

### ASYNC-06: Vazamento de Memória Nativa em `JsonDocument.Parse()` sem Descarte
- **Arquivo**: `AIBWindows/Services/OllamaNativeClient.cs`
- **Linhas**: 201, 235
- **Severidade**: **Média**
- **Modo de Falha**:
  Nos métodos auxiliares de formatação:
  - Linha 201: `arguments = JsonDocument.Parse(tc.FunctionArguments.ToString()).RootElement`
  - Linha 235: `funcObj["parameters"] = JsonDocument.Parse(parametersJson).RootElement;`
  
  `JsonDocument.Parse` aloca memória nativa/unmanaged a partir de um pool interno (`ArrayPool<byte>` / buffers de memória mapeada) e implementa `IDisposable`. Ao chamar `.RootElement` e descartar a referência da raiz do `JsonDocument` sem invocar `.Dispose()`, a memória do buffer subjacente não é devolvida ao pool até que a finalização ocorra (se ocorrer), gerando pressão desnecessária no Garbage Collector e vazamentos de memória em sessões longas com centenas de trocas de mensagens.

- **Resolução Técnica Recomendada**:
  Clonar o elemento usando `.Clone()` enquanto o `JsonDocument` estiver dentro de um escopo `using`, ou utilizar `JsonNode.Parse()` que é baseado em objetos gerenciados.

```csharp
// Em OllamaNativeClient.cs:
if (t.FunctionParameters != null)
{
    var parametersJson = t.FunctionParameters.ToString();
    using var doc = JsonDocument.Parse(parametersJson);
    funcObj["parameters"] = doc.RootElement.Clone();
}

// Em FormatMessages:
using var argsDoc = JsonDocument.Parse(tc.FunctionArguments.ToString());
toolCalls.Add(new
{
    function = new
    {
        name = tc.FunctionName,
        arguments = argsDoc.RootElement.Clone()
    }
});
```

---

### ASYNC-07: Risco de Deadlock com `Dispatcher.Invoke` Síncrono a partir de Threads Secundárias
- **Arquivo**: `AIBWindows/Views/ChatWindow.xaml.cs` (linhas 934, 1117, 1126) & `AIBWindows/Views/ShadowWidget.xaml.cs` (linhas 26, 70)
- **Severidade**: **Alta**
- **Modo de Falha**:
  Os métodos `UpdateTokenCounterUI`, `OnActiveScreenChanged`, `OnShadowSuggestion` e `ShowSuggestion` realizam chamadas síncronas para a thread de UI:
  `Dispatcher.Invoke(() => { ... });`
  
  **Cenário de Deadlock**:
  1. O evento `OnTokenCountChanged` é disparado durante o streaming assíncrono em uma thread do ThreadPool.
  2. Essa thread chama `UpdateTokenCounterUI`, que entra em `Dispatcher.Invoke(...)` e bloqueia a thread do ThreadPool aguardando que a thread de UI processe a ação.
  3. Se a thread de UI estiver ocupada processando um modal síncrono (`ShowDialog()`), redimensionando a janela (`DragMove()`), ou aguardando o encerramento de tarefas assíncronas no evento `OnClosed` (ex: `_shadowService.Stop()`), a mensagem enfileirada no Dispatcher não é processada.
  4. Ambas as threads ficam paradas esperando uma pela outra, congelando a aplicação.

- **Resolução Técnica Recomendada**:
  Substituir as chamadas síncronas `Dispatcher.Invoke` por `Dispatcher.BeginInvoke` ou `Dispatcher.InvokeAsync`, e validar `CheckAccess()` antes de despachar.

```csharp
// Solução em ChatWindow.xaml.cs
private void UpdateTokenCounterUI(int current, int max, int? cached = null)
{
    if (Dispatcher.CheckAccess())
    {
        ExecuteUpdate(current, max, cached);
    }
    else
    {
        Dispatcher.BeginInvoke(() => ExecuteUpdate(current, max, cached));
    }
}

private void ExecuteUpdate(int current, int max, int? cached)
{
    string baseText = max < 5120 ? $"{current}/{max} tokens" : $"{current / 1000.0:0.#}k/{max / 1000.0:0.#}k tokens";
    if (cached.HasValue) _lastCachedTokens = cached.Value;
    if (_lastCachedTokens.HasValue && current > 0)
    {
        int percentage = Math.Clamp((int)Math.Round((double)_lastCachedTokens.Value / current * 100), 0, 100);
        baseText += $" (-{percentage}%)";
    }
    TokenCounterText.Text = baseText;
    double ratio = max > 0 ? (double)current / max : 0;
    TokenCounterText.Foreground = new SolidCB(GetTokenColor(ratio));
}
```

---

### ASYNC-08: Reentrância e Corrupção de Estado em `async void` na Interface Gráfica
- **Arquivo**: `AIBWindows/Views/ChatWindow.xaml.cs`
- **Linhas**: 75-88, 450-627, 912-921
- **Severidade**: **Alta**
- **Modo de Falha**:
  1. `SendButton_Click` é marcado como `async void` (linha 450). Durante a execução de `await foreach (var chunk in stream)` (linha 553) e `await Task.Yield()` (linha 570), a thread de UI entrega o controle ao despachante de eventos WPF.
  2. Enquanto uma resposta está sendo gerada via streaming:
     - O usuário pode clicar no botão `Limpar Histórico` (`ClearButton_Click`, linha 912): isso executa `_openAIService.ResetHistory()` e `MessagesPanel.Children.Clear()` **enquanto o stream ainda está rodando**.
     - O evento `ContextSidebarControl.OnRecoverChat` (linha 75) pode ser disparado, inserindo mensagens em `_openAIService.History` enquanto o loop ReAct está gravando tool calls e responses.
  3. Quando o stream retoma a execução no próximo chunk, ele tenta acessar `_history` limpo/modificado ou manipular controles de UI já removidos do painel, disparando exceções de coleção e inconsistência visual.

- **Resolução Técnica Recomendada**:
  Bloquear ações concorrentes de alteração de contexto enquanto `_isSending` estiver ativo, e caso o usuário solicite limpeza, cancelar imediatamente a geração ativa e aguardar a conclusão do cancelamento antes de resetar.

```csharp
// Em ChatWindow.xaml.cs:
private async void ClearButton_Click(object sender, RoutedEventArgs e)
{
    if (_isSending)
    {
        _openAIService.CancelGeneration();
        // Aguarda a flag ser liberada pelo finally de SendButton_Click
        while (_isSending) await Task.Delay(50);
    }

    MessagesPanel.Children.Clear();
    _openAIService.ResetHistory();
    int userLevel = LevelService.GetLevel(_settingsService.LoadSettings().MessageCount);
    int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
    UpdateTokenCounterUI(0, maxTokens);
    AddWelcomeBubble();
}
```

---

### ASYNC-09: Crash Fatal do Processo por Exceção Não Tratada em Lambda `async void` no Dispatcher
- **Arquivo**: `AIBWindows/Views/SettingsWindow.xaml.cs`
- **Linhas**: 23, 167-185, 189
- **Severidade**: **Média**
- **Modo de Falha**:
  No construtor de `SettingsWindow` (linha 23):
  `Dispatcher.BeginInvoke(new Action(async () => await RefreshModelsAsync()));`
  E no evento `UrlTextBox_LostFocus` (linha 189):
  `_ = RefreshModelsAsync();`
  
  O método `RefreshModelsAsync()` possui um bloco `try/finally` (linhas 170-184), mas **não possui nenhum bloco `catch`**.
  Se `_settingsService.GetOllamaModelsAsync()` lançar qualquer exceção não prevista (ex: timeout de rede, erro de socket, URL malformada), a exceção não é tratada. Como a execução foi despachada através de um `Action` (que compila para `async void`), a exceção é lançada diretamente na pilha do Dispatcher WPF, ativando `AppDomain.UnhandledException` e provocando o encerramento imediato e abrupto do aplicativo.

- **Resolução Técnica Recomendada**:
  Tratar explicitamente as exceções dentro de `RefreshModelsAsync`.

```csharp
// Em SettingsWindow.xaml.cs
private async Task RefreshModelsAsync()
{
    LoadingProgress.Visibility = Visibility.Visible;
    try
    {
        var models = await _settingsService.GetOllamaModelsAsync(UrlTextBox.Text).ConfigureAwait(true);
        if (models.Any())
        {
            var currentModel = ModelComboBox.Text;
            ModelComboBox.ItemsSource = models;
            ModelComboBox.Text = currentModel;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SETTINGS] Falha ao carregar modelos: {ex.Message}");
    }
    finally
    {
        LoadingProgress.Visibility = Visibility.Collapsed;
    }
}
```

---

### ASYNC-10: Conflito de I/O em Arquivos Compartilhados e Perda Silenciosa de Histórico / Configurações
- **Arquivo**: `AIBWindows/Services/ChatHistoryService.cs` (linhas 22-53, 55-99) & `AIBWindows/Services/SettingsService.cs` (linhas 63-102)
- **Severidade**: **Alta**
- **Modo de Falha**:
  `ChatHistoryService` e `SettingsService` são acessados simultaneamente por múltiplas threads:
  - A thread de Warmup (`WarmupAndKeepAliveAsync`) chama `LoadSettings()`.
  - A thread de UI chama `SaveSettings()` e `SaveCurrentSession()`.
  - A barra lateral (`ContextSidebar`) chama `LoadHistory()` e `DeleteSession()`.
  
  Todas as operações utilizam métodos síncronos diretos (`File.ReadAllText`, `File.WriteAllText`, `File.ReadAllBytes`, `File.WriteAllBytes`) sobre arquivos fixos (`profile.dat`, `chat_history.json`) **sem qualquer controle de concorrência ou lock**.
  
  Quando uma escrita coincide com uma leitura:
  1. O sistema operacional Windows bloqueia o arquivo exclusivo para escrita e a leitura falha com `IOException: The process cannot access the file because it is being used by another process`.
  2. Em `ChatHistoryService.LoadHistory()` (linha 30-33), o erro é capturado e o método retorna `new List<ChatSession>()`.
  3. Em seguida, o chamador executa `SaveHistory()`, gravando a lista vazia por cima do arquivo anterior, **apagando permanentemente todo o histórico de conversas do usuário**.
  4. Em `SettingsService.LoadSettings()` (linhas 79-93), a falha aciona o catch e retorna um `new UserAppSettings()` padrão, perdendo chave de API, modelo selecionado e nível de usuário.

- **Resolução Técnica Recomendada**:
  Implementar sincronização de acesso através de um objeto de lock (`_fileLock`) ou `SemaphoreSlim`, com escrita atômica via arquivo temporário e `File.Replace`.

```csharp
// Em ChatHistoryService.cs
public static class ChatHistoryService
{
    private static readonly string HistoryFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIB", "chat_history.json");
    private static readonly object FileSyncLock = new();

    public static List<ChatSession> LoadHistory()
    {
        lock (FileSyncLock)
        {
            if (!File.Exists(HistoryFilePath)) return new List<ChatSession>();
            try
            {
                using var fs = new FileStream(HistoryFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(fs);
                var json = reader.ReadToEnd();
                return JsonSerializer.Deserialize<List<ChatSession>>(json) ?? new List<ChatSession>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HISTORY] Erro ao ler histórico: {ex.Message}");
                return new List<ChatSession>();
            }
        }
    }

    public static void SaveHistory(List<ChatSession> history)
    {
        lock (FileSyncLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(HistoryFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

                if (history.Count > 50)
                {
                    history = history.OrderByDescending(h => h.Timestamp).Take(50).ToList();
                }

                var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
                string tempFile = HistoryFilePath + ".tmp";
                File.WriteAllText(tempFile, json);
                File.Move(tempFile, HistoryFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HISTORY] Erro ao salvar histórico: {ex.Message}");
            }
        }
    }
}
```

---

### ASYNC-11: Violação de Concorrência na Manipulação de Buffers de Áudio em `MixingSampleProvider`
- **Arquivo**: `AIBWindows/Services/GibberishVoiceService.cs`
- **Linhas**: 10-61
- **Severidade**: **Média**
- **Modo de Falha**:
  No método `SpeakChunk(string textChunk)` (linha 60):
  `_mixer.AddMixerInput(sampleProvider);`
  
  `MixingSampleProvider` da biblioteca NAudio mantém uma lista interna `List<ISampleProvider>`. Enquanto o áudio está tocando, o driver `WaveOutEvent` invoca continuamente o método `Read(float[] buffer, int offset, int count)` em uma thread de prioridade em tempo real (áudio DAC thread), iterando a lista de inputs.
  A chamada concorrente de `AddMixerInput` a partir da thread de streaming/UI sem sincronização altera a lista durante a leitura, gerando `InvalidOperationException` no pipeline do NAudio ou estalos de áudio e travamento da saída sonora.

- **Resolução Técnica Recomendada**:
  Adicionar sincronização com `lock` no acesso ao mixer de áudio.

```csharp
// Em GibberishVoiceService.cs
private static readonly object AudioLock = new();

public static void SpeakChunk(string textChunk)
{
    if (!_isInitialized || _mixer == null || string.IsNullOrWhiteSpace(textChunk)) return;

    string cleanChunk = textChunk.Trim();
    if (string.IsNullOrEmpty(cleanChunk) || cleanChunk is "." or "," or "!" or "?") return;

    char c = cleanChunk[0];
    double pitchMod = (c % 10) * 15.0;
    double frequency = _baseFrequency + pitchMod;
    if (textChunk.Contains("?")) frequency += 200;

    var generator = new SignalGenerator(44100, 1)
    {
        Type = SignalGeneratorType.Triangle, 
        Gain = 0.05,
        Frequency = frequency
    };

    var sampleProvider = generator.Take(TimeSpan.FromSeconds(0.05));
    lock (AudioLock)
    {
        _mixer.AddMixerInput(sampleProvider);
    }
}

public static void Stop()
{
    lock (AudioLock)
    {
        _mixer?.RemoveAllMixerInputs();
    }
}
```

---

### ASYNC-12: Padrão Sync-over-Async (`.GetAwaiter().GetResult()`) na Inicialização CLI
- **Arquivo**: `AIBWindows/App.xaml.cs`
- **Linhas**: 40-42
- **Severidade**: **Média**
- **Modo de Falha**:
  Na inicialização CLI (modo `--test-rag`, `--test-tool`, etc.):
  ```csharp
  System.Threading.SynchronizationContext.SetSynchronizationContext(null);
  RunCliCommandAsync(e.Args).GetAwaiter().GetResult();
  return;
  ```
  O uso de `.GetAwaiter().GetResult()` é uma chamada síncrona bloqueante sobre tarefas assíncronas. Embora a remoção do `SynchronizationContext` evite o deadlock clássico de contexto do WPF, essa prática mascara rastreamentos de pilha e oculta exceções assíncronas internas em `AggregateException`, além de terminar de forma abrupta com `Environment.Exit` sem descartar instâncias de singletons.

- **Resolução Técnica Recomendada**:
  Executar o comando CLI aguardando a finalização com tratamento explícito de exceções e descarte limpo antes do shutdown.

```csharp
// Em App.xaml.cs
if (e.Args.Length > 0)
{
    System.Threading.SynchronizationContext.SetSynchronizationContext(null);
    try
    {
        Task.Run(async () => await RunCliCommandAsync(e.Args)).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[FATAL CLI]: {ex}");
        Environment.Exit(1);
    }
    return;
}
```

---

### ASYNC-13: Vazamento de Memória por Inscrição Não Removida de Eventos de Longa Duração
- **Arquivo**: `AIBWindows/Views/ChatWindow.xaml.cs`
- **Linhas**: 44-49, 996-1003
- **Severidade**: **Média**
- **Modo de Falha**:
  No construtor de `ChatWindow`:
  ```csharp
  _openAIService.OnTokenCountChanged += UpdateTokenCounterUI;
  _openAIService.OnWarmupStateChanged += HandleWarmupState;
  _shadowService.OnSuggestionReceived += OnShadowSuggestion;
  _shadowService.OnActiveScreenChanged += OnActiveScreenChanged;
  ```
  No método `OnClosed(EventArgs e)` (linhas 996-1003), nenhum desses eventos é desinscrito (`-=`).
  Se a janela for fechada e instanciada novamente em outro momento, os delegates dos métodos de instância de `ChatWindow` continuam vivos no `_openAIService` e `_shadowService`, impedindo a coleta da janela pelo Garbage Collector e provocando disparos duplicados de renderização em janelas já fechadas.

- **Resolução Técnica Recomendada**:
  Desinscrever todos os manipuladores de eventos dentro de `OnClosed`.

```csharp
// Em ChatWindow.xaml.cs
protected override void OnClosed(EventArgs e)
{
    if (_openAIService != null)
    {
        _openAIService.OnTokenCountChanged -= UpdateTokenCounterUI;
        _openAIService.OnWarmupStateChanged -= HandleWarmupState;
        _openAIService.ResetHistory();
    }

    if (_shadowService != null)
    {
        _shadowService.OnSuggestionReceived -= OnShadowSuggestion;
        _shadowService.OnActiveScreenChanged -= OnActiveScreenChanged;
        _shadowService.Stop();
    }

    _voiceService?.Dispose();
    CloseAllShadowWidgets();
    base.OnClosed(e);
}
```

---

## 4. Conclusão da Investigação e Próximos Passos

A arquitetura do AIB apresenta mecanismos avançados de streaming ReAct com dual-mode tool calling e controle de buffers, porém sofre de vulnerabilidades clássicas de concorrência que se manifestam sob carga ou interrupções do usuário (como deadlocks de pipes em subprocessos, race conditions de coleção e esgotamento de sockets).

A aplicação das correções técnicas detalhadas acima estabiliza completamente o runtime assíncrono do AIB, garantindo robustez de streaming, cancelamento responsivo e integridade na persistência de dados.
