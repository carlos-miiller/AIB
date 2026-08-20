# Relatório de Análise Estática e Auditoria Arquitetural Profunda — Projeto AIB

**Projeto**: AIB (Assistente Inteligente Baseado em IA)  
**Versão do Framework**: .NET 8.0 (Windows Desktop — WPF / C# 12)  
**Data da Auditoria**: 19 de Agosto de 2026  
**Status**: Concluído / Autorizado para Engenharia  

---

## Sumário Executivo

O presente documento consolida a auditoria estática e arquitetural de ponta a ponta realizada sobre a base de código do **AIB (Assistente Inteligente Baseado em IA)**, englobando a aplicação desktop (`AIBWindows`) e o ecossistema de testes automatizados (`AIB.Tests`).

O projeto AIB demonstra uma visão de produto sofisticada e ambiciosa: assistente desktop autônomo com suporte a múltiplos provedores de LLM (OpenAI e Ollama local), execução de ferramentas de sistema via loop ReAct com dual-mode tool calling (nativo e fallback por expressões regulares), interface estilizada com suporte a Markdown, captura de tela com visão computacional, síntese/reconhecimento de voz e um inovador sistema de gamificação e progressão por níveis que expande dinamicamente as capacidades de contexto e ferramentas disponíveis.

Contudo, a auditoria identificou **graves débitos técnicos estruturais**, **vulnerabilidades críticas de concorrência e segurança**, **riscos iminentes de deadlock**, **vazamentos de recursos nativos/memória** e uma **suíte de testes deficitária** que ameaçam a estabilidade, escalabilidade e segurança da aplicação.

### Principais Diagnósticos Globais:
1. **Acoplamento Extremo e Ausência de Container de Injeção de Dependências (IoC/DI)**: A aplicação não utiliza `Microsoft.Extensions.DependencyInjection` ou padrão Generic Host. Quase todos os serviços são expostos como classes estáticas mutáveis ou instanciados diretamente via operador `new`, inviabilizando testes unitários desacoplados e introduzindo estado global compartilhado.
2. **Violação Integral do Padrão MVVM (Monólitos / God Views)**: Não existe nenhuma camada de *ViewModel* no projeto. A classe `ChatWindow.xaml.cs` acumula 1.137 linhas, gerindo desde requisições HTTP e streaming de IA até manipulação procedural de nós visuais e animações da UI na thread principal.
3. **Deadlocks Críticos em I/O e Sincronização**: Identificou-se deadlock estrutural no redirecionamento de pipes do Windows em subprocessos (`RunCommandTool.cs`), concorrência destrutiva na manipulação de coleções de histórico durante tarefas de background (`OpenAIService.cs`), e deadlocks potenciais na thread de UI através de `Dispatcher.Invoke` síncrono.
4. **Vulnerabilidades de Segurança em Ferramentas do Sistema**: A ferramenta `RunCommandTool` permite injeção de comandos PowerShell sem validação estrita e executa comandos de alto privilégio sem solicitar confirmação ao usuário, violando a política de autorização prevista na arquitetura. As ferramentas `ReadFileTool` e `WriteFileTool` não possuem sanitização contra *Path Traversal*.
5. **Proliferação de Serviços Stub (Cascas Ocas)**: Foram catalogados 10 serviços que são stubs não funcionais (retornam tarefas vazias ou coleções nulas), enquanto dependências NuGet robustas (`LiteDB`, `SmartComponents.LocalEmbeddings`, `Whisper.net`, `PdfPig`) já estão declaradas no `.csproj` mas não integradas.
6. **Vazamento de Isolamento de Credenciais**: O cofre DPAPI implementa busca global não restrita, podendo enviar chaves de API de terceiros para endpoints incorretos, além de utilizar convenção insegura de retorno de erro por string (`"ERRO: ..."`).

---

## Matriz Geral de Riscos e Severidades

A tabela abaixo sumariza todas as vulnerabilidades e inconformidades detectadas, classificadas de acordo com o impacto operacional, risco de segurança e estabilidade da aplicação:

| ID | Categoria | Componente / Arquivo | Linhas | Severidade | Impacto Resumido |
|---|---|---|---|---|---|
| **ARC-01** | Arquitetura | `App.xaml.cs` | 18, 45, 65 | **Crítica** | Ausência de container IoC/DI; instanciação ad-hoc e estado estático mutável. |
| **ARC-02** | Arquitetura / MVVM | `Views/ChatWindow.xaml.cs` | 1-1137 | **Crítica** | God Class monolítica na View; ausência de ViewModels; lógica de negócio acoplada à UI. |
| **ARC-03** | Arquitetura / Design | `Services/*Service.cs` (10 arquivos) | Várias | **Crítica** | Coleção de 10 serviços stub vazios sem integração com pacotes NuGet existentes. |
| **ARC-04** | Arquitetura / Design | `Services/OpenAIService.cs` | 1-993 | **Crítica** | Monólito de IA com múltiplas responsabilidades e instanciações ad-hoc. |
| **ASYNC-01** | Concorrência / I/O | `Services/Tools/RunCommandTool.cs` | 57-78 | **Crítica** | Deadlock fatal nos pipes de `stdout`/`stderr` do Windows e vazamento de processos órfãos. |
| **ASYNC-02** | Concorrência / Threads | `Services/OpenAIService.cs` | 17, 66, 113, 151, 218 | **Crítica** | Race condition e corrupção de coleção `_history` (`List<T>`) entre Warmup e Chat. |
| **SEC-01** | Segurança / Shell | `Services/Tools/RunCommandTool.cs` | 32-58 | **Crítica** | Injeção de comandos PowerShell e bypass do modal de autorização do usuário. |
| **ARC-05** | Arquitetura / Ciclo de Vida | `App.xaml.cs` | 38-43, 157-182 | **Alta** | Execução CLI síncrona acoplada dentro do ciclo de vida WPF com supressão de contexto. |
| **ARC-06** | Arquitetura / UI | `Views/ChatWindow.xaml.cs` | 235-389, 535-547 | **Alta** | Geração procedural de componentes de UI em C# inviabilizando virtualização e templates. |
| **ARC-07** | Arquitetura / Acoplamento | `Views/SettingsWindow.xaml.cs` | 214-218 | **Alta** | Acoplamento direto entre janelas via busca global de instâncias na árvore WPF. |
| **ARC-08** | Arquitetura / SoC | `Views/FirstRunWindow.xaml.cs` | 18-32, 59-93, 325-393 | **Alta** | Wizard procedimental sem ViewModel e conversores embutidos no code-behind. |
| **ARC-09** | Arquitetura / Domínio | `Services/ITool.cs` | 23 | **Alta** | Vazamento do tipo concreto `OpenAI.Chat.ChatTool` para a abstração de domínio. |
| **ARC-10** | Arquitetura / SRP | `Services/SettingsService.cs` | 104-134 | **Alta** | Violação de SRP: I/O de configurações misturado com chamadas de rede HTTP ao Ollama. |
| **ARC-11** | Arquitetura / Segurança | `Services/CredentialService.cs` | 49, 79, 83, 92 | **Alta** | Convenção de erro baseada em strings (`"ERRO: ..."`) inspecionada via `.StartsWith()`. |
| **ASYNC-03** | Concorrência / Lifecycle | `Services/OpenAIService.cs` | 20, 25-28, 210-212 | **Alta** | Race condition e `ObjectDisposedException` ao redefinir `_generationCts` sem sincronização. |
| **ASYNC-04** | Concorrência / Cancelamento | `Services/OpenAIService.cs` | 127, 141, 622-626, 713 | **Alta** | Ignorância de `CancellationToken` na execução paralela de ferramentas (`Task.WhenAll`). |
| **ASYNC-05** | Concorrência / Sockets | `Services/OllamaNativeClient.cs` / `OpenAIService.cs` | 37-43 / 82, 126, 241 | **Alta** | Esgotamento de sockets TCP (`TIME_WAIT` leak) por criação contínua de `HttpClient`. |
| **ASYNC-07** | Concorrência / UI | `Views/ChatWindow.xaml.cs` / `ShadowWidget.xaml.cs` | 934, 1117 / 26, 70 | **Alta** | Deadlock da threadpool ao acionar `Dispatcher.Invoke` síncrono com a UI ocupada. |
| **ASYNC-08** | Concorrência / Reentrância | `Views/ChatWindow.xaml.cs` | 75-88, 450-627, 912-921 | **Alta** | Reentrância em `async void SendButton_Click` e mutação concorrente de histórico ativo. |
| **ASYNC-10** | Concorrência / File I/O | `Services/ChatHistoryService.cs` / `SettingsService.cs` | 22-99 / 63-102 | **Alta** | Conflito de I/O em arquivos sem lock (`IOException`) resultando em perda total de histórico. |
| **SEC-03** | Segurança / File System | `Services/Tools/ReadFileTool.cs` / `WriteFileTool.cs` | 44-59 / 52-60 | **Alta** | Ausência de sanitização contra Path Traversal e leitura de arquivos protegidos do OS. |
| **SEC-04** | Segurança / Credenciais | `Services/CredentialService.cs` | 26, 58, 64-77 | **Alta** | Quebra de isolamento de credenciais (Global Search Fallback) e chave em texto claro. |
| **RES-01** | Resiliência / Exceções | Múltiplos arquivos (`ChatHistoryService`, `OpenAIService`) | Várias | **Alta** | Supressão silenciosa de exceções em blocos `catch { }` vazios em rotinas críticas. |
| **RES-02** | Resiliência / Rede | `Services/OpenAIService.cs` / `OllamaNativeClient.cs` | 728, 789 / 41 | **Alta** | Configuração de `InfiniteTimeSpan` e ausência de políticas de Retry e Circuit Breaker. |
| **RES-04** | Resiliência / Crash | `App.xaml.cs` | 29-110 | **Alta** | Ausência de manipuladores globais para exceções não tratadas e unobserved tasks. |
| **TST-03** | Qualidade / Testes | `AIB.Tests` | Toda a suíte | **Alta** | Lacuna crítica de cobertura de testes nos componentes centrais de IA, Tools e Cofre. |
| **ARC-12** | Arquitetura / Estado | `Services/DirectoryService.cs` | 8-9, 50-51, 83-92 | **Média** | Estado estático mutável e resolução frágil de caminhos relativos de diretórios. |
| **ARC-13** | Arquitetura / OCP | `Services/ToolRegistry.cs` | 64-79 | **Média** | Registro estático e instanciação rígida de ferramentas (violação de OCP/DIP). |
| **ARC-14** | Arquitetura / UI | `Views/CommandConfirmationWindow.xaml.cs` | 63-81 | **Média** | Invocação modal estática da UI acoplada no backend impedindo automação headless. |
| **ARC-15** | Arquitetura / Recursos | `Services/GibberishVoiceService.cs` | 9, 22-29 | **Média** | Instância estática de `WaveOutEvent` não liberada no encerramento da aplicação. |
| **ASYNC-06** | Concorrência / Memória | `Services/OllamaNativeClient.cs` | 201, 235 | **Média** | Vazamento de memória nativa por ausência de `using` em instâncias de `JsonDocument`. |
| **ASYNC-09** | Concorrência / Crash | `Views/SettingsWindow.xaml.cs` | 23, 167-185, 189 | **Média** | Crash fatal da aplicação por exceção não tratada em lambda `async void` no Dispatcher. |
| **ASYNC-11** | Concorrência / Áudio | `Services/GibberishVoiceService.cs` | 10-61 | **Média** | Manipulação sem sincronização do mixer de áudio concorrente com a thread de DAC. |
| **ASYNC-12** | Concorrência / Bloqueio | `App.xaml.cs` | 40-42 | **Média** | Anti-pattern sync-over-async (`.GetAwaiter().GetResult()`) na inicialização CLI. |
| **ASYNC-13** | Concorrência / Memória | `Views/ChatWindow.xaml.cs` | 44-49, 996-1003 | **Média** | Não desinscrição de eventos em serviços de longa vida ao fechar janelas WPF. |
| **UI-02** | WPF / Performance | `Views/ContextSidebar.xaml.cs` / `ShadowWidget.xaml.cs` | 29-35 / 17-22 | **Média** | Timers contínuos sem descarte gerando retenção de instâncias e gasto contínuo de CPU. |
| **UI-05** | WPF / P-Invoke | `Views/SettingsWindow.xaml.cs` | 281-298 | **Média** | Risco de vazamento de memória não gerenciada (`Marshal.AllocHGlobal`) no efeito Blur. |
| **RES-03** | Resiliência / Config | `Services/OpenAIService.cs` | 761-795 | **Média** | Alterações em chave de API ou URL ignoradas sem alteração prévia no nome do modelo. |
| **TST-02** | Qualidade / Testes | `AIB.Tests/OllamaNativeClientTests.cs` | 38 | **Média** | Chamada bloqueante sync-over-async no setup de mock gerando warning `xUnit1031`. |
| **UI-04** | WPF / Virtualização | `Views/FirstRunWindow.xaml` | 112-118, 145-149 | **Baixa** | `ScrollViewer.CanContentScroll="False"` desativando virtualização de UI do painel. |
| **TST-01** | Qualidade / Testes | `AIB.Tests/UnitTest1.cs` | 1-10 | **Baixa** | Teste vazio residual gerado por template poluindo a suíte de testes. |

---

## Seção 1: Arquitetura, Injeção de Dependências e Padrões de Projeto (MVVM / Clean Code)

### [ARC-01] Ausência de Container de Injeção de Dependências (IoC) e Instanciação Ad-Hoc
- **Arquivo / Linhas**: `AIBWindows/App.xaml.cs:18, 45, 65`, disseminado em todo o projeto.
- **Severidade**: **Crítica**
- **Diagnóstico Técnico**:
  O projeto `AIBWindows` instancia serviços concretos diretamente em variáveis de instância privadas (`private readonly SettingsService _settingsService = new();`) e aciona métodos de negócio via classes e membros estáticos (`DirectoryService`, `CredentialService`, `AuditLogService`, `ChatHistoryService`, `LevelService`, `GibberishVoiceService`).
  Essa ausência de abstração e inversão de controle:
  1. Quebra o *Dependency Inversion Principle* (DIP) do SOLID;
  2. Impede a substituição de dependências de I/O (rede, disco, DPAPI) por dublês de teste (*Mocks* / *Stubs*) no `AIB.Tests`;
  3. Acopla o ciclo de vida de objetos ao ciclo de vida da thread de execução do WPF.
- **Resolução Técnica Recomendada**:
  Adotar o padrão Generic Host (`Microsoft.Extensions.Hosting` e `Microsoft.Extensions.DependencyInjection`). Registrar contratos de serviço com seus respectivos ciclos de vida (`Singleton`, `Transient`, `Scoped`) e injetar dependências estritamente via construtores.

```csharp
// Refatoração: App.xaml.cs configurando Generic Host e DI
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services => ((App)Current)._host!.Services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices((context, services) =>
            {
                // Infraestrutura e Configurações
                services.AddSingleton<IAppDirectories, AppDirectories>();
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<ICredentialVault, DpapiCredentialVault>();
                services.AddSingleton<IAuditLogService, FileAuditLogService>();
                services.AddHttpClient();

                // Provedores de IA e Registro de Ferramentas
                services.AddSingleton<IToolRegistry, ToolRegistry>();
                services.AddTransient<ITool, RunCommandTool>();
                services.AddTransient<ITool, ReadFileTool>();
                services.AddTransient<ITool, WriteFileTool>();
                services.AddSingleton<IAiCompletionService, OpenAiCompletionService>();

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
        if (_host != null)
        {
            using (_host)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
        }
        base.OnExit(e);
    }
}
```

---

### [ARC-02] Monolith / God Class em `ChatWindow.xaml.cs` e Violação Completa de MVVM
- **Arquivo / Linhas**: `AIBWindows/Views/ChatWindow.xaml.cs:1-1137`
- **Severidade**: **Crítica**
- **Diagnóstico Técnico**:
  A classe `ChatWindow.xaml.cs` atua como um monólito clássico (*God Class*), contendo 1.137 linhas que misturam:
  - Inicialização direta de serviços de backend (`OpenAIService`, `ShadowAssistantService`, `VoiceService`);
  - Regras de negócio de gamificação, cálculo de XP e interpolação de gradientes de cores (`RefreshLevelUI`, linhas 176-219);
  - Geração imperativa de elementos da árvore visual WPF (`Border`, `StackPanel`, `MarkdownViewer`, linhas 235-389);
  - Orquestração de tarefas de streaming assíncrono e tool calling (`SendButton_Click`, linhas 450-627);
  - Integração com P/Invoke do Windows Forms para detecção de múltiplos monitores (`EnsureShadowWidgetsForAllScreens`, linhas 1067-1135).
  Essa concentração impede testes automatizados de comportamento e satura a UI thread com processamento intensivo.
- **Resolução Técnica Recomendada**:
  Extrair todo o estado de aplicação, comandos e orquestração de mensageria para `ChatViewModel` utilizando o pacote `CommunityToolkit.Mvvm`.

```csharp
// Refatoração: ChatViewModel.cs desacoplado da UI
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

public partial class ChatViewModel : ObservableObject
{
    private readonly IAiCompletionService _aiService;
    private readonly ISettingsService _settingsService;
    private readonly ILevelService _levelService;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private int _currentLevel = 1;

    [ObservableProperty]
    private double _levelProgressRatio;

    [ObservableProperty]
    private string _tokenStatusText = "0/3072 tokens";

    public ObservableCollection<ChatMessageItemViewModel> Messages { get; } = new();

    public IAsyncRelayCommand SendMessageCommand { get; }
    public IRelayCommand CancelGenerationCommand { get; }

    public ChatViewModel(IAiCompletionService aiService, ISettingsService settingsService, ILevelService levelService)
    {
        _aiService = aiService;
        _settingsService = settingsService;
        _levelService = levelService;

        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, () => !string.IsNullOrWhiteSpace(InputText) && !IsGenerating);
        CancelGenerationCommand = new RelayCommand(() => _aiService.CancelGeneration(), () => IsGenerating);
    }

    private async Task SendMessageAsync()
    {
        var userText = InputText.Trim();
        InputText = string.Empty;
        IsGenerating = true;

        Messages.Add(new ChatMessageItemViewModel(MessageAuthor.User, userText));
        var assistantMsg = new ChatMessageItemViewModel(MessageAuthor.Assistant, string.Empty);
        Messages.Add(assistantMsg);

        try
        {
            await foreach (var chunk in _aiService.StreamResponseAsync(userText))
            {
                assistantMsg.AppendContent(chunk);
            }
        }
        finally
        {
            IsGenerating = false;
        }
    }
}
```

---

### [ARC-03] Proliferação de Serviços Stub / Vazios (Cascas Ocas)
- **Arquivos / Linhas**:
  - `AIBWindows/Services/AuditLogService.cs:4` — `AppendAsync(object logData) => Task.CompletedTask;`
  - `AIBWindows/Services/MemoryService.cs:4-5` — `DeleteMemory(int id) {}`, `GetRecentMemories => new List<object>();`
  - `AIBWindows/Services/OcrService.cs:4` — `ExtractTextFromActiveScreenAsync => Task.FromResult("");`
  - `AIBWindows/Services/ShadowAssistantService.cs:3-12` — Métodos vazios, eventos nunca disparados.
  - `AIBWindows/Services/ShadowHistoryService.cs:4` — `Suggestions => new List<object>();`
  - `AIBWindows/Services/SkillService.cs:8-11` — `GetSkillCount => 0`, `ListLocalSkills => new List<LocalSkill>();`
  - `AIBWindows/Services/VoiceService.cs:8-15` — Métodos vazios, `Dispose()` não implementa `IDisposable`.
  - `AIBWindows/Services/ReminderService.cs:9-14` — `ActiveReminders => new List<Reminder>();` (aloca lista vazia contínua).
  - `AIBWindows/Services/ContextService.cs:6-15` — Retorna listas vazias e descarta entradas.
  - `AIBWindows/Services/TestRunner.cs:3-7` — Retorna `Task.FromResult(true)` incondicionalmente.
- **Severidade**: **Crítica**
- **Diagnóstico Técnico**:
  Dez serviços centrais do ecossistema são stubs vazios que não executam processamento real. Botões de visão computacional OCR (`ChatWindow:642`), gravação de voz Whisper (`ChatWindow:677`), barra de contexto lateral e comandos CLI dependem desses stubs. Notavelmente, o `AIB.csproj` já referencia `LiteDB`, `SmartComponents.LocalEmbeddings`, `Whisper.net` e `PdfPig`, mas os serviços não as utilizam.
- **Resolução Técnica Recomendada**:
  Extrair interfaces para todos os serviços e implementar as integrações reais utilizando as bibliotecas referenciadas.

```csharp
// Exemplo: Implementação Real do IMemoryService utilizando LiteDB
using LiteDB;

public interface IMemoryService
{
    Task StoreMemoryAsync(string content, string category, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRecord>> GetRecentMemoriesAsync(int limit = 10, CancellationToken ct = default);
    Task DeleteMemoryAsync(int id, CancellationToken ct = default);
}

public class LiteDbMemoryService : IMemoryService, IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<MemoryRecord> _collection;

    public LiteDbMemoryService(IAppDirectories directories)
    {
        var dbPath = Path.Combine(directories.DataDir, "memory.litedb");
        _db = new LiteDatabase($"Filename={dbPath};Connection=Shared");
        _collection = _db.GetCollection<MemoryRecord>("memories");
        _collection.EnsureIndex(x => x.Timestamp);
    }

    public Task StoreMemoryAsync(string content, string category, CancellationToken ct = default)
    {
        var record = new MemoryRecord
        {
            Content = content,
            Category = category,
            Timestamp = DateTime.UtcNow
        };
        _collection.Insert(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryRecord>> GetRecentMemoriesAsync(int limit = 10, CancellationToken ct = default)
    {
        var items = _collection.Query()
            .OrderByDescending(x => x.Timestamp)
            .Limit(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<MemoryRecord>>(items);
    }

    public Task DeleteMemoryAsync(int id, CancellationToken ct = default)
    {
        _collection.Delete(id);
        return Task.CompletedTask;
    }

    public void Dispose() => _db.Dispose();
}

public record MemoryRecord
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
```

---

### [ARC-04] Monolithic God Class em `OpenAIService.cs` e Acoplamento Concreto
- **Arquivo / Linhas**: `AIBWindows/Services/OpenAIService.cs:1-993`
- **Severidade**: **Crítica**
- **Diagnóstico Técnico**:
  `OpenAIService` acumula quase 1.000 linhas de código com responsabilidades concorrentes:
  1. Instanciação e cacheamento de clientes OpenAI e Ollama;
  2. Execução de warmup de VRAM com requisições HTTP manuais brutas;
  3. Montagem procedural de system prompt lendo variáveis de ambiente e diretórios do usuário;
  4. Orquestração do loop ReAct com parsing de chamadas de ferramentas nativas e via regex;
  5. Manutenção de histórico compartilhado não sincronizado (`_history`);
  6. Cálculo de tokens via Tiktoken (`Microsoft.ML.Tokenizers`).
- **Resolução Técnica Recomendada**:
  Decompor `OpenAIService` em adaptadores especializados: `IAiCompletionService`, `IReActCoordinator`, `ISystemPromptFactory` e `ITokenizerService`.

---

### [ARC-05] Execução CLI Síncrona Acoplada no Ciclo de Vida da Aplicação WPF
- **Arquivo / Linhas**: `AIBWindows/App.xaml.cs:38-43, 157-182`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  Em `App.OnStartup`, caso existam argumentos CLI (`--test-rag`, `--test-tool`), o código anula o contexto de sincronização com `System.Threading.SynchronizationContext.SetSynchronizationContext(null);` e bloqueia a thread de UI chamando `RunCliCommandAsync(e.Args).GetAwaiter().GetResult();`, finalizando o processo com `Environment.Exit()`. Isso corrompe o ciclo de vida natural do WPF e impede testes e encerramento limpo de recursos gerenciados.
- **Resolução Técnica Recomendada**:
  Separar o ponto de entrada da aplicação (`Program.cs`), direcionando a execução para um runner de CLI headless antes da inicialização do subsistema WPF.

```csharp
// Refatoração: Program.cs isolando CLI de UI
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--test"))
        {
            var host = CreateCliHost(args);
            var cliRunner = host.Services.GetRequiredService<ICliTestRunner>();
            return cliRunner.RunAsync(args).GetAwaiter().GetResult();
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
```

---

### [ARC-06] Geração Procedural de UI e Manipulação de Árvore Visual em C#
- **Arquivo / Linhas**: `AIBWindows/Views/ChatWindow.xaml.cs:235-389, 535-547`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  Componentes de mensagens de usuário e assistente são instanciados proceduralmente em C# (`new Border()`, `new MarkdownViewer()`, `MessagesPanel.Children.Add(border)`).
  Isso desabilita o mecanismo de virtualização de UI nativo do WPF (`VirtualizingStackPanel`), causa alto consumo de memória gráfica ao manter milhares de elementos visuais vivos e impede o uso de estilização declarativa em XAML.
- **Resolução Técnica Recomendada**:
  Utilizar um `ItemsControl` (ou `ListBox`) virtualizado associado a uma coleção observável no ViewModel, controlando a apresentação via `DataTemplateSelector`.

```xml
<!-- Refatoração: ChatWindow.xaml com Templates Declarativos -->
<ItemsControl ItemsSource="{Binding Messages}" VirtualizingPanel.IsVirtualizing="True">
    <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate>
            <VirtualizingStackPanel IsVirtualizing="True" VirtualizationMode="Recycling" />
        </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplateSelector>
        <local:ChatMessageTemplateSelector
            UserTemplate="{StaticResource UserMessageTemplate}"
            AssistantTemplate="{StaticResource AssistantMessageTemplate}" />
    </ItemsControl.ItemTemplateSelector>
</ItemsControl>
```

---

### [ARC-07] Acoplamento Rígido Direto Entre Janelas (Cross-View Querying)
- **Arquivo / Linhas**: `AIBWindows/Views/SettingsWindow.xaml.cs:214-218`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  Ao salvar preferências no `SettingsWindow.Save_Click`, o código busca a instância ativa da janela de chat inspecionando a lista global do WPF:
  ```csharp
  var chatWindow = System.Windows.Application.Current.Windows.OfType<ChatWindow>().FirstOrDefault();
  chatWindow?.ApplyCharacterUI();
  ```
  Essa prática viola o encapsulamento e cria dependência circular entre Views. Se a janela de chat estiver fechada ou minimizada na bandeja, a notificação falha silenciosamente.
- **Resolução Técnica Recomendada**:
  Implementar mensageria desacoplada via `IMessenger` (`CommunityToolkit.Mvvm`).

```csharp
// Mensagem de Domínio
public record CharacterAppearanceChangedMessage(string CharacterId, string AvatarPath);

// No SettingsViewModel:
_messenger.Send(new CharacterAppearanceChangedMessage(SelectedId, AvatarPath));

// No ChatViewModel:
_messenger.Register<CharacterAppearanceChangedMessage>(this, (r, m) =>
{
    LoadCharacterTheme(m.CharacterId);
});
```

---

### [ARC-08] FirstRunWindow: Lógica de Negócio e Conversores Acoplados ao Code-Behind
- **Arquivo / Linhas**: `AIBWindows/Views/FirstRunWindow.xaml.cs:18-32, 59-93, 325-393`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  O arquivo code-behind declara conversores internos (`ValueToStarForegroundConverter`), realiza leitura síncrona de arquivos JSON de perfil do disco na thread de UI (`LoadAgents`) e executa chamadas assíncronas do cofre em manipuladores `async void` sem proteção contra crash.
- **Resolução Técnica Recomendada**:
  Mover conversores para a camada `Converters`, migrar a leitura de arquivos para um serviço de repositório e vincular o formulário ao `FirstRunViewModel`.

---

### [ARC-09] Vazamento do SDK da OpenAI para a Interface de Domínio `ITool`
- **Arquivo / Linhas**: `AIBWindows/Services/ITool.cs:23`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  A interface `ITool` expõe a propriedade `ChatTool ChatToolDefinition { get; }`, vinculando o contrato central de ferramentas do assistente ao pacote NuGet proprietário `OpenAI.Chat`. Isso impede que o sistema opere de maneira agnóstica com outros provedores (ex: Anthropic Claude, Google Gemini nativo ou modelos locais via Ollama puro).
- **Resolução Técnica Recomendada**:
  Criar um esquema neutro de domínio `ToolMetadata` / `ToolDefinition` e realizar o mapeamento para `OpenAI.Chat.ChatTool` exclusivamente na camada de infraestrutura do provedor OpenAI.

```csharp
// Contrato de Domínio Neutro para Ferramentas
public record ToolParameterDefinition(string Name, string Type, string Description, bool Required);

public record ToolMetadata(string Name, string Description, IReadOnlyList<ToolParameterDefinition> Parameters);

public interface ITool
{
    string Name { get; }
    string Description { get; }
    int RequiredLevel { get; }
    ToolMetadata Metadata { get; }
    Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1, CancellationToken ct = default);
}
```

---

### [ARC-10] Violação de SRP no `SettingsService`: I/O de Configuração com Chamadas HTTP
- **Arquivo / Linhas**: `AIBWindows/Services/SettingsService.cs:104-134`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  `SettingsService` inclui o método `GetOllamaModelsAsync(string baseUrl)`, que utiliza um `HttpClient` para consultar a API `/api/tags` do Ollama. A recuperação de configurações locais do usuário e a comunicação de rede para descoberta de modelos de IA pertencem a camadas arquiteturais distintas.
- **Resolução Técnica Recomendada**:
  Mover a descoberta de modelos para `IOllamaModelDiscoveryService` ou `IOllamaClient`.

---

### [ARC-11] Convenção Frágil de Retorno de Erros por String no `CredentialService`
- **Arquivo / Linhas**: `AIBWindows/Services/CredentialService.cs:49, 79, 83, 92`, `App.xaml.cs:133`, `FirstRunWindow.xaml.cs:360`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  Em vez de retornar valores anuláveis (`string?`) ou lançar exceções tipadas de segurança, `CredentialService` retorna mensagens de erro em texto puro iniciadas por `"ERRO: ..."`. Os chamadores realizam verificações como `if (key.StartsWith("ERRO"))`. Caso uma credencial válida inicie por coincidência com esses caracteres ou caso ocorra um erro de decriptação, o fluxo de execução propaga falhas silenciosas.
- **Resolução Técnica Recomendada**:
  Adotar retorno anulável e exceções específicas de infraestrutura de segurança.

```csharp
public interface ICredentialVault
{
    Task StoreCredentialAsync(string system, string key, string secret, CancellationToken ct = default);
    Task<string?> RetrieveCredentialAsync(string system, string key, CancellationToken ct = default);
    Task<bool> HasCredentialAsync(string system, string key, CancellationToken ct = default);
    Task WipeAllCredentialsAsync(CancellationToken ct = default);
}
```

---

### [ARC-12] Estado Estático Mutável e Resolução Relativa de Diretórios em `DirectoryService`
- **Arquivo / Linhas**: `AIBWindows/Services/DirectoryService.cs:8-9, 50-51, 83-92`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  `DirectoryService` expõe campos estáticos mutáveis (`_dataDir`, `_tempDir`) redefinidos em tempo de execução sem travas de thread. Além disso, as linhas 50-51 tentam localizar assets através de caminhos relativos ao diretório de desenvolvimento (`..\\..\\..\\character`), que quebram imediatamente em ambientes de produção empacotados.
- **Resolução Técnica Recomendada**:
  Utilizar `AppContext.BaseDirectory` e injetar a interface `IAppDirectories`.

---

### [ARC-13] Instanciação Rígida de Ferramentas no `ToolRegistry` (Violação OCP/DIP)
- **Arquivo / Linhas**: `AIBWindows/Services/ToolRegistry.cs:64-79`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  `ToolRegistry.RegisterNativeTools()` instancia manualmente `new RunCommandTool()`, `new ReadFileTool()`, `new WriteFileTool()`. Não é possível estender o conjunto de ferramentas por injeção de dependência ou registrar ferramentas mockadas em testes unitários.
- **Resolução Técnica Recomendada**:
  Injetar `IEnumerable<ITool>` via construtor no `ToolRegistry`.

---

### [ARC-14] Invocação de Diálogo Modal da UI Acoplada a Ferramentas de Backend
- **Arquivo / Linhas**: `AIBWindows/Views/CommandConfirmationWindow.xaml.cs:63-81`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  `CommandConfirmationWindow.ShowAsync` invoca diretamente o `Application.Current.Dispatcher`, impedindo que ferramentas de automação rodem em cenários de linha de comando ou pipelines de CI/CD.
- **Resolução Técnica Recomendada**:
  Introduzir a interface `IUserConfirmationService` com implementações `WpfConfirmationService` e `HeadlessConfirmationService`.

---

### [ARC-15] Vazamento de Recursos Nativos de Áudio em `GibberishVoiceService`
- **Arquivo / Linhas**: `AIBWindows/Services/GibberishVoiceService.cs:9, 22-29`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  O serviço aloca a instância estática `WaveOutEvent` da biblioteca NAudio em `Initialize()`, mantendo o driver de áudio do sistema operacional permanentemente ocupado. O serviço não implementa `IDisposable` e não libera os recursos no encerramento da aplicação.
- **Resolução Técnica Recomendada**:
  Transformar em serviço de instância `IAudioPlaybackService` gerenciado pelo container de DI com descarte automático de recursos.

---

## Seção 2: Assincronismo, Concorrência, Streaming de IA e Deadlocks

### [ASYNC-01] Deadlock Fatal em I/O de Subprocessos e Vazamento de Processos Órfãos
- **Arquivo / Linhas**: `AIBWindows/Services/Tools/RunCommandTool.cs:57-78`
- **Severidade**: **Crítica**
- **Diagnóstico Técnico**:
  No método `ExecuteAsync`, o processo PowerShell é executado e o código tenta ler as saídas de forma sequencial e bloqueante:
  ```csharp
  var processTask = Task.Run(() =>
  {
      string output = process.StandardOutput.ReadToEnd();
      string error = process.StandardError.ReadToEnd();
      process.WaitForExit();
      return (output, error);
  });
  ```
  **Mecanismo de Falha**: Os buffers de pipe de redirecionamento do Windows possuem capacidade limitada (entre 4 KB e 64 KB). Se o script PowerShell emitir mensagens para o stream de erro (`StandardError`) que excedam o tamanho do buffer antes de encerrar a saída padrão (`StandardOutput`), o processo filho é suspenso pelo kernel do Windows aguardando que o buffer de erro seja esvaziado. A thread C#, por sua vez, está bloqueada em `StandardOutput.ReadToEnd()`. Ambas as pontas entram em **Deadlock Permanente**.
  O comando trava até o timeout de 30 segundos, quando `process.Kill()` é acionado. Por não utilizar `entireProcessTree: true`, processos filhos disparados pelo PowerShell permanecem ativos como processos órfãos consumindo CPU e memória.
- **Resolução Técnica Recomendada**:
  Executar leituras assíncronas concorrentes com `Task.WhenAll`, aguardar o término com `process.WaitForExitAsync(ct)` e passar `entireProcessTree: true` na finalização forçada.

```csharp
// Solução Robusta para RunCommandTool.cs
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            var exitTask = process.WaitForExitAsync(cts.Token);

            await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            string combined = (stdout + "\n" + stderr).Trim();

            if (string.IsNullOrWhiteSpace(combined))
                return "Comando executado com sucesso (sem saída).";

            return combined.Length > 8000 ? combined[..8000] + "\n...[Saída truncada pelo limite de segurança]" : combined;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return "ERRO: O comando excedeu o tempo limite de 30 segundos e foi interrompido (Timeout).";
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
    catch (Exception ex)
    {
        return $"ERRO fatal ao executar processo: {ex.Message}";
    }
}
```

---

### [ASYNC-02] Race Condition e Corrupção na Coleção Compartilhada `_history`
- **Arquivo / Linhas**: `AIBWindows/Services/OpenAIService.cs:17, 66, 107-113, 151-152, 218, 610, 631, 934-950`
- **Severidade**: **Crítica**
- **Diagnóstico Técnico**:
  No construtor de `OpenAIService` (linha 66), a tarefa de aquecimento `Task.Run(() => WarmupAndKeepAliveAsync());` é disparada na threadpool.
  Dentro desse método, mensagens de heartbeat (`[SYSTEM_HEARTBEAT]`) são adicionadas diretamente a `_history` (linha 113) e posteriormente removidas com `_history.RemoveRange(...)` (linha 151).
  Simultaneamente, se a UI invocar `StreamResponseAsync()`, `CalculateCurrentTokens()` ou `ResetHistory()`, múltiplas threads leem e escrevem sobre a mesma instância de `List<ChatMessage>`.
  Como `List<T>` não possui sincronização para escrita concorrente, ocorrem:
  1. `InvalidOperationException` por mutação durante enumeração;
  2. Corrupção da contagem interna `_size`, levando a `IndexOutOfRangeException`;
  3. Vazamento acidental de prompts de sistema fantasmas para o histórico persistente do usuário.
- **Resolução Técnica Recomendada**:
  Proteger todos os acessos a `_history` com `SemaphoreSlim` e utilizar uma cópia isolada da lista para a rotina de warmup.

```csharp
// Proteção de Concorrência do Histórico em OpenAIService.cs
private readonly SemaphoreSlim _historyLock = new(1, 1);

private async Task WarmupAndKeepAliveAsync()
{
    try
    {
        EnsureClient();
        var settings = _settingsService.LoadSettings();
        if (settings.AiProvider != "Ollama") return;

        // Isola o histórico em uma lista local
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

        warmupHistory.Add(ChatMessage.CreateUserMessage("[SYSTEM_HEARTBEAT] Responda apenas 'ONLINE'."));

        var ollamaClient = new OllamaNativeClient(settings.ApiUrl, _sharedHttpClient);
        await ollamaClient.CompleteChatAsync(settings.ModelName, warmupHistory, null, 0.1f, false, CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[WARMUP ERROR]: {ex.Message}");
    }
}
```

---

### [ASYNC-03] Race Condition, `ObjectDisposedException` e Vazamento de Cancelamento em `_generationCts`
- **Arquivo / Linhas**: `AIBWindows/Services/OpenAIService.cs:20, 25-28, 210-212`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  Ao iniciar uma geração de streaming em `StreamResponseAsync`:
  ```csharp
  _generationCts?.Dispose();
  _generationCts = new CancellationTokenSource();
  ```
  E ao cancelar em `CancelGeneration`:
  ```csharp
  public void CancelGeneration() => _generationCts?.Cancel();
  ```
  Se o usuário clicar no botão de parada exatamente no momento em que `_generationCts` está sendo descartado ou recriado, `Cancel()` é invocado em um objeto já descartado, lançando `ObjectDisposedException` não tratada. Além disso, descartar o CTS sem cancelá-lo previamente permite que streams HTTP em andamento continuem consumindo largura de banda em segundo plano.
- **Resolução Técnica Recomendada**:
  Utilizar travamento atômico com `lock` e cancelamento seguro prévio.

```csharp
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

// Em StreamResponseAsync:
CancellationToken ct;
lock (_ctsLock)
{
    try
    {
        _generationCts?.Cancel();
        _generationCts?.Dispose();
    }
    catch (ObjectDisposedException) { }

    _generationCts = new CancellationTokenSource();
    ct = _generationCts.Token;
}
```

---

### [ASYNC-04] Ignorância de `CancellationToken` na Execução Paralela de Ferramentas
- **Arquivo / Linhas**: `AIBWindows/Services/OpenAIService.cs:127, 141, 622-626, 666, 713`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  Na orquestração paralela de ferramentas:
  ```csharp
  var tasks = orderedCalls.Select(tc => ExecuteToolPairedAsync(tc, userLevel)).ToArray();
  var results = await Task.WhenAll(tasks);
  ```
  O método `ExecuteToolPairedAsync` não repassa o `CancellationToken ct` para `_toolRegistry.ExecuteToolAsync` nem para as implementações de `ITool`. Se o usuário abortar a operação, as ferramentas continuam executando em segundo plano até o término completo de cada processo ou operação de I/O.
- **Resolução Técnica Recomendada**:
  Propagar `ct` em toda a cadeia de execução de ferramentas e aplicar `.WaitAsync(ct)`.

---

### [ASYNC-05] Ciclo de Vida Inadequado de `HttpClient` e Esgotamento de Sockets (`TIME_WAIT` Leak)
- **Arquivo / Linhas**: `AIBWindows/Services/OllamaNativeClient.cs:37-43` & `AIBWindows/Services/OpenAIService.cs:82, 126, 241, 709`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  Instâncias de `OllamaNativeClient` e `HttpClient` são criadas via `new` dentro de loops iterativos do ReAct e descartadas logo em seguida. No Windows, o fechamento contínuo de sockets TCP locais (porta 11434) coloca as portas efêmeras em estado `TIME_WAIT` durante 4 minutos. Sob uso contínuo, o pool de portas do sistema operacional se esgota, resultando em `SocketException: Only one usage of each socket address is normally permitted`.
- **Resolução Técnica Recomendada**:
  Utilizar um `SocketsHttpHandler` singleton compartilhado com pooling configurado.

```csharp
public class OllamaNativeClient
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
    };

    private static readonly HttpClient SharedHttpClient = new(SharedHandler)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _httpClient;

    public OllamaNativeClient(string apiUrl, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        // ...
    }
}
```

---

### [ASYNC-06] Vazamento de Memória Nativa em `JsonDocument.Parse()` sem Descarte
- **Arquivo / Linhas**: `AIBWindows/Services/OllamaNativeClient.cs:201, 235`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  O código executa `JsonDocument.Parse(...).RootElement` sem encapsular a chamada em um bloco `using`. O `JsonDocument` aloca buffers de memória nativa a partir de um pool interno (`ArrayPool<byte>`). Ao abandonar a raiz sem invocar `.Dispose()`, a memória do buffer subjacente não é devolvida ao pool, gerando vazamento de recursos e pressão excessiva sobre o Garbage Collector.
- **Resolução Técnica Recomendada**:
  Utilizar `.Clone()` dentro de um escopo `using var doc = JsonDocument.Parse(...)`.

---

### [ASYNC-07] Risco de Deadlock com `Dispatcher.Invoke` Síncrono a partir de Threads Secundárias
- **Arquivo / Linhas**: `AIBWindows/Views/ChatWindow.xaml.cs:934, 1117, 1126` & `AIBWindows/Views/ShadowWidget.xaml.cs:26, 70`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  Eventos disparados em threads da threadpool (como `OnTokenCountChanged` e `OnSuggestionReceived`) executam `Dispatcher.Invoke(() => { ... })`. Se a UI thread estiver bloqueada aguardando um diálogo modal, redimensionamento de janela ou finalização de tarefas no evento `OnClosed`, a thread do background fica travada aguardando a fila do Dispatcher, provocando **Deadlock Mútuo**.
- **Resolução Técnica Recomendada**:
  Substituir todas as chamadas síncronas por `Dispatcher.BeginInvoke` ou `await Dispatcher.InvokeAsync()`.

---

### [ASYNC-08] Reentrância e Corrupção de Estado em `async void` na Interface Gráfica
- **Arquivo / Linhas**: `AIBWindows/Views/ChatWindow.xaml.cs:75-88, 450-627, 912-921`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  O manipulador `SendButton_Click` é assíncrono (`async void`). Durante os yields de streaming (`await Task.Yield()`), o despachante de eventos da UI permite que o usuário acione o botão "Limpar Histórico" (`ClearButton_Click`) ou selecione conversas na barra lateral. Isso altera o estado de `_history` enquanto o loop ReAct ainda está em execução, gerando falhas de coleção corrompida e inconsistências visuais.
- **Resolução Técnica Recomendada**:
  Bloquear ações concorrentes de alteração de histórico enquanto a geração de respostas estiver ativa e sinalizar o cancelamento prévio antes de limpar o contexto.

---

### [ASYNC-09] Crash Fatal do Processo por Exceção Não Tratada em Lambda `async void` no Dispatcher
- **Arquivo / Linhas**: `AIBWindows/Views/SettingsWindow.xaml.cs:23, 167-185, 189`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  No construtor de `SettingsWindow`, a inicialização de modelos executa:
  `Dispatcher.BeginInvoke(new Action(async () => await RefreshModelsAsync()));`
  O método `RefreshModelsAsync()` possui um bloco `try/finally`, mas não contém nenhum bloco `catch`. Se o serviço `GetOllamaModelsAsync()` falhar por erro de rede ou URL inválida, a exceção é lançada na pilha do Dispatcher WPF, ativando o encerramento forçado da aplicação (*Application Crash*).
- **Resolução Técnica Recomendada**:
  Adicionar tratamento explícito de exceções no método `RefreshModelsAsync`.

---

### [ASYNC-10] Conflito de I/O em Arquivos Compartilhados e Perda Silenciosa de Histórico / Configurações
- **Arquivo / Linhas**: `AIBWindows/Services/ChatHistoryService.cs:22-99` & `AIBWindows/Services/SettingsService.cs:63-102`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  `ChatHistoryService` e `SettingsService` realizam operações diretas de leitura e escrita (`File.ReadAllText`, `File.WriteAllText`) sem mecanismos de travamento de arquivo ou exclusão mútua. Quando a thread de warmup lê as configurações no mesmo milissegundo em que o usuário altera um parâmetro na UI, o Windows lança `IOException` (acesso exclusivo). Em `ChatHistoryService.LoadHistory`, o catch captura a exceção e retorna uma lista vazia, que é gravada por cima do arquivo no próximo salvamento, **apagando permanentemente todo o histórico de conversas do usuário**.
- **Resolução Técnica Recomendada**:
  Adicionar um objeto de lock (`object _fileLock = new();`) e implementar gravação atômica via arquivo temporário com `File.Move(temp, target, overwrite: true)`.

---

### [ASYNC-11] Violação de Concorrência na Manipulação de Buffers de Áudio em `MixingSampleProvider`
- **Arquivo / Linhas**: `AIBWindows/Services/GibberishVoiceService.cs:10-61`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  O método `SpeakChunk` adiciona provedores de áudio ao mixer (`_mixer.AddMixerInput(...)`) diretamente a partir de threads de streaming. Ao mesmo tempo, o driver de áudio do Windows (NAudio DAC thread) executa continuamente `_mixer.Read(...)` para reproduzir o som. A alteração da lista sem sincronização provoca `InvalidOperationException` ou travamento do subsistema de som.
- **Resolução Técnica Recomendada**:
  Adicionar sincronização com `lock (_audioLock)` no acesso aos métodos do mixer.

---

### [ASYNC-12] Padrão Sync-over-Async (`.GetAwaiter().GetResult()`) na Inicialização CLI
- **Arquivo / Linhas**: `AIBWindows/App.xaml.cs:40-42`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  O uso de `.GetAwaiter().GetResult()` na inicialização CLI encapsula exceções reais em `AggregateException`, mascara o stack trace e finaliza o processo abruptamente com `Environment.Exit` sem executar descarte limpo de singletons.
- **Resolução Técnica Recomendada**:
  Encapsular a execução CLI com tratamento explícito e encerramento controlado.

---

### [ASYNC-13] Vazamento de Memória por Inscrição Não Removida de Eventos de Longa Duração
- **Arquivo / Linhas**: `AIBWindows/Views/ChatWindow.xaml.cs:44-49, 996-1003`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  A classe `ChatWindow` subscreve eventos de serviços de longa vida (`OnTokenCountChanged`, `OnWarmupStateChanged`, `OnSuggestionReceived`), mas não remove as subscrições no método `OnClosed`. Como os serviços permanecem vivos, suas tabelas de delegates retêm a referência de `ChatWindow`, impedindo sua coleta pelo Garbage Collector.
- **Resolução Técnica Recomendada**:
  Desinscrever explicitamente todos os eventos em `OnClosed`.

---

## Seção 3: Interface Gráfica (WPF/XAML), Ciclo de Vida e Vazamento de Memória

### [UI-01] Desinscrição Inexistente de Eventos e Vazamento de Memória em Views
- **Arquivo / Linhas**: `AIBWindows/Views/ChatWindow.xaml.cs:44-53, 75-87, 996-1003`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  Conforme diagnosticado na análise arquitetural, múltiplas referências fortes a eventos de janela e controles (`this.Deactivated`, `StateChanged`, `IsVisibleChanged`) não são liberadas quando a janela é fechada. No WPF, isso mantém toda a árvore lógica e visual viva no heap gerenciado.
- **Resolução Técnica Recomendada**:
  Implementar desinscrição completa no método `OnClosed`:

```csharp
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

    if (_voiceService != null)
    {
        _voiceService.Dispose();
    }

    this.Deactivated -= Window_Deactivated;
    this.StateChanged -= ChatWindow_StateChanged;
    this.IsVisibleChanged -= ChatWindow_IsVisibleChanged;

    CloseAllShadowWidgets();
    base.OnClosed(e);
}
```

---

### [UI-02] Timers Descontrolados Retendo Controles e Consumindo CPU em Background
- **Arquivo / Linhas**: `AIBWindows/Views/ContextSidebar.xaml.cs:29-35` & `AIBWindows/Views/ShadowWidget.xaml.cs:17-22, 48-56`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  Em `ContextSidebar.xaml.cs`, o `DispatcherTimer _pulseTimer` é iniciado no construtor com intervalo de 10 segundos e nunca é desativado (`Stop()`), executando verificações contínuas mesmo se o painel estiver colapsado ou fora de tela. No `ShadowWidget.xaml.cs`, cada invocação de `HideSuggestion()` cria novos manipuladores anônimos no evento `fadeOut.Completed`, gerando acúmulo contínuo de closures na memória.
- **Resolução Técnica Recomendada**:
  Vincular o ciclo de vida dos timers aos eventos `Loaded` e `Unloaded` dos controles visuais e reutilizar instâncias estáticas de animações.

```csharp
// ContextSidebar.xaml.cs
public ContextSidebar()
{
    InitializeComponent();
    
    _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
    _pulseTimer.Tick += (s, e) => CheckApproachingReminders();

    Loaded += (s, e) => _pulseTimer.Start();
    Unloaded += (s, e) => _pulseTimer.Stop();
}
```

---

### [UI-03] Chamadas Síncronas `Dispatcher.Invoke` com Risco de Deadlock de UI
- **Arquivo / Linhas**: `AIBWindows/Views/ChatWindow.xaml.cs:934, 1117, 1126` & `AIBWindows/Views/ShadowWidget.xaml.cs:26, 70`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  O uso de `Dispatcher.Invoke` em vez de `Dispatcher.BeginInvoke` para despachar atualizações de status de tokens e widgets de monitor secundário expõe a aplicação a travamento mútuo quando a UI Thread está bloqueada em renderizações ou diálogos.
- **Resolução Técnica Recomendada**:
  Utilizar `Dispatcher.BeginInvoke` com prioridade `DispatcherPriority.Background` ou `DispatcherPriority.Normal`.

---

### [UI-04] Ineficiência de Layout e Desativação de Virtualização em ListBox
- **Arquivo / Linhas**: `AIBWindows/Views/FirstRunWindow.xaml:112-118, 145-149`
- **Severidade**: **Baixa**
- **Diagnóstico Técnico**:
  O componente `ListBox x:Name="AgentsListBox"` configura simultaneamente `ScrollViewer.CanContentScroll="False"` e `VirtualizingStackPanel`. No ecossistema WPF, a definição de `CanContentScroll="False"` desliga integralmente a virtualização de dados, forçando a criação física de todos os controles na memória durante a inicialização da tela de boas-vindas.
- **Resolução Técnica Recomendada**:
  Ajustar as propriedades de rolagem para habilitar a virtualização nativa:

```xml
<ListBox x:Name="AgentsListBox" 
         Background="Transparent" BorderThickness="0" 
         ScrollViewer.HorizontalScrollBarVisibility="Auto" 
         ScrollViewer.VerticalScrollBarVisibility="Disabled"
         ScrollViewer.CanContentScroll="True"
         VirtualizingPanel.IsVirtualizing="True"
         VirtualizingPanel.VirtualizationMode="Recycling" />
```

---

### [UI-05] Potencial Vazamento de Memória Não-Gerenciada em `EnableBlur` (P/Invoke)
- **Arquivo / Linhas**: `AIBWindows/Views/SettingsWindow.xaml.cs:281-298`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  No método `EnableBlur()`, a memória não gerenciada é alocada com `Marshal.AllocHGlobal(accentStructSize)`. Se uma exceção ocorrer durante as chamadas `Marshal.StructureToPtr` ou `SetWindowCompositionAttribute`, a linha `Marshal.FreeHGlobal(accentPtr)` é ignorada, vazando memória nativa no processo.
- **Resolução Técnica Recomendada**:
  Envolver a alocação e liberação em um bloco `try / finally`.

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

## Seção 4: Lógica de Negócios, Segurança, Resiliência e Qualidade da Suíte de Testes

### [SEC-01] Injeção de Comandos e Falta de Autorização do Usuário em `RunCommandTool`
- **Arquivo / Linhas**: `AIBWindows/Services/Tools/RunCommandTool.cs:32-58, 49`
- **Severidade**: **Crítica**
- **Diagnóstico Técnico**:
  1. O comando é montado usando concatenação direta com escape ingênuo de aspas:
     `Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\""`
     No PowerShell, essa construção permite injeção direta de comandos arbitrários e subexpressões (como `$(Start-Process evil.exe)` ou `; Remove-Item C:\ -Recurse`).
  2. O método `RunCommandTool.ExecuteAsync` **não invoca `CommandConfirmationWindow.ShowAsync`**, ignorando a camada de segurança e consentimento do usuário. Qualquer comando gerado pelo modelo de IA é executado diretamente no sistema operacional com as permissões da conta do usuário.
- **Resolução Técnica Recomendada**:
  Exigir confirmação obrigatória através de `IUserConfirmationService` antes de despachar a execução para o processo filho.

```csharp
// Validação de Segurança e Autorização Prévia
var confirmationContext = new CommandConfirmationContext
{
    Tool = Name,
    Command = command,
    Level = userLevel,
    Cwd = Environment.CurrentDirectory
};

var (authorized, _) = await _confirmationService.RequestConfirmationAsync(confirmationContext, ct);
if (!authorized)
{
    return "EXECUÇÃO RECUSADA: O usuário não autorizou a execução deste comando no sistema operacional.";
}
```

---

### [SEC-03] Path Traversal e Falta de Restrição de Acesso ao Sistema de Arquivos
- **Arquivo / Linhas**: `AIBWindows/Services/Tools/ReadFileTool.cs:44-59` & `AIBWindows/Services/Tools/WriteFileTool.cs:52-60`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  `ReadFileTool` e `WriteFileTool` não canonicam nem validam caminhos de arquivo (`Path.GetFullPath`). Isso permite que a IA leia chaves privadas (`~/.ssh/id_rsa`), credenciais na nuvem (`~/.aws/credentials`) ou sobrescreva binários em pastas de sistema (`C:\Windows\System32`) ou arquivos de inicialização do Windows (`Startup`).
- **Resolução Técnica Recomendada**:
  Implementar um validador de caminhos restritos (`PathSecurityValidator`) bloqueando diretórios críticos e extensões executáveis.

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

    public static bool ValidatePath(string inputPath, bool isWriteOperation, out string canonicalPath, out string error)
    {
        canonicalPath = Path.GetFullPath(inputPath);
        error = string.Empty;

        foreach (var dir in SensitiveDirectories)
        {
            if (canonicalPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            {
                error = "ACESSO NEGADO: Operação não permitida em diretório protegido do sistema operacional.";
                return false;
            }
        }

        if (isWriteOperation)
        {
            var ext = Path.GetExtension(canonicalPath).ToLowerInvariant();
            if (BlockedExtensions.Contains(ext))
            {
                error = "ACESSO NEGADO: Criação ou modificação de arquivos executáveis/scripts é estritamente proibida.";
                return false;
            }
        }

        return true;
    }
}
```

---

### [SEC-04] Quebra de Isolamento de Credenciais (Global Fallback) e Chaves em Texto Claro
- **Arquivo / Linhas**: `AIBWindows/Services/CredentialService.cs:26, 58, 64-77, 79`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  As linhas 64-77 implementam uma busca global que, caso uma chave não exista no arquivo do sistema solicitado (ex: `openai`), itera recursivamente todos os outros arquivos `.bin` (ex: `google`, `anthropic`, `custom_api`) e retorna a primeira credencial que coincidir com o nome da chave. Isso pode vazar chaves da Google ou Anthropic para os servidores da OpenAI ou Ollama. Além disso, o parâmetro `system` não é sanitizado contra caracteres de caminho inválidos.
- **Resolução Técnica Recomendada**:
  Remover a busca global entre sistemas, sanitizar nomes de arquivos e isolar estritamente cada namespace de credenciais.

---

### [RES-01] Blocos `catch { }` Vazios e Supressão Silenciosa de Erros
- **Arquivo / Linhas**: 
  - `AIBWindows/Services/ChatHistoryService.cs:30, 52, 120`
  - `AIBWindows/Services/OllamaNativeClient.cs:244`
  - `AIBWindows/Services/OpenAIService.cs:196, 646, 912`
  - `AIBWindows/Views/ChatWindow.xaml.cs:1101`
  - `AIBWindows/Views/FirstRunWindow.xaml.cs:81`
  - `AIBWindows/Views/SettingsWindow.xaml.cs:59`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  Exceções causadas por falhas de I/O em disco, corrupção de JSON ou desserialização de schemas de ferramentas são silenciadas com blocos `catch { }` sem registro de log. Em `OllamaNativeClient.cs:244`, se os parâmetros de uma ferramenta contiverem JSON malformado, a ferramenta é descartada silenciosamente do schema enviado ao LLM sem alerta para o usuário ou desenvolvedor.
- **Resolução Técnica Recomendada**:
  Substituir blocos `catch` vazios por logging estruturado com `ILogger<T>` ou `Console.Error.WriteLine` e tratamento de exceções específicas.

---

### [RES-02] Timeout Infinito (`InfiniteTimeSpan`) e Ausência de Políticas de Retry / Circuit Breaker
- **Arquivo / Linhas**: `AIBWindows/Services/OpenAIService.cs:728, 789` & `AIBWindows/Services/OllamaNativeClient.cs:41`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  A configuração `options.NetworkTimeout = Timeout.InfiniteTimeSpan;` é definida para os clientes de rede. Se o Ollama local travar ou a conexão com a OpenAI sofrer perda de pacotes (*black hole*), a chamada assíncrona aguarda indefinidamente, congelando a interface. Além disso, não há mecanismos de retentativa para erros transitórios (HTTP 429 - Rate Limit, HTTP 503 - Service Unavailable).
- **Resolução Técnica Recomendada**:
  Definir timeouts realistas (ex: 120 segundos) e configurar políticas de retentativa exponencial (*Exponential Backoff* com Jitter) utilizando o pacote `Polly` ou `ClientRetryPolicy`.

---

### [RES-03] Falha de Reatividade a Mudanças de Configurações em `OpenAIService.EnsureClient`
- **Arquivo / Linhas**: `AIBWindows/Services/OpenAIService.cs:761-795`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  A re-inicialização do cliente LLM em `EnsureClient()` só ocorre se `_client == null || _lastModel != modelName`. Se o usuário alterar a `ApiKey`, `ApiUrl` ou o provedor mantendo o mesmo `ModelName`, a modificação é ignorada e a aplicação continua usando os valores antigos.
- **Resolução Técnica Recomendada**:
  Monitorar a tupla completa de configurações (`ModelName`, `ApiUrl`, `ApiKey`, `AiProvider`) para invalidar o cliente em cache.

---

### [RES-04] Ausência de Manipuladores Globais de Exceção Não-Tratada em `App.xaml.cs`
- **Arquivo / Linhas**: `AIBWindows/App.xaml.cs:29-110`
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  A classe `App` não registra manipuladores para `DispatcherUnhandledException`, `AppDomain.CurrentDomain.UnhandledException` ou `TaskScheduler.UnobservedTaskException`. Qualquer exceção disparada em chamadas assíncronas desacopladas encerra o aplicativo de forma abrupta.
- **Resolução Técnica Recomendada**:
  Registrar manipuladores globais na inicialização da aplicação:

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

    this.DispatcherUnhandledException += (sender, args) =>
    {
        Console.Error.WriteLine($"[CRITICAL UI EXCEPTION]: {args.Exception}");
        MessageBox.Show($"Ocorreu um erro inesperado na interface:\n{args.Exception.Message}", "Erro de Execução - AIB", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true; // Impede o encerramento do processo
    };

    AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
    {
        Console.Error.WriteLine($"[FATAL DOMAIN EXCEPTION]: {args.ExceptionObject}");
    };

    TaskScheduler.UnobservedTaskException += (sender, args) =>
    {
        Console.Error.WriteLine($"[UNOBSERVED TASK EXCEPTION]: {args.Exception}");
        args.SetObserved();
    };
}
```

---

### [TST-01] Teste Nulo Trivial em `UnitTest1.cs`
- **Arquivo / Linhas**: `AIB.Tests/UnitTest1.cs:1-10`
- **Severidade**: **Baixa**
- **Diagnóstico Técnico**:
  O arquivo contém o método de teste vazio `public void Test1() { }`, resíduo do template padrão de criação do projeto xUnit, sem valor de validação.
- **Resolução Técnica Recomendada**:
  Remover o arquivo `UnitTest1.cs` da suíte de testes.

---

### [TST-02] Sync-over-Async e Warning `xUnit1031` no Mock de Teste (`OllamaNativeClientTests`)
- **Arquivo / Linhas**: `AIB.Tests/OllamaNativeClientTests.cs:38`
- **Severidade**: **Média**
- **Diagnóstico Técnico**:
  A configuração do mock Moq executa `request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult()`, gerando o warning `xUnit1031` (bloqueio de thread assíncrona) e podendo causar travamento na execução concorrente de testes do xUnit.
- **Resolução Técnica Recomendada**:
  Configurar o delegate de retorno do mock como método assíncrono real.

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

### [TST-03] Lacuna Crítica de Cobertura de Testes Unitários
- **Arquivo / Linhas**: Projeto `AIB.Tests` (Global)
- **Severidade**: **Alta**
- **Diagnóstico Técnico**:
  A suíte de testes atual cobre exclusivamente cálculos aritméticos de XP (`LevelServiceTests`) e validações de propriedades do payload do `OllamaNativeClient`. Módulos fundamentais do sistema possuem **0% de cobertura de testes**:
  - `OpenAIService`: O loop ReAct, agregação de chunks de streaming, trim de tokens e fallback de regex não possuem nenhum teste automatizado;
  - `Tools` (`RunCommandTool`, `ReadFileTool`, `WriteFileTool`): Nenhum teste valida restrições de permissão por nível de usuário, sanitização de caminhos ou timeouts;
  - `CredentialService`: Não há testes automatizados para criptografia/descriptografia DPAPI;
  - `ChatHistoryService` e `SettingsService`: Sem testes de persistência ou concorrência.
- **Resolução Técnica Recomendada**:
  Implementar suíte completa de testes unitários para `ToolRegistry`, ferramentas e serviços de domínio.

```csharp
// Exemplo de Teste Automatizado para ToolRegistry e ReadFileTool
namespace AIB.Tests
{
    public class ToolRegistryTests
    {
        [Fact]
        public async Task ExecuteToolAsync_WhenToolDoesNotExist_ShouldReturnNotFoundError()
        {
            var registry = new ToolRegistry(Enumerable.Empty<ITool>());
            string result = await registry.ExecuteToolAsync("ferramenta_inexistente", "{}", 1);
            result.Should().Contain("não encontrada no registry");
        }

        [Fact]
        public async Task ReadFileTool_WhenFileDoesNotExist_ShouldReturnFormattedError()
        {
            var tool = new ReadFileTool();
            string args = JsonSerializer.Serialize(new { path = "C:\\arquivo_que_nao_existe_987654.txt" });
            string result = await tool.ExecuteAsync(args, 1);
            result.Should().Contain("ERRO: Arquivo não encontrado");
        }
    }
}
```

---

## Roteiro Estratégico de Refatoração e Arquitetura Alvo (Target Architecture)

Para elevar o AIB ao nível de excelência corporativa, escalabilidade e manutenibilidade, propõe-se a seguinte arquitetura modular limpa (*Clean Architecture* / MVVM desacoplado):

### 1. Estrutura Proposta de Solução (.NET 8):

```
AIB/
├── src/
│   ├── AIB.Core/                          # Camada de Domínio e Abstrações Neutras
│   │   ├── Models/                        # ChatSession, AgentProfile, UserSettings, MemoryRecord
│   │   ├── Interfaces/                    # IAiCompletionService, ITool, ICredentialVault, IMemoryService
│   │   └── Services/                      # LevelEngine, PromptBuilder, ReActOrchestrator
│   │
│   ├── AIB.Infrastructure/                # Adaptadores de I/O e Provedores Externos
│   │   ├── AI/                            # OpenAiClientAdapter, OllamaClientAdapter, TiktokenService
│   │   ├── Storage/                       # DpapiCredentialVault, LiteDbMemoryService, JsonSettingsService
│   │   ├── Audio/                         # WhisperNetVoiceService, NAudioSoundService
│   │   ├── Vision/                        # WindowsMediaOcrService, ScreenCaptureService
│   │   └── Tools/                         # RunCommandTool, ReadFileTool, WriteFileTool
│   │
│   └── AIB.App/                           # Apresentação Desktop WPF (.NET 8 WPF)
│       ├── Converters/                    # ValueToStarForegroundConverter, LevelToBrushConverter
│       ├── ViewModels/                    # ChatViewModel, SettingsViewModel, FirstRunViewModel
│       ├── Views/                         # ChatWindow, SettingsWindow, FirstRunWindow, ShadowWidget
│       ├── Services/                      # WpfUserConfirmationService, WpfNotificationService
│       └── App.xaml.cs                    # Generic Host & Dependency Injection Setup
│
└── tests/
    └── AIB.Tests/                         # Suíte de Testes Automatizados (xUnit + Moq + FluentAssertions)
        ├── Unit/                          # Testes de Core, ViewModels e Tools
        └── Integration/                   # Testes de Persistência e Adaptadores de Rede com HttpMock
```

### 2. Plano de Execução em Fases:

| Fase | Foco Principal | Entregáveis | Impacto Esperado |
|---|---|---|---|
| **Fase 1** | **Hotfixes Críticos de Concorrência & Segurança** | - Correção do deadlock de pipes em `RunCommandTool.cs`.<br>- Proteção por semáforo no `_history` em `OpenAIService.cs`.<br>- Adição de modal de autorização em ferramentas de sistema.<br>- Sanitização de caminhos contra Path Traversal em `ReadFileTool`/`WriteFileTool`. | Eliminação imediata de travamentos e vulnerabilidades críticas de segurança. |
| **Fase 2** | **Injeção de Dependências & Arquitetura MVVM** | - Configuração do `Generic Host` em `App.xaml.cs`.<br>- Criação de `ChatViewModel`, `SettingsViewModel` e `FirstRunViewModel`.<br>- Migração da árvore visual procedural do `ChatWindow` para `ItemsControl` e `DataTemplates`. | Desacoplamento da UI Thread, testabilidade de apresentação e limpeza de memória. |
| **Fase 3** | **Integração Real de Serviços (Eliminação de Stubs)** | - Implementação de `LiteDbMemoryService` usando o pacote LiteDB.<br>- Integração do OCR real com `PdfPig` / Windows OCR.<br>- Implementação de reconhecimento de voz local com `Whisper.net`. | Ativação completa dos recursos prometidos na interface sem cascas vazias. |
| **Fase 4** | **Resiliência de Rede & Ampliação da Cobertura de Testes** | - Configuração de timeouts e políticas de retry exponencial.<br>- Implementação de testes automatizados para todas as ferramentas e `OpenAIService`.<br>- Remoção de warnings `xUnit1031` e testes residuais. | Confiabilidade contínua de software e prevenção contra regressões. |

---

## Conclusão

A auditoria estática profunda no projeto AIB revelou uma base conceitual inovadora, porém sobrecarregada por implementações frágeis de concorrência, acoplamento estrutural e ausência de testes nos componentes mais críticos.

A implementação das correções pontuais detalhadas e a transição para a **Arquitetura Alvo Proposta** transformará o AIB em um assistente inteligente altamente estável, seguro contra injeções de comandos ou vazamento de credenciais, imune a deadlocks de interface e apto para evolução sustentável em ambiente corporativo.
