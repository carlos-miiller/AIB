# Handoff Report — Explorer 2: Asynchrony, Concurrency, Streaming & Thread-Safety

**Agent Folder**: `c:\Users\Carlo\CPAPS\AIB\.agents\teamwork_preview_explorer_async_2\`  
**Target Solution**: AIB (.NET 8 WPF Application)  
**Date**: 2026-08-19  

---

## 1. Observation

Direct static code observations recorded across the AIB codebase:

### 1.1 Process Pipe Redirection Deadlock
- **File**: `AIBWindows/Services/Tools/RunCommandTool.cs:62-75`
- **Verbatim Code**:
  ```csharp
  var processTask = Task.Run(() =>
  {
      string output = process.StandardOutput.ReadToEnd();
      string error = process.StandardError.ReadToEnd();
      process.WaitForExit();
      return (output, error);
  });
  ```
  `process.StandardOutput.ReadToEnd()` synchronously blocks before reading `StandardError.ReadToEnd()`. If stderr output fills the OS buffer (4KB–64KB), the process deadlocks until the 30s timeout trips. `process.Kill()` lacks `entireProcessTree: true`.

### 1.2 Unsynchronized Shared `_history` Collection Mutation
- **File**: `AIBWindows/Services/OpenAIService.cs:17, 66, 113, 151-152, 218, 610, 631, 934-950`
- **Verbatim Code**:
  Line 17: `private readonly List<ChatMessage> _history = new();`
  Line 66: `Task.Run(() => WarmupAndKeepAliveAsync());`
  Line 113: `_history.Add(ChatMessage.CreateUserMessage("[SYSTEM_HEARTBEAT]..."));`
  Line 151-152: `_history.RemoveRange(realHistoryCount, _history.Count - realHistoryCount);`
  Line 218: `_history.Add(ChatMessage.CreateUserMessage(userMessage));`
  Line 939: `_history.RemoveAt(removeIdx);`
  `List<ChatMessage>` is accessed concurrently by the ThreadPool warmup task and the UI chat streaming task without synchronization.

### 1.3 `CancellationTokenSource` Disposal and Race Condition
- **File**: `AIBWindows/Services/OpenAIService.cs:20, 25-28, 210-212`
- **Verbatim Code**:
  ```csharp
  public void CancelGeneration()
  {
      _generationCts?.Cancel();
  }
  // In StreamResponseAsync:
  _generationCts?.Dispose();
  _generationCts = new CancellationTokenSource();
  var ct = _generationCts.Token;
  ```
  `_generationCts` is disposed without prior cancellation. Calling `CancelGeneration()` while disposing or recreating throws `ObjectDisposedException`.

### 1.4 Ignored Cancellation in Parallel Tool Execution
- **File**: `AIBWindows/Services/OpenAIService.cs:127, 141, 622-626, 666, 713`
- **Verbatim Code**:
  Line 622-625:
  ```csharp
  var tasks = orderedCalls
      .Select(tc => ExecuteToolPairedAsync(tc, userLevel))
      .ToArray();
  var results = await Task.WhenAll(tasks);
  ```
  `ExecuteToolPairedAsync` and `ITool.ExecuteAsync` do not accept `ct`. Background tools continue executing after user cancellation.

### 1.5 Ephemeral `HttpClient` Creation & Socket Exhaustion
- **File**: `AIBWindows/Services/OllamaNativeClient.cs:37-43` & `AIBWindows/Services/OpenAIService.cs:82, 126, 241, 709`
- **Verbatim Code**:
  `public OllamaNativeClient(string apiUrl, HttpClient? httpClient = null)`
  `_httpClient = httpClient ?? new HttpClient();`
  `new OllamaNativeClient(...)` is instantiated on every ReAct loop turn (up to 18x per prompt). `HttpClient` instances are neither shared nor disposed, leading to `TIME_WAIT` socket leak.

### 1.6 Unmanaged Memory Leak in `JsonDocument.Parse`
- **File**: `AIBWindows/Services/OllamaNativeClient.cs:201, 235`
- **Verbatim Code**:
  Line 201: `arguments = JsonDocument.Parse(tc.FunctionArguments.ToString()).RootElement`
  Line 235: `funcObj["parameters"] = JsonDocument.Parse(parametersJson).RootElement;`
  `JsonDocument.Parse` allocates unmanaged memory from `ArrayPool<byte>` but is never disposed.

### 1.7 Synchronous `Dispatcher.Invoke` Deadlock Risk
- **File**: `AIBWindows/Views/ChatWindow.xaml.cs:934, 1117, 1126` & `AIBWindows/Views/ShadowWidget.xaml.cs:26, 70`
- **Verbatim Code**:
  `UpdateTokenCounterUI`: `Dispatcher.Invoke(() => ...)`
  `OnActiveScreenChanged`: `Dispatcher.Invoke(() => ...)`
  `OnShadowSuggestion`: `Dispatcher.Invoke(() => ...)`
  Synchronous invocation from ThreadPool threads deadlocks if the UI thread is executing a modal dialog or `DragMove()`.

### 1.8 UI Reentrancy & History Collision during Active Stream
- **File**: `AIBWindows/Views/ChatWindow.xaml.cs:75-88, 450-627, 912-921`
- **Verbatim Code**:
  `ClearButton_Click` calls `MessagesPanel.Children.Clear()` and `_openAIService.ResetHistory()` while `SendButton_Click` (`async void`) is actively iterating `await foreach (var chunk in stream)` and awaiting `Task.Yield()`.

### 1.9 Unhandled Exceptions in `async void` Dispatcher Lambda
- **File**: `AIBWindows/Views/SettingsWindow.xaml.cs:23, 170-185, 189`
- **Verbatim Code**:
  Line 23: `Dispatcher.BeginInvoke(new Action(async () => await RefreshModelsAsync()));`
  `RefreshModelsAsync` has `try/finally` without `catch`. Network errors crash the process via unobserved `async void` dispatch.

### 1.10 Unsynchronized File I/O Race Conditions
- **File**: `AIBWindows/Services/ChatHistoryService.cs:22-53, 55-99` & `AIBWindows/Services/SettingsService.cs:63-102`
- **Verbatim Code**:
  `File.ReadAllText(HistoryFilePath)`, `File.WriteAllText(HistoryFilePath, json)`, `File.ReadAllBytes(SettingsPath)`, `File.WriteAllBytes(SettingsPath, encryptedBytes)`.
  Concurrent reads and writes collide with `IOException`. Catch blocks return empty defaults, wiping user chat history or reverting settings to defaults.

### 1.11 Audio Mixer Thread Concurrency Violation
- **File**: `AIBWindows/Services/GibberishVoiceService.cs:10-61`
- **Verbatim Code**:
  Line 60: `_mixer.AddMixerInput(sampleProvider);`
  NAudio's `MixingSampleProvider` is read concurrently by the audio playback thread while `AddMixerInput` mutates its list from UI/streaming threads without locks.

### 1.12 Sync-over-Async in CLI Runner
- **File**: `AIBWindows/App.xaml.cs:40-42`
- **Verbatim Code**:
  Line 41: `RunCliCommandAsync(e.Args).GetAwaiter().GetResult();`

### 1.13 Window Event Handler Memory Leak
- **File**: `AIBWindows/Views/ChatWindow.xaml.cs:44-49, 996-1003`
- Event subscriptions to `_openAIService` and `_shadowService` are never detached in `OnClosed`.

---

## 2. Logic Chain

1. **Deadlock in RunCommandTool**:
   - `StandardOutput.ReadToEnd()` blocks until stdout EOF.
   - If the subprocess writes >4KB/64KB to stderr first, the OS pipe buffer saturates.
   - The subprocess blocks on stderr write; C# blocks on stdout read → permanent deadlock.
   - The 30s timeout is reached, `process.Kill()` is invoked, but child processes survive because `entireProcessTree: true` is absent.

2. **Data Corruption in `_history`**:
   - `_history` is a single `List<ChatMessage>` instance shared across the entire `OpenAIService`.
   - `WarmupAndKeepAliveAsync()` runs in `Task.Run` on a background thread.
   - Concurrent writes (`Add`, `RemoveRange`, `RemoveAt`) on `List<T>` violate thread safety guarantees, corrupting internal capacity counters and throwing `InvalidOperationException`.

3. **Socket Exhaustion (`TIME_WAIT`)**:
   - Every ReAct step instantiates `OllamaNativeClient` with `new HttpClient()`.
   - Each `HttpClient` allocates socket ports. Unclosed sockets remain in `TIME_WAIT` for 240 seconds on Windows.
   - Active chat sessions quickly exhaust the ~16,000 ephemeral TCP port pool, causing `SocketException`.

4. **UI Thread Deadlocks**:
   - Background threads call `Dispatcher.Invoke(...)`.
   - If the UI thread is suspended in a modal loop (`ShowDialog`) or dragging (`DragMove`), the background thread blocks waiting for Dispatcher return, resulting in a classic WPF UI deadlock.

5. **Data Loss on File Collisions**:
   - `File.ReadAllText` and `File.WriteAllText` are called without synchronization locks.
   - When a read overlaps a write, Windows throws `IOException`.
   - Catch handlers catch the exception and return empty collections (`new List<ChatSession>()` or `new UserAppSettings()`).
   - The subsequent save writes the empty collection, permanently destroying the user's chat history and settings.

---

## 3. Caveats

- `AIB.Tests` currently contains minimal tests (`LevelServiceTests.cs`, `OllamaNativeClientTests.cs`, `UnitTest1.cs`) and does not stress-test concurrency, tool loops, or UI dispatching.
- Audio playback issues (`GibberishVoiceService`) depend on hardware audio drivers; on some DACs NAudio fails silently rather than throwing `InvalidOperationException`.
- `AuditLogService` is currently a stub returning `Task.CompletedTask`, so log I/O concurrency is not yet active, but will inherit file race issues once implemented if not synchronized.

---

## 4. Conclusion

The AIB codebase contains **13 critical, high, and medium concurrency/async defects** that severely impact runtime reliability, causing process hangs (deadlocks), application crashes (unhandled async void exceptions), socket pool exhaustion, and data loss (chat history wiping).

All 13 issues have been cataloged with exact file and line references, failure scenarios, and complete, production-ready C# fix snippets in `c:\Users\Carlo\CPAPS\AIB\.agents\teamwork_preview_explorer_async_2\analysis.md`.

---

## 5. Verification Method

To verify and test the findings independently:

1. **Static Verification**:
   Inspect the exact line numbers specified in `analysis.md`:
   - `AIBWindows/Services/Tools/RunCommandTool.cs` (lines 62-75)
   - `AIBWindows/Services/OpenAIService.cs` (lines 17, 66, 113, 151, 210-212, 622-626, 709)
   - `AIBWindows/Services/OllamaNativeClient.cs` (lines 37-43, 201, 235)
   - `AIBWindows/Views/ChatWindow.xaml.cs` (lines 450, 553, 912, 934, 1117)
   - `AIBWindows/Services/ChatHistoryService.cs` (lines 27, 50)
   - `AIBWindows/Services/SettingsService.cs` (lines 74, 101)

2. **Build and Test Verification Command**:
   Run the project test suite using dotnet CLI in PowerShell:
   ```pwsh
   dotnet test c:\Users\Carlo\CPAPS\AIB\AIB.Tests\AIB.Tests.csproj
   ```

3. **Reproduction Tests**:
   - **Process Deadlock**: Invoke `RunCommandTool.ExecuteAsync` with a PowerShell command producing >64KB on stderr (e.g. `1..5000 | ForEach-Object { [Console]::Error.WriteLine("Error $_") }`). Observe 30-second hang and timeout.
   - **Socket Leak**: Run a 10-turn conversation with intelligent tools enabled and run `netstat -ano | findstr 11434` to observe escalating `TIME_WAIT` socket accumulation.
