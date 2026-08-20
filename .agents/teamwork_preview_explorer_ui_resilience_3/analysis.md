# Relatório de Análise Estática Profunda: UI/WPF, Segurança, Resiliência e Testes

**Data da Auditoria:** 19/08/2026  
**Escopo:** `AIBWindows` e `AIB.Tests`  
**Foco:** WPF/XAML, Memory Leaks, Segurança em Ferramentas e Credenciais, Tratamento de Exceções, Resiliência e Qualidade da Suíte de Testes.

---

## 1. WPF, XAML, Memory Leaks e Anti-patterns de UI

### [ISSUE-UI-01] Desinscrição Inexistente de Eventos e Vazamento de Memória em Views
- **Arquivo / Linha:** `AIBWindows/Views/ChatWindow.xaml.cs:44-53, 75-87, 996-1003`
- **Severidade:** Alta
- **Impacto / Risco:** 
  A classe `ChatWindow` subscreve múltiplos eventos de longa duração (`_openAIService.OnTokenCountChanged`, `_openAIService.OnWarmupStateChanged`, `_shadowService.OnSuggestionReceived`, `_shadowService.OnActiveScreenChanged`, `ContextSidebarControl.OnRecoverChat`, `this.Deactivated`), mas no método `OnClosed` nenhuma dessas subscrições é desfeita. Se a janela for fechada e recriada ou se serviços globais mantiverem referências, a instância de `ChatWindow` e toda a árvore visual de controles do WPF (incluindo imagens, buffers de texto e componentes de UI) ficam retidas na memória, impedindo a coleta pelo Garbage Collector (GC).
- **Resolução Técnica:**
  Implementar desinscrição explícita no evento `OnClosed` ou `Unloaded` e adotar padrão de eventos fracos (*WeakEventManager*) para serviços de longa duração.
```csharp
protected override void OnClosed(EventArgs e)
{
    // Desinscrever eventos de serviços para permitir coleta de lixo
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

    if (_voiceService != null)
    {
        _voiceService.OnTranscriptionUpdated -= OnTranscriptionUpdated;
        _voiceService.Dispose();
    }

    StateChanged -= ChatWindow_StateChanged;
    IsVisibleChanged -= ChatWindow_IsVisibleChanged;
    this.Deactivated -= Window_Deactivated;

    CloseAllShadowWidgets();
    base.OnClosed(e);
}
```

---

### [ISSUE-UI-02] Timers Descontrolados Retendo Controles e Consumindo CPU em Background
- **Arquivo / Linha:** 
  - `AIBWindows/Views/ContextSidebar.xaml.cs:29-35`
  - `AIBWindows/Views/ShadowWidget.xaml.cs:17-22, 48-56`
- **Severidade:** Média
- **Impacto / Risco:** 
  Em `ContextSidebar.xaml.cs`, o `DispatcherTimer _pulseTimer` é disparado a cada 10 segundos no construtor e nunca é parado (`Stop()`) ou desinscrito quando o controle sai da visualização. Da mesma forma, em `ShadowWidget.xaml.cs`, cada chamada a `HideSuggestion()` registra uma nova closure anônima em `fadeOut.Completed` e cria novas animações `DoubleAnimation`, acumulando manipuladores e mantendo o `_hideBubbleTimer` ativo. Isso gera acúmulo contínuo de objetos na memória gerenciada e consumo desnecessário de ciclos de CPU na UI Thread.
- **Resolução Técnica:**
  Gerenciar o ciclo de vida dos timers associando-os aos eventos `Loaded` e `Unloaded` dos controles e reutilizar instâncias de animações.
```csharp
// ContextSidebar.xaml.cs
public ContextSidebar()
{
    InitializeComponent();
    Loaded += (s, e) => _pulseTimer.Start();
    Unloaded += (s, e) => _pulseTimer.Stop();

    _pulseTimer = new System.Windows.Threading.DispatcherTimer
    {
        Interval = TimeSpan.FromSeconds(10)
    };
    _pulseTimer.Tick += PulseTimer_Tick;
}

private void PulseTimer_Tick(object? sender, EventArgs e) => CheckApproachingReminders();
```

---

### [ISSUE-UI-03] Chamadas Síncronas `Dispatcher.Invoke` com Risco de Deadlock de UI
- **Arquivo / Linha:** 
  - `AIBWindows/Views/ChatWindow.xaml.cs:934, 1117, 1126`
  - `AIBWindows/Views/ShadowWidget.xaml.cs:26, 70`
- **Severidade:** Alta
- **Impacto / Risco:** 
  O uso de `Dispatcher.Invoke(...)` síncrono para atualizar contadores de tokens ou trocar estados de widgets a partir de callbacks disparados por threads de background (como `OpenAIService` ou `ShadowAssistantService`) bloqueia a thread de execução do background aguardando a fila da UI Thread. Caso a UI Thread esteja aguardando a finalização de uma tarefa assíncrona ou em operação de encerramento (`Shutdown`), ocorre um congelamento irreversível (*Deadlock*).
- **Resolução Técnica:**
  Substituir `Dispatcher.Invoke` por `Dispatcher.BeginInvoke` ou `await Dispatcher.InvokeAsync(...)`.
```csharp
// Em ChatWindow.xaml.cs
private void UpdateTokenCounterUI(int current, int max, int? cached = null)
{
    Dispatcher.BeginInvoke(new Action(() =>
    {
        string baseText = max < 5120 ? $"{current}/{max} tokens" : $"{current / 1000.0:0.#}k/{max / 1000.0:0.#}k tokens";
        if (cached.HasValue) _lastCachedTokens = cached.Value;
        if (_lastCachedTokens.HasValue && current > 0)
        {
            int percentage = Math.Clamp((int)Math.Round((double)_lastCachedTokens.Value / current * 100), 0, 100);
            baseText += $" (-{percentage}%)";
        }
        TokenCounterText.Text = baseText;
        TokenCounterText.Foreground = new SolidCB(GetTokenColor(max > 0 ? (double)current / max : 0));
    }));
}
```

---

### [ISSUE-UI-04] Ineficiência de Layout e Desativação de Virtualização em ListBox
- **Arquivo / Linha:** `AIBWindows/Views/FirstRunWindow.xaml:112-118, 145-149`
- **Severidade:** Baixa
- **Impacto / Risco:** 
  No `FirstRunWindow.xaml`, o `ListBox x:Name="AgentsListBox"` define `ScrollViewer.CanContentScroll="False"` juntamente com um `VirtualizingStackPanel`. No WPF, configurar `CanContentScroll="False"` desativa completamente a virtualização de UI do `VirtualizingStackPanel`, forçando a criação e medição de todos os elementos visuais de uma única vez, degradando o tempo de renderização e aumentando o consumo de memória.
- **Resolução Técnica:**
  Manter a virtualização habilitada ou remover o `VirtualizingStackPanel` se o número de itens for estático e pequeno, ajustando as propriedades de rolagem.
```xml
<ListBox x:Name="AgentsListBox" 
         Background="Transparent" BorderThickness="0" 
         ScrollViewer.HorizontalScrollBarVisibility="Auto" 
         ScrollViewer.VerticalScrollBarVisibility="Disabled"
         ScrollViewer.CanContentScroll="True"
         VirtualizingPanel.IsVirtualizing="True"
         VirtualizingPanel.VirtualizationMode="Recycling"
         PreviewMouseWheel="AgentsListBox_PreviewMouseWheel">
```

---

### [ISSUE-UI-05] Potencial Vazamento de Memória Não-Gerenciada em `EnableBlur` (P/Invoke)
- **Arquivo / Linha:** `AIBWindows/Views/SettingsWindow.xaml.cs:281-298`
- **Severidade:** Média
- **Impacto / Risco:** 
  No método `EnableBlur()`, a memória não gerenciada é alocada com `Marshal.AllocHGlobal(accentStructSize)`. Se uma exceção ocorrer durante `Marshal.StructureToPtr` ou `SetWindowCompositionAttribute`, a chamada `Marshal.FreeHGlobal(accentPtr)` na linha 297 nunca será executada, causando vazamento de memória não-gerenciada no processo.
- **Resolução Técnica:**
  Envolver a alocação e o uso de ponteiros nativos em bloco `try ... finally`.
```csharp
internal void EnableBlur()
{
    var windowHelper = new WindowInteropHelper(this);
    var accent = new AccentPolicy { AccentState = 4, GradientColor = 0x01000000 };
    int accentStructSize = Marshal.SizeOf(accent);
    IntPtr accentPtr = Marshal.AllocHGlobal(accentStructSize);
    try
    {
        Marshal.StructureToPtr(accent, accentPtr, false);
        var data = new WindowCompositionAttributeData
        {
            Attribute = 19,
            SizeOfData = accentStructSize,
            Data = accentPtr
        };
        SetWindowCompositionAttribute(windowHelper.Handle, ref data);
    }
    finally
    {
        Marshal.FreeHGlobal(accentPtr);
    }
}
```

---

## 2. Segurança, Ferramentas (Tools) e Gerenciamento de Credenciais

### [ISSUE-SEC-01] Deadlock Crítico de Streams no Processo de Execução de Comandos (`RunCommandTool`)
- **Arquivo / Linha:** `AIBWindows/Services/Tools/RunCommandTool.cs:62-68`
- **Severidade:** Crítica
- **Impacto / Risco:** 
  O método `ExecuteAsync` executa o seguinte bloco síncrono:
  ```csharp
  var processTask = Task.Run(() =>
  {
      string output = process.StandardOutput.ReadToEnd();
      string error = process.StandardError.ReadToEnd();
      process.WaitForExit();
      return (output, error);
  });
  ```
  Este é o anti-pattern clássico de deadlock de processos em .NET: `StandardOutput.ReadToEnd()` bloqueia a thread aguardando o fechamento do stream de saída. Se o comando executado gerar dados suficientes na saída de erro padrão (`StandardError`) que excedam a capacidade do buffer do pipe do sistema operacional (tamanho padrão de 4KB a 64KB no Windows), o processo filho fica suspenso aguardando o esvaziamento do buffer de erro, enquanto o processo pai fica bloqueado aguardando o fim do stream de saída. Ambas as partes entram em **Deadlock**, o comando expira no timeout de 30 segundos e é abortado forçadamente com falha.
- **Resolução Técnica:**
  Utilizar leitura assíncrona concorrente de ambos os streams com `Task.WhenAll` ou o método assíncrono `WaitForExitAsync(cancellationToken)`.
```csharp
var outputTask = process.StandardOutput.ReadToEndAsync();
var errorTask = process.StandardError.ReadToEndAsync();

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(cts.Token));
    string finalOutput = (await outputTask) + "\n" + (await errorTask);
    return string.IsNullOrWhiteSpace(finalOutput) ? "Comando executado com sucesso." : finalOutput.Trim();
}
catch (OperationCanceledException)
{
    process.Kill(entireProcessTree: true);
    return "ERRO: O comando demorou mais de 30 segundos e foi interrompido (Timeout).";
}
```

---

### [ISSUE-SEC-02] Injeção de Comandos e Falta de Solicitação de Autorização do Usuário (`RunCommandTool`)
- **Arquivo / Linha:** `AIBWindows/Services/Tools/RunCommandTool.cs:49, 32-58`
- **Severidade:** Crítica
- **Impacto / Risco:** 
  1. O comando é montado usando concatenação direta com escape ingênuo de aspas: `Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\""`. No PowerShell, isso permite injeção arbitrária de comandos e subexpressões (como `$(...)` ou `; Remove-Item -Recurse`).
  2. O `RunCommandTool.ExecuteAsync` **NÃO invoca `CommandConfirmationWindow.ShowAsync`**, ignorando a camada de segurança de confirmação visual do usuário descrita na documentação do projeto. Qualquer chamada de ferramenta executará comandos diretamente no PowerShell com privilégios de usuário sem consentimento explícito.
  3. No timeout, a chamada `process.Kill()` não usa `entireProcessTree: true`, deixando processos filhos órfãos rodando em segundo plano.
- **Resolução Técnica:**
  Integrar a verificação de permissão e modal de confirmação com `CommandConfirmationWindow.ShowAsync`, além de finalizar a árvore completa de processos.
```csharp
public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
{
    var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
    if (!args.TryGetProperty("command", out var cmdElement))
        return "ERRO: O parâmetro 'command' é obrigatório.";

    string command = cmdElement.GetString() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(command))
        return "ERRO: O comando não pode estar vazio.";

    // 1. Confirmação do Usuário via Modal de Segurança
    var ctx = new CommandConfirmationContext
    {
        Tool = Name,
        Command = command,
        Level = userLevel,
        Cwd = Environment.CurrentDirectory
    };

    var (allowed, _) = await CommandConfirmationWindow.ShowAsync(ctx);
    if (!allowed)
    {
        return "EXECUÇÃO RECUSADA: O usuário não autorizou a execução deste comando.";
    }

    // 2. Execução Segura do Processo
    var startInfo = new ProcessStartInfo
    {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Environment.CurrentDirectory
    };

    using var process = new Process { StartInfo = startInfo };
    process.Start();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(cts.Token));

        string finalOutput = (await outputTask) + "\n" + (await errorTask);
        return string.IsNullOrWhiteSpace(finalOutput) ? "Comando executado com sucesso." : finalOutput.Trim();
    }
    catch (OperationCanceledException)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
        return "ERRO: O comando excedeu o limite de 30s e foi finalizado.";
    }
}
```

---

### [ISSUE-SEC-03] Path Traversal e Falta de Restrição de Acesso ao Sistema de Arquivos (`ReadFileTool` e `WriteFileTool`)
- **Arquivo / Linha:** 
  - `AIBWindows/Services/Tools/ReadFileTool.cs:44-59`
  - `AIBWindows/Services/Tools/WriteFileTool.cs:52-60`
- **Severidade:** Alta
- **Impacto / Risco:** 
  As ferramentas `ReadFileTool` e `WriteFileTool` não validam nem canonicam caminhos (`Path.GetFullPath`), permitindo que a IA leia chaves SSH (`~/.ssh/id_rsa`), arquivos de configuração do sistema, credenciais ou sobrescreva executáveis e arquivos críticos do sistema operacional ou inicialização (`Startup`). Além disso, `ReadFileTool` abre o arquivo sem especificar `FileShare.ReadWrite`, causando `IOException` se o arquivo estiver em uso.
- **Resolução Técnica:**
  Implementar normalização de caminhos, validação de diretórios seguros ou confirmação para modificações de arquivos fora do diretório de trabalho.
```csharp
public static class PathSecurityValidator
{
    private static readonly string[] BlockedExtensions = { ".exe", ".dll", ".bat", ".cmd", ".vbs", ".ps1" };
    private static readonly string[] SensitiveDirectories = 
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws")
    };

    public static bool IsPathSafe(string inputPath, bool writeAccess, out string sanitizedPath, out string errorMessage)
    {
        sanitizedPath = Path.GetFullPath(inputPath);
        errorMessage = string.Empty;

        foreach (var sensitive in SensitiveDirectories)
        {
            if (sanitizedPath.StartsWith(sensitive, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "ACESSO NEGADO: Tentativa de acesso a diretório protegido do sistema.";
                return false;
            }
        }

        if (writeAccess && BlockedExtensions.Contains(Path.GetExtension(sanitizedPath).ToLower()))
        {
            errorMessage = "ACESSO NEGADO: Não é permitido criar ou sobrescrever arquivos executáveis ou scripts de sistema.";
            return false;
        }

        return true;
    }
}
```

---

### [ISSUE-SEC-04] Vazamento de Isolamento de Credenciais e Retorno de Strings de Erro como Valor em `CredentialService`
- **Arquivo / Linha:** `AIBWindows/Services/CredentialService.cs:26, 58, 64-77, 79`
- **Severidade:** Alta
- **Impacto / Risco:** 
  1. **Avaliação da Criptografia:** O `CredentialService` utiliza Windows DPAPI (`ProtectedData.Protect` com `DataProtectionScope.CurrentUser`), o que é adequado e seguro para dados locais por usuário no Windows.
  2. **Vazamento entre Sistemas (Global Search Fallback):** As linhas 64-77 implementam uma busca global que, caso uma chave não seja encontrada no sistema solicitado (ex: `openai`), itera todos os outros arquivos `.bin` (ex: `google`, `anthropic`, `custom_api`) e retorna a primeira credencial encontrada com o mesmo nome de chave. Isso quebra o isolamento de credenciais e pode enviar chaves de outros provedores acidentalmente para endpoints da OpenAI/Ollama.
  3. **Convenção de Retorno de Erro em String:** Retornar `"ERRO: Credencial não encontrada..."` como valor de string faz com que consumidores (como `OpenAIService.EnsureClient`) acabem utilizando a mensagem de erro como chave de API real (`Bearer ERRO: ...`), enviando requisições com strings espúrias.
  4. **Path Traversal no parâmetro `system`:** O parâmetro `system` é concatenado diretamente no caminho sem sanitização de caracteres inválidos de nome de arquivo.
- **Resolução Técnica:**
  Remover a busca global entre arquivos de outros sistemas, sanitizar o nome do sistema e usar exceções ou tipo `Result<string?>`.
```csharp
public static class CredentialService
{
    private static string CredentialsDir => Path.Combine(DirectoryService.DataDir, "credentials");

    private static string GetSafeFilePath(string system)
    {
        string safeName = string.Concat(system.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (string.IsNullOrEmpty(safeName))
            throw new ArgumentException("Nome do sistema inválido.", nameof(system));
        return Path.Combine(CredentialsDir, $"{safeName}.bin");
    }

    public static string? RetrieveCredential(string system, string key)
    {
        try
        {
            EnsureDir();
            string filePath = GetSafeFilePath(system);
            if (!File.Exists(filePath)) return null;

            var decryptedJson = DecryptFile(filePath);
            if (string.IsNullOrEmpty(decryptedJson)) return null;

            var creds = JsonSerializer.Deserialize<Dictionary<string, string>>(decryptedJson);
            if (creds != null && creds.TryGetValue(key, out string? value))
            {
                return value;
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CREDENTIALS] Erro ao recuperar chave '{key}': {ex.Message}");
            return null;
        }
    }
}
```

---

## 3. Tratamento de Exceções, Resiliência e Robustez

### [ISSUE-ERR-01] Blocos `catch` Vazios e Supressão Silenciosa de Erros
- **Arquivo / Linha:** 
  - `AIBWindows/Services/ChatHistoryService.cs:30, 52, 120`
  - `AIBWindows/Services/OllamaNativeClient.cs:244`
  - `AIBWindows/Services/OpenAIService.cs:196, 646, 912`
  - `AIBWindows/Views/ChatWindow.xaml.cs:1101`
  - `AIBWindows/Views/FirstRunWindow.xaml.cs:81`
  - `AIBWindows/Views/SettingsWindow.xaml.cs:59`
- **Severidade:** Alta
- **Impacto / Risco:** 
  Exceções causadas por falhas de I/O em disco, corrupção de JSON ou desserialização de ferramentas são silenciadas com blocos `catch { }` sem nenhum log. Por exemplo:
  - Em `ChatHistoryService.cs:52`, se o disco estiver cheio ou sem permissão de escrita, o histórico da conversa é perdido silenciosamente.
  - Em `OllamaNativeClient.cs:244`, se os parâmetros de uma ferramenta contiverem JSON malformado, a ferramenta é descartada silenciosamente do schema enviado à IA sem que o usuário ou desenvolvedor saiba.
- **Resolução Técnica:**
  Inserir logs com `Console.Error.WriteLine` ou `AuditLogService` e tratar exceções específicas (`JsonException`, `IOException`, `UnauthorizedAccessException`).
```csharp
// Em ChatHistoryService.cs
public static void SaveHistory(List<ChatSession> history)
{
    try
    {
        var dir = Path.GetDirectoryName(HistoryFilePath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

        if (history.Count > 50)
            history = history.OrderByDescending(h => h.Timestamp).Take(50).ToList();

        var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(HistoryFilePath, json);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ChatHistory] Falha ao salvar histórico de chat: {ex.Message}");
    }
}
```

---

### [ISSUE-ERR-02] Timeout Infinito (`InfiniteTimeSpan`) e Ausência de Políticas de Retry / Circuit Breaker nas Chamadas LLM
- **Arquivo / Linha:** 
  - `AIBWindows/Services/OpenAIService.cs:728, 789`
  - `AIBWindows/Services/OllamaNativeClient.cs:41`
- **Severidade:** Alta
- **Impacto / Risco:** 
  A configuração `options.NetworkTimeout = Timeout.InfiniteTimeSpan;` é definida tanto para o cliente OpenAI quanto para o HttpClient do Ollama. Caso o servidor Ollama trave ou a conexão com a OpenAI seja interrompida por queda de rede/proxy, a requisição fica pendente indefinidamente, travando o assistente. Além disso, não há política de retentativa exponencial (*Exponential Backoff*) nem *Circuit Breaker* para erros transitórios (HTTP 429 - Rate Limit, HTTP 503 - Service Unavailable, falhas de socket).
- **Resolução Técnica:**
  Definir timeouts realistas (ex: 120s) e configurar políticas de retentativa com jitter para requisições não-streaming e cancelamento cooperativo via `CancellationToken`.
```csharp
// Configuração resiliente do cliente OpenAI / HTTP
var options = new OpenAIClientOptions
{
    NetworkTimeout = TimeSpan.FromSeconds(120),
    RetryPolicy = new ClientRetryPolicy(maxRetries: 3) // ou política com Polly
};
```

---

### [ISSUE-ERR-03] Falha de Reatividade a Mudanças de Configurações em `OpenAIService.EnsureClient`
- **Arquivo / Linha:** `AIBWindows/Services/OpenAIService.cs:761-795`
- **Severidade:** Média
- **Impacto / Risco:** 
  No método `EnsureClient()`, a re-inicialização do cliente só ocorre se `_client == null || _lastModel != modelName`. Se o usuário alterar o `ApiKey`, `ApiUrl` ou `AiProvider` nas configurações mantendo o mesmo `ModelName`, a alteração é completamente ignorada pelo `OpenAIService`, que continuará enviando requisições com a URL ou chave anterior.
- **Resolução Técnica:**
  Rastrear as configurações completas (URL, Chave, Provedor e Modelo) para determinar se a recriação do cliente é necessária.
```csharp
private string? _lastUrl;
private string? _lastKey;
private string? _lastProvider;

private void EnsureClient()
{
    var settings = _settingsService.LoadSettings();
    string modelName = settings.ModelName;
    string apiUrl = settings.ApiUrl;
    string apiKey = settings.ApiKey;
    string provider = settings.AiProvider;

    if (_client == null || _lastModel != modelName || _lastUrl != apiUrl || _lastKey != apiKey || _lastProvider != provider)
    {
        // Criação do novo cliente atualizado...
        _lastModel = modelName;
        _lastUrl = apiUrl;
        _lastKey = apiKey;
        _lastProvider = provider;
    }
}
```

---

### [ISSUE-ERR-04] Ausência de Manipuladores Globais de Exceção Não-Tratada em `App.xaml.cs`
- **Arquivo / Linha:** `AIBWindows/App.xaml.cs:29-110`
- **Severidade:** Alta
- **Impacto / Risco:** 
  A classe `App` não registra manipuladores para `DispatcherUnhandledException`, `AppDomain.CurrentDomain.UnhandledException` ou `TaskScheduler.UnobservedTaskException`. Qualquer exceção disparada em threads secundárias, chamadas assíncronas *fire-and-forget* (ex: `_ = AuditLogService.AppendAsync(...)`) ou eventos de UI resultará em encerramento abrupto (*crash*) do processo sem relatório de erro para o usuário.
- **Resolução Técnica:**
  Registrar manipuladores globais no `OnStartup`.
```csharp
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

    this.DispatcherUnhandledException += (s, args) =>
    {
        Console.Error.WriteLine($"[CRITICAL UI EXCEPTION]: {args.Exception}");
        MessageBox.Show($"Ocorreu um erro inesperado na interface:\n{args.Exception.Message}", "Erro - AIB", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true; // Previne crash da aplicação
    };

    AppDomain.CurrentDomain.UnhandledException += (s, args) =>
    {
        Console.Error.WriteLine($"[FATAL DOMAIN EXCEPTION]: {args.ExceptionObject}");
    };

    TaskScheduler.UnobservedTaskException += (s, args) =>
    {
        Console.Error.WriteLine($"[UNOBSERVED TASK EXCEPTION]: {args.Exception}");
        args.SetObserved();
    };
    
    // Continuação da inicialização...
}
```

---

## 4. Qualidade e Cobertura da Suíte de Testes (`AIB.Tests`)

### [ISSUE-TEST-01] Teste Nulo Trivial em `UnitTest1.cs`
- **Arquivo / Linha:** `AIB.Tests/UnitTest1.cs:1-10`
- **Severidade:** Baixa
- **Impacto / Risco:** 
  O arquivo `UnitTest1.cs` contém um teste vazio (`public void Test1() { }`) gerado pelo template do dotnet, poluindo o relatório de testes sem agregar nenhuma cobertura ou validação real.
- **Resolução Técnica:**
  Remover o arquivo ou substituí-lo por testes de integração reais.

---

### [ISSUE-TEST-02] Sync-over-Async e Aviso de Deadlock no Mock de Teste (`OllamaNativeClientTests`)
- **Arquivo / Linha:** `AIB.Tests/OllamaNativeClientTests.cs:38`
- **Severidade:** Média
- **Impacto / Risco:** 
  A linha `var content = request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult();` usa bloqueio síncrono em tarefa assíncrona dentro da configuração do Moq, gerando o aviso de compilação `xUnit1031` e podendo causar travamento na execução paralela da suíte de testes do xUnit.
- **Resolução Técnica:**
  Configurar o Mock para operar de forma assíncrona sem `GetResult()`.
```csharp
handlerMock
    .Protected()
    .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>()
    )
    .Returns(async (HttpRequestMessage request, CancellationToken token) =>
    {
        string? content = request.Content != null ? await request.Content.ReadAsStringAsync(token) : null;
        return new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"done\":true}")
        };
    });
```

---

### [ISSUE-TEST-03] Lacuna Crítica de Cobertura de Testes Unitários
- **Arquivo / Linha:** Projeto `AIB.Tests`
- **Severidade:** Alta
- **Impacto / Risco:** 
  A suíte de testes cobre apenas o cálculo aritmético de níveis (`LevelServiceTests`) e a serialização básica do `OllamaNativeClient`. Módulos fundamentais do sistema possuem **0% de cobertura de testes**:
  - **`OpenAIService`**: O loop ReAct, agregação de chunks de streaming, execução de ferramentas, fallback de regex e trimming do histórico de mensagens não possuem nenhum teste automatizado.
  - **`Tools` (`RunCommandTool`, `ReadFileTool`, `WriteFileTool`, `ToolRegistry`)**: Nenhum teste valida tratamento de caminhos inexistentes, JSON inválido, timeouts ou níveis de permissão.
  - **`CredentialService`**: Nenhuma validação para o armazenamento, recuperação criptografada com DPAPI ou limpeza de cofre.
  - **`ChatHistoryService` e `SettingsService`**: Nenhuma validação para persistência de dados ou migração.
- **Resolução Técnica:**
  Implementar classes de teste dedicadas para as ferramentas e serviços essenciais.
  
#### Exemplo de Teste para `ToolRegistry` e `ReadFileTool`:
```csharp
using System.IO;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using AIB.Services;
using AIB.Services.Tools;

namespace AIB.Tests
{
    public class ToolRegistryTests
    {
        [Fact]
        public async Task ExecuteToolAsync_WhenLevelIsInsufficient_ShouldReturnAccessDenied()
        {
            // Arrange
            var registry = new ToolRegistry();

            // Act (tentando rodar ferramenta com userLevel = 0 se exigisse nível superior)
            string result = await registry.ExecuteToolAsync("non_existent_tool", "{}", 1);

            // Assert
            result.Should().Contain("não encontrada no registry");
        }

        [Fact]
        public async Task ReadFileTool_WhenFileDoesNotExist_ShouldReturnError()
        {
            // Arrange
            var tool = new ReadFileTool();
            string args = "{\"path\": \"C:\\\\non_existent_file_12345.txt\"}";

            // Act
            string result = await tool.ExecuteAsync(args, 1);

            // Assert
            result.Should().Contain("ERRO: Arquivo não encontrado");
        }
    }
}
```

---

## 5. Tabela de Sumário de Descobertas

| ID | Área | Componente / Arquivo | Severidade | Descrição do Problema |
|---|---|---|---|---|
| ISSUE-SEC-01 | Segurança / Concorrência | `RunCommandTool.cs:62` | **Crítica** | Deadlock no pipe de stdout/stderr com `Process.WaitForExit` |
| ISSUE-SEC-02 | Segurança / Permissões | `RunCommandTool.cs:49` | **Crítica** | Injeção de comando PowerShell e bypass do modal de confirmação do usuário |
| ISSUE-SEC-03 | Segurança / Arquivos | `ReadFileTool.cs`, `WriteFileTool.cs` | **Alta** | Ausência de restrição de diretório (*Path Traversal*) e sanitização |
| ISSUE-SEC-04 | Segurança / Credenciais | `CredentialService.cs:64` | **Alta** | Falha de isolamento de credenciais (busca global) e convenção de erro string |
| ISSUE-UI-01 | WPF / Memória | `ChatWindow.xaml.cs:44` | **Alta** | Eventos não desinscritos retendo Views na memória (Memory Leak) |
| ISSUE-UI-03 | WPF / UI Thread | `ChatWindow.xaml.cs:934` | **Alta** | `Dispatcher.Invoke` síncrono disparado de thread de background com risco de Deadlock |
| ISSUE-ERR-01 | Resiliência / Erros | `ChatHistoryService`, `OllamaNativeClient`, etc. | **Alta** | Múltiplos blocos `catch { }` vazios suprimindo exceções e erros de I/O |
| ISSUE-ERR-02 | Resiliência / Rede | `OpenAIService.cs`, `OllamaNativeClient.cs` | **Alta** | Timeout infinito de rede e ausência de Retry Policy / Circuit Breaker |
| ISSUE-ERR-04 | Resiliência / Crash | `App.xaml.cs:29` | **Alta** | Ausência de manipuladores globais de exceção não-tratada |
| ISSUE-TEST-03 | Testes / Cobertura | `AIB.Tests` | **Alta** | Lacuna crítica de testes em `OpenAIService`, `Tools`, `CredentialService` |
| ISSUE-UI-02 | WPF / Performance | `ContextSidebar.xaml.cs:29` | **Média** | Timers contínuos sem descarte gerando consumo de CPU e retenção |
| ISSUE-UI-05 | WPF / Interop | `SettingsWindow.xaml.cs:281` | **Média** | Potencial vazamento de memória não gerenciada em P/Invoke `EnableBlur` |
| ISSUE-ERR-03 | Resiliência / Config | `OpenAIService.cs:761` | **Média** | Alterações de API URL/Key não atualizam o cliente LLM sem troca de modelo |
| ISSUE-TEST-02 | Testes / Concorrência | `OllamaNativeClientTests.cs:38` | **Média** | Sync-over-async no setup de mock gerando `xUnit1031` |
| ISSUE-UI-04 | WPF / XAML | `FirstRunWindow.xaml:116` | **Baixa** | `ScrollViewer.CanContentScroll="False"` desativando virtualização de UI |
| ISSUE-TEST-01 | Testes / Qualidade | `UnitTest1.cs:1` | **Baixa** | Teste vazio residual não funcional |
