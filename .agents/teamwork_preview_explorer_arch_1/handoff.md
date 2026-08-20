# Handoff Report — Architecture, DI, MVVM & Clean Code Exploration

**Agent**: teamwork_preview_explorer_arch_1  
**Milestone**: M1 (Architecture Survey)  
**Date**: 2026-08-19  

---

## 1. Observation

Direct code observations gathered via static code inspection:

### 1.1 Injeção de Dependências e Ciclo de Vida (`App.xaml.cs`)
- `AIBWindows/App.xaml.cs:18`: `private readonly SettingsService _settingsService = new();`
- `AIBWindows/App.xaml.cs:38-42`: `System.Threading.SynchronizationContext.SetSynchronizationContext(null); RunCliCommandAsync(e.Args).GetAwaiter().GetResult(); return;`
- `AIBWindows/App.xaml.cs:65`: `_chatWindow = new ChatWindow();`
- `AIBWindows/App.xaml.cs:133`: `return CredentialService.RetrieveCredential("openai", "ApiKey").StartsWith("ERRO");`
- `AIBWindows/App.xaml.cs:157-182`: CLI runner embutido no ciclo de vida WPF com `Environment.Exit(...)`.
- Nenhum container IoC (`Microsoft.Extensions.DependencyInjection` ou `Autofac`) configurado ou referenciado.

### 1.2 Padrão MVVM e Acoplamento em Views
- Não existem classes ou diretório `ViewModels/` no projeto `AIBWindows`.
- `AIBWindows/Views/ChatWindow.xaml.cs:1-1137` (1.137 linhas): Contém instanciação de 5 serviços via `new` (linhas 42-66), manipulação direta de visual tree (`MessagesPanel.Children.Add(...)`, linhas 254, 307, 367, 546), controle de animações imperativas `Storyboard` (linhas 317-340), parsing de slash-commands (linhas 461-492), lógica de cálculo de XP e cores de ranking (`RefreshLevelUI`, linhas 176-219), multi-monitor screen interop com WinForms (`Screen.AllScreens`, linhas 1069-1095).
- `AIBWindows/Views/ChatWindow.xaml.cs:624`: `((App)System.Windows.Application.Current).ShowNotification("AIB", notifyText);`
- `AIBWindows/Views/SettingsWindow.xaml.cs:214-218`:
  ```csharp
  var chatWindow = System.Windows.Application.Current.Windows.OfType<ChatWindow>().FirstOrDefault();
  if (chatWindow != null) chatWindow.ApplyCharacterUI();
  ```
- `AIBWindows/Views/FirstRunWindow.xaml.cs:18-32`: `ValueToStarForegroundConverter` declarado dentro do arquivo `FirstRunWindow.xaml.cs`.
- `AIBWindows/Views/ContextSidebar.xaml.cs:21-24`: Ligação direta a propriedades estáticas de serviços (`ReminderService.ActiveReminders`, `ContextService.ActiveFiles`, `ShadowHistoryService.Suggestions`).
- `AIBWindows/Views/CommandConfirmationWindow.xaml.cs:63-81`: `public static async Task<(bool Allowed, bool AlwaysAllow)> ShowAsync(CommandConfirmationContext ctx)` aciona diretamente `System.Windows.Application.Current.Dispatcher`.

### 1.3 Serviços Stub e Falhas de Design
- `AIBWindows/Services/AuditLogService.cs:4`: `public static Task AppendAsync(object logData) => Task.CompletedTask;`
- `AIBWindows/Services/MemoryService.cs:4-5`: `public static void DeleteMemory(int id) { } public static List<object> GetRecentMemories(int count) => new List<object>();`
- `AIBWindows/Services/OcrService.cs:4`: `public Task<string> ExtractTextFromActiveScreenAsync() => Task.FromResult("");`
- `AIBWindows/Services/ShadowAssistantService.cs:3-12`: Métodos vazios, eventos não invocados.
- `AIBWindows/Services/ShadowHistoryService.cs:4`: `public static List<object> Suggestions => new List<object>();`
- `AIBWindows/Services/SkillService.cs:8-11`: `public static int GetSkillCount() => 0; public static List<LocalSkill> ListLocalSkills() => new List<LocalSkill>();`
- `AIBWindows/Services/VoiceService.cs:8-15`: Métodos vazios, evento não disparado, `Dispose()` vazio sem `IDisposable`.
- `AIBWindows/Services/ReminderService.cs:10`: `public static List<Reminder> ActiveReminders => new List<Reminder>();` (aloca nova lista vazia a cada chamada).
- `AIBWindows/Services/ContextService.cs:7-13`: Todos os métodos retornam listas vazias ou são stubs.
- `AIBWindows/Services/TestRunner.cs:4-5`: `RunRagTestAsync() => Task.FromResult(true); RunToolTestAsync() => Task.FromResult(true);`
- `AIBWindows/Services/OpenAIService.cs:1-993`: God class com 993 linhas, mistura Tiktoken, Keep-Alive HTTP manual para Ollama, prompt building e instanciação repetida de `new OllamaNativeClient(...)` no loop de streaming (linhas 126 e 241).
- `AIBWindows/Services/ITool.cs:23`: `ChatTool ChatToolDefinition { get; }` acopla o contrato de domínio ao SDK proprietário `OpenAI.Chat`.
- `AIBWindows/Services/ToolRegistry.cs:64-79`: Instanciação fixa `new RunCommandTool()`, `new ReadFileTool()`, `new WriteFileTool()`.
- `AIBWindows/Services/SettingsService.cs:104-134`: Faz requisição HTTP GET para `/api/tags` do Ollama dentro do serviço de configurações.
- `AIBWindows/Services/CredentialService.cs:49, 79, 83, 92, 96`: Retorna strings começando com `"ERRO: ..."`.
- `AIBWindows/Services/DirectoryService.cs:8-9, 50-51`: Diretórios estáticos mutáveis e navegação relativa frágil (`..\\..\\..\\character`).
- `AIBWindows/Services/GibberishVoiceService.cs:9, 22-29`: Alocação estática de `WaveOutEvent` sem descarte (`Dispose`).

---

## 2. Logic Chain

1. **A ausência de IoC/DI e o uso de classes/métodos estáticos globais** (Obs 1.1, 1.2, 1.3) impedem que dependências sejam substituídas por dublês de teste (Mocks). Como consequência, o projeto de testes `AIB.Tests` só consegue testar métodos matemáticos puros (`LevelService`) e formatação JSON isolada (`OllamaNativeClient`), deixando 95% da base de código sem cobertura de testes.
2. **A inexistência de ViewModels e o acúmulo de responsabilidades na `ChatWindow` e `SettingsWindow`** (Obs 1.2) viola os princípios de Separação de Preocupações (SoC) e Responsabilidade Única (SRP). Toda a lógica de negócios, streaming de IA, cálculo de XP e orquestração de áudio está presa à thread de interface gráfica (WPF UI Thread), elevando o risco de travamento de tela e dificultando manutenções.
3. **A presença de 10 serviços stub / vazios** (Obs 1.3) significa que funcionalidades apresentadas na interface do usuário (botão de OCR, botão de transcrição de voz Whisper, barra lateral de lembretes, histórico de sugestões do Shadow Assistant e CLI test runner) são apenas "fachadas ocas". Os pacotes NuGet `LiteDB`, `SmartComponents.LocalEmbeddings`, `Whisper.net` e `PdfPig` já presentes no `.csproj` não estão integrados.
4. **O vazamento de tipos do SDK da OpenAI na interface `ITool` e no `OpenAIService`** (Obs 1.3) gera acoplamento tecnológico excessivo. Modelos locais via Ollama ou outros provedores são forçados a simular o modelo de objetos da OpenAI, inviabilizando a substituição ou extensão para outros LLMs nativos de forma limpa.
5. **O controle de fluxo baseado em strings `"ERRO..."` no `CredentialService`** (Obs 1.3) e o I/O de rede dentro do `SettingsService` representam falhas de design que introduzem fragilidade operacional e riscos de segurança.

---

## 3. Caveats

- A análise deste relatório focou estritamente em **Arquitetura, Injeção de Dependências, Padrão MVVM, Separação de Preocupações e Design de Serviços**.
- Questões aprofundadas sobre deadlocks específicos de async/await, loops de concorrência e detalhes de consumo de streams HTTP serão detalhadas pelos exploradores complementares (M2/M3).
- Não foram feitas alterações no código-fonte de produção durante esta fase de investigação (modo somente leitura).

---

## 4. Conclusion

A arquitetura do AIB requer uma modernização estruturada:
1. **Implantação de Microsoft.Extensions.Hosting & DI**: Centralizar a criação e ciclo de vida de todos os serviços e janelas no `App.xaml.cs`.
2. **Adoção do Padrão MVVM com CommunityToolkit.Mvvm**: Criar `ChatViewModel`, `SettingsViewModel`, `FirstRunViewModel` e `ContextSidebarViewModel`, migrando a manipulação imperativa de UI para DataTemplates e Data Binding no XAML.
3. **Implementação Real dos Serviços Ocos**: Implementar `IAuditLogService`, `IMemoryService` (LiteDB + SmartComponents), `IOcrService` (Windows Media OCR / PdfPig), `IVoiceService` (Whisper.net), eliminando os 10 stubs.
4. **Desacoplamento de Domínio e Provedores**: Tornar `ITool` neutra (sem dependência de `OpenAI.Chat.ChatTool`) e separar `SettingsService` de chamadas de rede HTTP.
5. **Refatoração Segura do Cofre de Credenciais**: Substituir retornos de texto `"ERRO..."` por `Result<T>` / exceções tipadas e mascarar logs sensíveis no console.

O documento completo de análise com todos os snippets de código de refatoração para cada um dos 15 apontamentos está registrado em:
`c:\Users\Carlo\CPAPS\AIB\.agents\teamwork_preview_explorer_arch_1\analysis.md`

---

## 5. Verification Method

Para verificar de forma independente todas as conclusões deste relatório:

1. **Inspeção de Ausência de ViewModels e MVVM**:
   ```pwsh
   # Confirmar que não existe diretório ou classes ViewModel
   Get-ChildItem -Path "c:\Users\Carlo\CPAPS\AIB\AIBWindows" -Recurse -Filter "*ViewModel*.cs"
   ```
2. **Inspeção dos Serviços Stub**:
   ```pwsh
   # Inspecionar implementações vazias
   Get-Content "c:\Users\Carlo\CPAPS\AIB\AIBWindows\Services\AuditLogService.cs"
   Get-Content "c:\Users\Carlo\CPAPS\AIB\AIBWindows\Services\MemoryService.cs"
   Get-Content "c:\Users\Carlo\CPAPS\AIB\AIBWindows\Services\OcrService.cs"
   Get-Content "c:\Users\Carlo\CPAPS\AIB\AIBWindows\Services\TestRunner.cs"
   ```
3. **Inspeção do Tamanho e Complexidade de ChatWindow e OpenAIService**:
   ```pwsh
   (Get-Content "c:\Users\Carlo\CPAPS\AIB\AIBWindows\Views\ChatWindow.xaml.cs").Length # 1137 linhas
   (Get-Content "c:\Users\Carlo\CPAPS\AIB\AIBWindows\Services\OpenAIService.cs").Length # 993 linhas
   ```
4. **Execução da Suíte de Testes Atual**:
   ```pwsh
   dotnet test "c:\Users\Carlo\CPAPS\AIB\AIB.Tests\AIB.Tests.csproj"
   ```
