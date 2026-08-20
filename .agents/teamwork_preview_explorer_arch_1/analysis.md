# Relatório de Auditoria Arquitetural e Boas Práticas — AIB (Assistente Inteligente Baseado em IA)

**Data da Análise**: 19 de Agosto de 2026  
**Escopo**: Arquitetura C#, Injeção de Dependências (DI), Ciclo de Vida da Aplicação, Padrão MVVM, Separação de Preocupações (SoC), Design de Serviços, Acoplamento e Código Limpo.  
**Projeto Avaliado**: `AIBWindows` (.NET 8.0 WPF) e `AIB.Tests` (xUnit).

---

## Sumário Executivo

A análise estática detalhada do código-fonte do projeto AIB revelou graves deficiências estruturais e desvios fundamentais dos padrões arquiteturais recomendados para aplicações .NET/WPF modernas. Entre os principais achados destacam-se:

1. **Ausência Completa de Container IoC / Injeção de Dependências**: O projeto não utiliza `Microsoft.Extensions.DependencyInjection` ou qualquer container IoC. Todas as dependências são instanciadas diretamente via `new` ou expostas através de classes/métodos estáticos globais mutáveis.
2. **Violação Total do Padrão MVVM (God Class em Views)**: Não existe uma única classe ViewModel em todo o projeto. Classes como `ChatWindow.xaml.cs` (1.137 linhas) e `FirstRunWindow.xaml.cs` (439 linhas) acumulam responsabilidades de apresentação, controle de fluxo, I/O síncrono, regras de negócio, cálculos de gamificação e renderização manual de árvore visual em C#.
3. **Proliferação de Serviços Stub / Vazios (Cascas Ocas)**: Foram identificados 10 serviços que são implementações dummy/stubs não funcionais (`AuditLogService`, `MemoryService`, `OcrService`, `ShadowAssistantService`, `ShadowHistoryService`, `SkillService`, `VoiceService`, `ReminderService`, `ContextService`, `TestRunner`). Embora o `.csproj` referencie bibliotecas robustas (`LiteDB`, `SmartComponents.LocalEmbeddings`, `Whisper.net`, `PdfPig`), elas não estão integradas aos serviços.
4. **Vazamento de Abstrações e Acoplamento Excessivo**: A interface de domínio `ITool` depende diretamente de tipos da SDK da OpenAI (`OpenAI.Chat.ChatTool`). A classe `SettingsService` acumula responsabilidades de I/O de configuração e chamadas de rede HTTP ao Ollama.
5. **Tratamento Antipedagógico de Erros em Segurança**: O `CredentialService` utiliza strings de erro como convenção (`"ERRO: ..."`) inspecionadas com `.StartsWith("ERRO")` por chamadores na UI, gerando fragilidade e risco de falhas silenciosas.

---

## Tabela Geral de Descobertas e Severidade

| ID | Componente / Arquivo | Problema / Anti-pattern | Severidade | Impacto Principal |
|---|---|---|---|---|
| **ARC-01** | `App.xaml.cs:18,65` | Ausência de Container de Injeção de Dependências (DI/IoC) | **Crítica** | Testabilidade nula, alto acoplamento, ciclo de vida descontrolado |
| **ARC-02** | `Views/ChatWindow.xaml.cs:1-1137` | Monolith / God Class na View e Violação Completa de MVVM | **Crítica** | Dívida técnica extrema, lógica de negócio presa na UI Thread |
| **ARC-03** | `Services/*Service.cs` (10 arquivos) | Coleção de Serviços Stub / Vazios não funcionais | **Crítica** | Funcionalidades de UI (OCR, Voz, Memória, Logs) são cascas ocas |
| **ARC-04** | `Services/OpenAIService.cs:1-993` | God Class de IA, Concorrência Frágil e Acoplamento Concreto | **Crítica** | Dificuldade de manutenção, instanciações ad-hoc de clientes HTTP |
| **ARC-05** | `App.xaml.cs:38-43, 157-182` | Execução CLI síncrona acoplada no ciclo de vida WPF | **Alta** | Violação de SRP, bloqueio e supressão de contexto de sincronização |
| **ARC-06** | `Views/ChatWindow.xaml.cs:235-389` | Geração procedural de UI e estilização Markdig em C# | **Alta** | Incompatível com DataTemplates XAML, memory leaks de elementos |
| **ARC-07** | `Views/SettingsWindow.xaml.cs:214-218` | Acoplamento direto entre Views via busca global na árvore | **Alta** | Quebra de encapsulamento e acoplamento rígido entre janelas |
| **ARC-08** | `Views/FirstRunWindow.xaml.cs:1-439` | Wizard procedimental sem ViewModel e Conversores embutidos | **Alta** | Dificuldade de evolução e violação de SoC |
| **ARC-09** | `Services/ITool.cs:23` | Vazamento do SDK da OpenAI para o contrato de domínio | **Alta** | Impossibilita uso agnóstico de ferramentas com outros provedores |
| **ARC-10** | `Services/SettingsService.cs:104-134` | Violação de SRP: I/O de configurações misturado com HTTP | **Alta** | Falha arquitetural de responsabilidade única |
| **ARC-11** | `Services/CredentialService.cs:49,79` | Convenção de erro baseada em strings ("ERRO...") | **Alta** | Fragilidade de controle de fluxo e risco de segurança |
| **ARC-12** | `Services/DirectoryService.cs:8-9,50-51` | Estado estático mutável e caminhos relativos frágeis | **Média** | Risco de concorrência e quebra em deploys empacotados |
| **ARC-13** | `Services/ToolRegistry.cs:64-79` | Instanciação rígida de ferramentas (Violação OCP/DIP) | **Média** | Impossibilidade de registro dinâmico via DI ou plugins |
| **ARC-14** | `Views/CommandConfirmationWindow.xaml.cs:63-81` | Chamada modal estática na UI acoplada a ferramentas de backend | **Média** | Dificulta execução headless e testes automatizados |
| **ARC-15** | `Services/GibberishVoiceService.cs:9,22-29` | Vazamento de recursos nativos de áudio não descartados | **Média** | Ocupação contínua de drivers de som |

---

## Detalhamento Técnico das Descobertas

---

### 1. Ciclo de Vida da Aplicação e Injeção de Dependências

#### [ARC-01] Ausência de Container de Injeção de Dependências (IoC) e Instanciação Ad-Hoc
- **Arquivo / Linhas**: `AIBWindows/App.xaml.cs:18, 45, 65`, disseminado por todo o projeto.
- **Severidade**: **Crítica**
- **Descrição Técnica**:
  O projeto não possui configuração de Injeção de Dependências. Em `App.xaml.cs`, `_settingsService = new();` e `_chatWindow = new ChatWindow();` são instanciados diretamente em campos privados. As classes de serviço utilizam quase exclusivamente métodos e estados estáticos (`DirectoryService`, `CredentialService`, `AuditLogService`, `ChatHistoryService`, `LevelService`, `GibberishVoiceService`). Isso viola diretamente o princípio de Inversão de Dependência (DIP - SOLID), impede a criação de dublês de teste (mocks/stubs), inviabiliza testes de unidade com xUnit e torna o ciclo de vida dos objetos incontrolável.
- **Resolução Técnica Recomendada**:
  Adicionar os pacotes `Microsoft.Extensions.Hosting` e `Microsoft.Extensions.DependencyInjection`. Estruturar a inicialização da aplicação WPF utilizando `IHost`, registrando serviços com seus respectivos ciclos de vida (`Singleton`, `Transient`, `Scoped`) e injetando dependências através dos construtores.

```csharp
// Refatoração sugerida: App.xaml.cs com Generic Host
public partial class App : Application
{
    private IHost _host = null!;

    public static IServiceProvider Services => ((App)Current)._host.Services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((context, services) =>
            {
                // Configuração e Infraestrutura
                services.AddSingleton<IAppDirectories, AppDirectories>();
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<ICredentialVault, DpapiCredentialVault>();
                services.AddSingleton<IAuditLogService, AuditLogService>();
                services.AddHttpClient();

                // Serviços de IA e Ferramentas
                services.AddSingleton<IToolRegistry, ToolRegistry>();
                services.AddTransient<ITool, RunCommandTool>();
                services.AddTransient<ITool, ReadFileTool>();
                services.AddTransient<ITool, WriteFileTool>();
                services.AddSingleton<IAiCompletionService, OpenAiService>();

                // ViewModels
                services.AddTransient<ChatViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<FirstRunViewModel>();

                // Views
                services.AddTransient<ChatWindow>();
                services.AddTransient<SettingsWindow>();
                services.AddTransient<FirstRunWindow>();
            })
            .Build();

        await _host.StartAsync();

        var chatWindow = _host.Services.GetRequiredService<ChatWindow>();
        chatWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        using (_host)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        base.OnExit(e);
    }
}
```

---

#### [ARC-05] Execução CLI Síncrona Acoplada no Ciclo de Vida da Aplicação WPF
- **Arquivo / Linhas**: `AIBWindows/App.xaml.cs:38-43, 157-182`
- **Severidade**: **Alta**
- **Descrição Técnica**:
  Em `App.OnStartup`, ao identificar argumentos de linha de comando (`--test-rag`, `--test-tool`, `--test-all`), o código executa `System.Threading.SynchronizationContext.SetSynchronizationContext(null);` e bloqueia a thread de UI com `RunCliCommandAsync(e.Args).GetAwaiter().GetResult();`, finalizando o processo com `Environment.Exit(...)`. Isso mistura as responsabilidades de uma aplicação com interface gráfica (WPF) e utilitário de terminal (CLI), introduzindo risco de deadlocks e violando o Princípio da Responsabilidade Única (SRP).
- **Resolução Técnica Recomendada**:
  Separar o ponto de entrada (`Program.cs`) entre um executor CLI independente (`ICliCommandRunner`) e a inicialização da aplicação WPF.

```csharp
// Refatoração sugerida: Program.cs
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--test"))
        {
            var host = CreateCliHost(args);
            var runner = host.Services.GetRequiredService<ICliTestRunner>();
            return runner.ExecuteAsync(args).GetAwaiter().GetResult();
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
```

---

### 2. Padrões de Projeto e MVVM (Separação de Preocupações)

#### [ARC-02] Monolith / God Class em `ChatWindow.xaml.cs` e Violação Completa de MVVM
- **Arquivo / Linhas**: `AIBWindows/Views/ChatWindow.xaml.cs:1-1137`
- **Severidade**: **Crítica**
- **Descrição Técnica**:
  A classe `ChatWindow.xaml.cs` possui 1.137 linhas e acumula todas as funções do sistema:
  - Instanciação manual de serviços (`OpenAIService`, `ShadowAssistantService`, `SettingsService`, `VoiceService`, `OcrService`).
  - Lógica de cálculo de gamificação, progressão de nível e interpolação de cores para a barra de XP (`RefreshLevelUI`, linhas 176-219).
  - Geração procedural de componentes de UI (`AddUserBubble`, `AddAgentBubble`, `AddTypingIndicator`, linhas 235-389).
  - Execução e cancelamento de tarefas de IA (`SendButton_Click`, linhas 450-627).
  - Orquestração de áudio e reconhecimento de voz (linhas 673-750).
  - Captura e gerenciamento de múltiplos monitores via WinForms (`EnsureShadowWidgetsForAllScreens`, `Screen.AllScreens`, linhas 1067-1135).
  - Animações manuais via `Storyboard` imperativo em C#.
  Não existe `ChatViewModel`. Todo o estado é armazenado em variáveis privadas mutáveis na janela.
- **Resolução Técnica Recomendada**:
  Adotar o pacote `CommunityToolkit.Mvvm` e migrar todo o estado e lógica de orquestração para `ChatViewModel`. A `ChatWindow` deve conter apenas código estritamente relacionado à apresentação ou comportamentos de janela não bindáveis.

```csharp
// Refatoração sugerida: ChatViewModel.cs
public partial class ChatViewModel : ObservableObject
{
    private readonly IAiCompletionService _aiService;
    private readonly ISettingsService _settingsService;
    private readonly ILevelService _levelService;
    private readonly IVoiceService _voiceService;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private int _currentLevel;

    [ObservableProperty]
    private double _levelProgress;

    [ObservableProperty]
    private string _tokenCounterText = string.Empty;

    public ObservableCollection<ChatMessageItemViewModel> Messages { get; } = new();

    public IAsyncRelayCommand SendMessageCommand { get; }
    public IRelayCommand ToggleVoiceCommand { get; }

    public ChatViewModel(
        IAiCompletionService aiService,
        ISettingsService settingsService,
        ILevelService levelService,
        IVoiceService voiceService)
    {
        _aiService = aiService;
        _settingsService = settingsService;
        _levelService = levelService;
        _voiceService = voiceService;

        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, () => !string.IsNullOrWhiteSpace(InputText) || IsSending);
    }

    private async Task SendMessageAsync()
    {
        if (IsSending)
        {
            _aiService.CancelGeneration();
            return;
        }

        var text = InputText.Trim();
        InputText = string.Empty;
        IsSending = true;

        Messages.Add(new ChatMessageItemViewModel { Sender = MessageSender.User, Content = text });

        var responseMessage = new ChatMessageItemViewModel { Sender = MessageSender.Assistant, Content = "" };
        Messages.Add(responseMessage);

        try
        {
            await foreach (var chunk in _aiService.StreamResponseAsync(text))
            {
                responseMessage.Content += chunk;
            }
        }
        finally
        {
            IsSending = false;
        }
    }
}
```

---

#### [ARC-06] Geração Procedural de UI e Manipulação de Árvore Visual em C#
- **Arquivo / Linhas**: `AIBWindows/Views/ChatWindow.xaml.cs:235-311, 346-389, 535-547`
- **Severidade**: **Alta**
- **Descrição Técnica**:
  As bolhas de chat de usuário e assistente são criadas via código C# instanciando `Border`, `LinearGB`, `SolidCB`, `MarkdownViewer` e inseridas diretamente em `MessagesPanel.Children.Add(border)`. Além de impedir o uso de VirtualizingStackPanel e DataTemplates nativos do WPF, isso dificulta a aplicação de temas e gera vazamento de memória por manter referências em manipuladores de eventos embutidos (ex: `viewer.PreviewMouseWheel += ...`).
- **Resolução Técnica Recomendada**:
  Substituir o `StackPanel` por um `ItemsControl` (ou `ListBox`) virtualizado, utilizando `DataTemplateSelector` no XAML.

```xml
<!-- Refatoração sugerida: ChatWindow.xaml -->
<ItemsControl ItemsSource="{Binding Messages}">
    <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate>
            <VirtualizingStackPanel />
        </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplateSelector>
        <local:ChatMessageTemplateSelector 
            UserTemplate="{StaticResource UserMessageTemplate}"
            AssistantTemplate="{StaticResource AssistantMessageTemplate}"
            ToolTemplate="{StaticResource ToolExecutionTemplate}" />
    </ItemsControl.ItemTemplateSelector>
</ItemsControl>
```

---

#### [ARC-07] Acoplamento Rígido Direto Entre Janelas (Cross-View Querying)
- **Arquivo / Linhas**: `AIBWindows/Views/SettingsWindow.xaml.cs:214-218`
- **Severidade**: **Alta**
- **Descrição Técnica**:
  No método `Save_Click` da `SettingsWindow`, a janela localiza a `ChatWindow` ativa no aplicativo via:
  ```csharp
  var chatWindow = System.Windows.Application.Current.Windows.OfType<ChatWindow>().FirstOrDefault();
  if (chatWindow != null)
  {
      chatWindow.ApplyCharacterUI();
  }
  ```
  Essa prática viola o encapsulamento, cria acoplamento circular entre Views e falha silenciosamente caso a janela mude de tipo ou seja reconstruída.
- **Resolução Técnica Recomendada**:
  Utilizar um barramento de mensagens fracamente acoplado (`IMessenger` do `CommunityToolkit.Mvvm` ou `MediatR`) para publicar um evento de alteração de configurações.

```csharp
// Refatoração: Publicação do evento no ViewModel
public record CharacterSettingsChangedMessage(string CharacterName);

// No SettingsViewModel:
_messenger.Send(new CharacterSettingsChangedMessage(SelectedCharacter));

// No ChatViewModel:
_messenger.Register<CharacterSettingsChangedMessage>(this, (r, m) =>
{
    ActiveCharacterName = m.CharacterName;
    ResetChatContext();
});
```

---

#### [ARC-08] FirstRunWindow: Lógica de Negócio e Conversores Acoplados ao Code-Behind
- **Arquivo / Linhas**: `AIBWindows/Views/FirstRunWindow.xaml.cs:18-32, 59-93, 325-393`
- **Severidade**: **Alta**
- **Descrição Técnica**:
  A classe `ValueToStarForegroundConverter` foi declarada dentro do arquivo `FirstRunWindow.xaml.cs` (linhas 18-32). O método `LoadAgents` lê e desserializa arquivos JSON do disco diretamente na UI Thread. Os métodos de validação e persistência do cofre (`CredentialService.StoreCredentialAsync`) são acionados via `async void Save_Click`, sem captura global de exceções, com risco de encerramento inesperado do processo (crash).
- **Resolução Técnica Recomendada**:
  1. Mover `ValueToStarForegroundConverter` para o namespace `AIB.Converters`.
  2. Implementar `FirstRunViewModel` com `IAsyncRelayCommand` e fluxo de passos baseado em estados observáveis.

---

### 3. Design de Serviços, Interfaces e Serviços Stub

#### [ARC-03] Proliferação de Serviços Stub / Vazios (Cascas Ocas)
- **Arquivos / Linhas**:
  - `AIBWindows/Services/AuditLogService.cs:4` — `AppendAsync(object logData) => Task.CompletedTask;`
  - `AIBWindows/Services/MemoryService.cs:4-5` — `DeleteMemory(int id) {}`, `GetRecentMemories => new List<object>();`
  - `AIBWindows/Services/OcrService.cs:4` — `ExtractTextFromActiveScreenAsync => Task.FromResult("");`
  - `AIBWindows/Services/ShadowAssistantService.cs:3-12` — Métodos vazios, eventos nunca disparados.
  - `AIBWindows/Services/ShadowHistoryService.cs:4` — `Suggestions => new List<object>();`
  - `AIBWindows/Services/SkillService.cs:8-11` — `GetSkillCount => 0`, `ListLocalSkills => new List<LocalSkill>();`
  - `AIBWindows/Services/VoiceService.cs:8-15` — Métodos vazios, `Dispose()` vazio sem `IDisposable`.
  - `AIBWindows/Services/ReminderService.cs:9-14` — `ActiveReminders => new List<Reminder>();` (aloca nova lista a cada chamada).
  - `AIBWindows/Services/ContextService.cs:6-15` — Retorna listas vazias e ignora inserções.
  - `AIBWindows/Services/TestRunner.cs:3-7` — Retorna `Task.FromResult(true)` incondicionalmente.
- **Severidade**: **Crítica**
- **Descrição Técnica**:
  Dez serviços centrais do sistema são implementações vazias (stubs). Componentes de UI (como o botão de visão OCR em `ChatWindow:642`, o botão de escuta Whisper em `ChatWindow:677`, a barra lateral `ContextSidebar:21-24` e os testes CLI de RAG em `App:162`) executam chamadas a esses stubs que não produzem nenhum resultado ou retornam dados fictícios.
  O arquivo de projeto `AIB.csproj` já inclui referências a `LiteDB`, `SmartComponents.LocalEmbeddings`, `Whisper.net` e `PdfPig`, mas os serviços correspondentes não as utilizam.
- **Resolução Técnica Recomendada**:
  Extrair interfaces para todos os serviços (`IAuditLogService`, `IMemoryService`, `IOcrService`, `IShadowAssistantService`, `ISkillService`, `IVoiceService`, `IReminderService`, `IContextService`, `ITestRunner`) e implementar as classes concretas conectando os pacotes NuGet existentes.

```csharp
// Exemplo de Contrato e Implementação para IAuditLogService
public interface IAuditLogService
{
    Task AppendAsync<T>(string eventName, T payload, CancellationToken ct = default);
}

public class FileAuditLogService : IAuditLogService
{
    private readonly string _logFilePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public FileAuditLogService(IAppDirectories directories)
    {
        _logFilePath = Path.Combine(directories.LogsDir, "audit.jsonl");
    }

    public async Task AppendAsync<T>(string eventName, T payload, CancellationToken ct = default)
    {
        var record = new
        {
            Timestamp = DateTime.UtcNow,
            Event = eventName,
            Data = payload
        };

        var line = JsonSerializer.Serialize(record) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);

        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_logFilePath, line, ct).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
```

---

#### [ARC-04] Monolithic God Class em `OpenAIService.cs` e Acoplamento Concreto
- **Arquivo / Linhas**: `AIBWindows/Services/OpenAIService.cs:1-993`
- **Severidade**: **Crítica**
- **Descrição Técnica**:
  `OpenAIService` é uma classe de 993 linhas que acumula:
  - Inicialização de clientes e tokenização com Tiktoken (`Microsoft.ML.Tokenizers`).
  - Warmup e envio de requisições HTTP manuais brutas para travamento de VRAM no Ollama (`httpClient.PostAsync($"{apiUrl}/api/generate", content)`).
  - Montagem de system prompt contextual lendo o diretório home e lista de skills locais.
  - Orquestração do loop ReAct com parsing de chamadas de ferramentas via regex e via JSON nativo.
  - Instanciação repetida de `new OllamaNativeClient(...)` dentro do loop assíncrono (linhas 126 e 241).
  - Manipulação não thread-safe de `List<ChatMessage> _history` compartilhada entre tarefas em background (`WarmupAndKeepAliveAsync`) e chamadas na UI.
- **Resolução Técnica Recomendada**:
  Decompor `OpenAIService` em:
  1. `IAiCompletionService` (com implementações `OpenAiClientAdapter` e `OllamaClientAdapter`).
  2. `IReActCoordinator` (para o loop de raciocínio e execução de ferramentas).
  3. `ISystemPromptFactory` (para construção dinâmica do prompt com diretórios e skills).
  4. `ITokenizerService` (para gestão de contagem e limiares de tokens).

---

#### [ARC-09] Vazamento do SDK da OpenAI para a Interface de Domínio `ITool`
- **Arquivo / Linhas**: `AIBWindows/Services/ITool.cs:23`
- **Severidade**: **Alta**
- **Descrição Técnica**:
  A interface `ITool` expõe:
  ```csharp
  ChatTool ChatToolDefinition { get; }
  ```
  Isso vincula toda ferramenta de negócio ou automação de sistema diretamente ao namespace `OpenAI.Chat` do SDK proprietário da OpenAI. Caso o projeto utilize outros LLMs locais, SDK da Anthropic, Google Gemini nativo ou modelos locais via Ollama puro, a definição de ferramentas fica presa a tipos de terceiros.
- **Resolução Técnica Recomendada**:
  Definir um contrato neutro `ToolDefinition` no domínio da aplicação e mapear para `OpenAI.Chat.ChatTool` somente na camada de infraestrutura/adaptador.

```csharp
// Refatoração de Domínio: ITool neutra
public record ToolParameter(string Name, string Type, string Description, bool Required);

public record ToolSchema(string Name, string Description, IReadOnlyList<ToolParameter> Parameters);

public interface ITool
{
    string Name { get; }
    string Description { get; }
    int RequiredLevel { get; }
    ToolSchema Schema { get; }
    Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1, CancellationToken ct = default);
}
```

---

#### [ARC-13] Instanciação Rígida de Ferramentas no `ToolRegistry` (Violação OCP/DIP)
- **Arquivo / Linhas**: `AIBWindows/Services/ToolRegistry.cs:64-79`
- **Severidade**: **Média**
- **Descrição Técnica**:
  O método `RegisterNativeTools()` instancia manualmente `new RunCommandTool()`, `new ReadFileTool()`, `new WriteFileTool()`. Não é possível injetar ferramentas mockadas em testes ou estender o conjunto de ferramentas sem alterar o código-fonte interno de `ToolRegistry`.
- **Resolução Técnica Recomendada**:
  Injetar `IEnumerable<ITool>` via construtor no `ToolRegistry`.

```csharp
public class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public ITool? GetTool(string name) => _tools.GetValueOrDefault(name);
    public IEnumerable<ITool> GetActiveTools(int userLevel) => _tools.Values.Where(t => t.RequiredLevel <= userLevel);
}
```

---

### 4. Gestão de Configurações, Credenciais e Contexto

#### [ARC-10] Violação de SRP no `SettingsService`: I/O de Configuração com Chamadas HTTP
- **Arquivo / Linhas**: `AIBWindows/Services/SettingsService.cs:104-134`
- **Severidade**: **Alta**
- **Descrição Técnica**:
  O `SettingsService` possui o método `GetOllamaModelsAsync(string baseUrl)`, que utiliza um `HttpClient` interno para consultar a API `/api/tags` do Ollama e obter a lista de modelos instalados. Um serviço de configurações (`SettingsService`) deve limitar-se à persistência e recuperação das preferências do usuário (`UserAppSettings`). A descoberta de modelos de IA é uma responsabilidade da camada de comunicação com o provedor de IA.
- **Resolução Técnica Recomendada**:
  Mover `GetOllamaModelsAsync` para `IOllamaModelDiscoveryService` ou integrar ao `IOllamaClient`.

---

#### [ARC-11] Convenção Frágil de Retorno de Erros por String no `CredentialService`
- **Arquivo / Linhas**: `AIBWindows/Services/CredentialService.cs:49, 79, 83, 92, 96`, `App.xaml.cs:133`, `FirstRunWindow.xaml.cs:360`
- **Severidade**: **Alta**
- **Descrição Técnica**:
  Os métodos de `CredentialService` retornam mensagens de texto iniciadas pelo prefixo `"ERRO"` em caso de falha (ex: `"ERRO: Credencial não encontrada em nenhum sistema."`). Os consumidores checam:
  ```csharp
  if (CredentialService.RetrieveCredential("openai", "ApiKey").StartsWith("ERRO"))
  ```
  Essa abordagem é frágil, impede a distinção entre tipos de erro (arquivo inexistente vs. chave inválida vs. erro de decriptação DPAPI) e pode quebrar caso um valor legítimo comece com os caracteres literais "ERRO".
- **Resolução Técnica Recomendada**:
  Implementar um padrão `Result<T>` ou utilizar retorno anulável (`string?`) e lançar exceções tipadas de segurança (`CredentialNotFoundException`, `VaultDecryptionException`).

```csharp
// Refatoração sugerida: ICredentialVault com Tipagem Segura
public interface ICredentialVault
{
    Task StoreCredentialAsync(string system, string key, string secret, CancellationToken ct = default);
    Task<string?> RetrieveCredentialAsync(string system, string key, CancellationToken ct = default);
    Task<bool> HasCredentialAsync(string system, string key, CancellationToken ct = default);
    Task WipeAllCredentialsAsync(CancellationToken ct = default);
}
```

---

#### [ARC-12] Estado Estático Mutável e Resolução Relativa de Diretórios em `DirectoryService`
- **Arquivo / Linhas**: `AIBWindows/Services/DirectoryService.cs:8-9, 50-51, 83-92`
- **Severidade**: **Média**
- **Descrição Técnica**:
  `DirectoryService` armazena `_dataDir` e `_tempDir` em campos estáticos mutáveis. O método `ApplyFromSettings` sobrescreve esses valores em tempo de execução sem sincronização de threads.
  Nas linhas 50-51, o método `EnsureDirectories` tenta localizar a pasta `character` navegando caminhos relativos arbitrários (`..\\..\\..\\character` e `..\\..\\..\\..\\character`). Isso causa quebras quando o executável é instalado em diretórios de produção ou iniciado a partir de outro diretório de trabalho (`Working Directory`).
- **Resolução Técnica Recomendada**:
  Criar a abstração `IAppDirectories` injetada via DI, calculando caminhos a partir de `AppContext.BaseDirectory` e `Environment.SpecialFolder.UserProfile`.

---

#### [ARC-14] Invocação de Diálogo Modal da UI Acoplada a Ferramentas de Execução de Comandos
- **Arquivo / Linhas**: `AIBWindows/Views/CommandConfirmationWindow.xaml.cs:63-81`
- **Severidade**: **Média**
- **Descrição Técnica**:
  `CommandConfirmationWindow` disponibiliza o método estático `ShowAsync(CommandConfirmationContext ctx)` que aciona `System.Windows.Application.Current.Dispatcher.InvokeAsync`. Isso acopla ferramentas de execução de comandos (como `RunCommandTool`) à thread de UI do WPF e ao objeto `MainWindow`, impedindo que ferramentas rodem de maneira desacoplada em modo CLI, testes unitários ou ambientes de automação.
- **Resolução Técnica Recomendada**:
  Introduzir a interface `IUserConfirmationService` com implementações `WpfUserConfirmationService` (que abre o modal) e `HeadlessUserConfirmationService` (que responde via CLI ou auto-aprova em ambiente de testes).

---

#### [ARC-15] Vazamento de Recursos Nativos de Áudio em `GibberishVoiceService`
- **Arquivo / Linhas**: `AIBWindows/Services/GibberishVoiceService.cs:9, 22-29`
- **Severidade**: **Média**
- **Descrição Técnica**:
  `GibberishVoiceService` aloca a instância estática `WaveOutEvent` da biblioteca NAudio em `Initialize()`, mantendo o driver de áudio aberto. O serviço não implementa descarte (`Dispose`), e a aplicação não libera a instância ao encerrar no `App.OnExit`.
- **Resolução Técnica Recomendada**:
  Transformar `GibberishVoiceService` em um serviço de instância `IAudioPlaybackService` implementando `IDisposable` e gerenciado pelo container de DI.

---

## Estrutura Recomendada para o Projeto (Target Architecture)

```
AIB/
├── src/
│   ├── AIB.Core/                      # Domínio e Abstrações Neutras (sem dependência de WPF)
│   │   ├── Models/                    # Entidades de Domínio (ChatSession, AgentProfile, UserSettings)
│   │   ├── Interfaces/                # Contratos (IAiCompletionService, ITool, ICredentialVault, IMemoryService)
│   │   └── Services/                  # Lógica de Negócio (LevelEngine, ReActCoordinator, PromptBuilder)
│   │
│   ├── AIB.Infrastructure/            # Implementações de Infraestrutura e I/O
│   │   ├── AI/                        # OpenAiClientAdapter, OllamaClientAdapter, TiktokenService
│   │   ├── Storage/                   # DpapiCredentialVault, LiteDbMemoryService, JsonSettingsService
│   │   ├── Audio/                     # WhisperNetVoiceService, NAudioSoundService
│   │   ├── Vision/                    # WindowsMediaOcrService
│   │   └── Tools/                     # RunCommandTool, ReadFileTool, WriteFileTool
│   │
│   └── AIB.App/                       # Camada de Apresentação WPF (.NET 8 WPF)
│       ├── Converters/                # ValueToStarForegroundConverter, TokenRatioToBrushConverter
│       ├── ViewModels/                # ChatViewModel, SettingsViewModel, FirstRunViewModel
│       ├── Views/                     # ChatWindow, SettingsWindow, FirstRunWindow
│       ├── Services/                  # WpfConfirmationService, WpfNotificationService
│       └── App.xaml.cs                # Generic Host & Dependency Injection Setup
│
└── tests/
    └── AIB.Tests/                     # Testes Unitários e de Integração xUnit
        ├── Core/                      # Testes de LevelEngine, PromptBuilder
        ├── Infrastructure/            # Testes com Mocks de HttpMessageHandler e ITool
        └── ViewModels/                # Testes de ChatViewModel, SettingsViewModel
```

---

## Conclusão da Auditoria de Arquitetura

O projeto AIB apresenta excelente ambição funcional (assistente local com ferramentas ReAct, cofre seguro, gamificação e suporte a múltiplos LLMs), porém sua base de código atual sofre de dívidas arquiteturais críticas:
1. **Acoplamento Extremo e Ausência de DI**: Dificulta a evolução sustentável e a testabilidade automatizada.
2. **Ausência de Camada ViewModel (MVVM)**: Concentra 100% da lógica nas Views (especialmente `ChatWindow.xaml.cs`), elevando a complexidade ciclomática e o risco de regressões.
3. **Serviços Ocos**: Recursos essenciais anunciados na interface do usuário (OCR, Voz, Memória Persistente) dependem de stubs que precisam ser substituídos por integrações reais das bibliotecas já referenciadas no `.csproj`.

A adoção das refatorações propostas neste relatório garantirá que o AIB atinja padrões corporativos de manutenibilidade, testabilidade, desacoplamento e robustez.
