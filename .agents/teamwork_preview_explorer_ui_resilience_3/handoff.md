# Handoff Report: UI, Resilience, Security & Test Suite Survey

## 1. Observation

### WPF / XAML & Memory Leaks
- `AIBWindows/Views/ChatWindow.xaml.cs:44-50`:
  `_openAIService.OnTokenCountChanged += UpdateTokenCounterUI;`
  `_openAIService.OnWarmupStateChanged += HandleWarmupState;`
  `_shadowService.OnSuggestionReceived += OnShadowSuggestion;`
  `_shadowService.OnActiveScreenChanged += OnActiveScreenChanged;`
  In `OnClosed` (lines 996-1003), none of these event subscriptions are removed.
- `AIBWindows/Views/ContextSidebar.xaml.cs:29-35`:
  `_pulseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };`
  `_pulseTimer.Tick += (s, e) => CheckApproachingReminders();`
  `_pulseTimer.Start();`
  `_pulseTimer` runs indefinitely without `Unloaded` stop or cleanup.
- `AIBWindows/Views/ChatWindow.xaml.cs:934, 1117, 1126`:
  `Dispatcher.Invoke(() => { ... });` called from background thread callbacks.
- `AIBWindows/Views/SettingsWindow.xaml.cs:286, 297`:
  `Marshal.AllocHGlobal(accentStructSize)` without a `try ... finally` block ensuring `Marshal.FreeHGlobal(accentPtr)`.
- `AIBWindows/Views/FirstRunWindow.xaml:116`:
  `<ListBox x:Name="AgentsListBox" ScrollViewer.CanContentScroll="False">` with `<VirtualizingStackPanel Orientation="Horizontal" />` disabling UI virtualization.

### Tools & Security
- `AIBWindows/Services/Tools/RunCommandTool.cs:62-68`:
  ```csharp
  var processTask = Task.Run(() =>
  {
      string output = process.StandardOutput.ReadToEnd();
      string error = process.StandardError.ReadToEnd();
      process.WaitForExit();
      return (output, error);
  });
  ```
  Synchronous read of stdout before stderr with `WaitForExit()` causing OS pipe buffer overflow deadlock.
- `AIBWindows/Services/Tools/RunCommandTool.cs:49`:
  `Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",`
  No user confirmation modal invoked; executes arbitrary PowerShell commands immediately.
- `AIBWindows/Services/Tools/ReadFileTool.cs:50` & `WriteFileTool.cs:59`:
  No path sandboxing or canonicalization checks; arbitrary file system access.
- `AIBWindows/Services/CredentialService.cs:42, 64-77, 79`:
  Uses DPAPI (`ProtectedData.Protect` with `CurrentUser`). Lines 64-77 iterate all `.bin` files on miss and return keys from unrelated systems. Errors returned as string literals (`"ERRO: ..."`).

### Error Handling & Resilience
- `AIBWindows/Services/ChatHistoryService.cs:30, 52, 120`:
  `catch { }` swallowing I/O exceptions on chat history persistence without logging.
- `AIBWindows/Services/OllamaNativeClient.cs:244`:
  `catch { }` in `FormatTools` silently dropping tool declarations.
- `AIBWindows/Services/OpenAIService.cs:728, 789` & `OllamaNativeClient.cs:41`:
  `options.NetworkTimeout = System.Threading.Timeout.InfiniteTimeSpan;` causing indefinite hangs on connection stalls.
- `AIBWindows/Services/OpenAIService.cs:761`:
  `if (_client == null || _lastModel != modelName)` ignores updates to `ApiKey`/`ApiUrl`/`AiProvider` if `ModelName` remains identical.
- `AIBWindows/App.xaml.cs:29-110`:
  No global exception handlers registered (`DispatcherUnhandledException`, `UnhandledException`, `UnobservedTaskException`).

### Test Suite (`AIB.Tests`)
- `AIB.Tests/UnitTest1.cs:1-10`: Empty dummy test (`public void Test1() { }`).
- `AIB.Tests/LevelServiceTests.cs`: Only 3 lookup methods tested.
- `AIB.Tests/OllamaNativeClientTests.cs:38`: `request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult()` generates compiler warning `xUnit1031`.
- `dotnet test c:\Users\Carlo\CPAPS\AIB\AIB.Tests\AIB.Tests.csproj`: 24 passed in 235 ms, but 0 unit tests exist for `OpenAIService`, `Tools`, `CredentialService`, `ChatHistoryService`, or `SettingsService`.

---

## 2. Logic Chain

1. **Process Deadlock in `RunCommandTool`**:
   - `Observation`: `process.StandardOutput.ReadToEnd()` blocks until stdout reaches EOF.
   - `Inference`: When a command produces stderr output exceeding the OS pipe buffer (4KB–64KB) prior to closing stdout, the child process blocks writing to stderr, while the parent process blocks reading stdout.
   - `Conclusion`: The process task deadlocks and always hits the 30-second timeout.

2. **Command Injection & Missing Confirmation**:
   - `Observation`: `RunCommandTool.ExecuteAsync` constructs command arguments with string interpolation and never invokes `CommandConfirmationWindow.ShowAsync`.
   - `Inference`: The ReAct loop can trigger destructive commands (`Remove-Item`, script downloads) without human authorization.
   - `Conclusion`: Critical security vulnerability requiring modal integration and input sanitization.

3. **Memory Leaks via Event Retention in `ChatWindow`**:
   - `Observation`: `_openAIService` and `_shadowService` events are subscribed in constructor and never unhooked in `OnClosed`.
   - `Inference`: Static or long-lived services hold strong delegate references to `ChatWindow`, preventing garbage collection of the window and its visual tree.
   - `Conclusion`: High-severity memory leak requiring cleanup in `OnClosed`.

4. **Credential Isolation Failure in `CredentialService`**:
   - `Observation`: `RetrieveCredential` searches across all `.bin` files if the requested system file is absent.
   - `Inference`: Requesting a key for `openai` can return a key configured for `anthropic` or `gemini`, and returning `"ERRO: ..."` sends error strings as HTTP Bearer tokens.
   - `Conclusion`: Global fallback must be eliminated and return types converted to nullable/result types.

5. **Test Suite Coverage Gap**:
   - `Observation`: Only 2 test files exist with 24 data-driven cases on arithmetic and 1 mock test on JSON serialization.
   - `Inference`: Core business logic (ReAct loop, file tools, credential encryption, settings migration) is completely untested.
   - `Conclusion`: Critical test suite quality deficit.

---

## 3. Caveats
- No runtime memory profiling session (e.g. dotMemory / Visual Studio Diagnostics Tools) was run; findings are based on static code analysis of event handler lifetimes, dispatcher calls, and unmanaged allocations.
- Dynamic skill loading (`execute_skill` and Python/PowerShell scripts) relies on external interpreter availability on the user's host machine.

---

## 4. Conclusion
The AIB codebase contains 2 **Critical** vulnerabilities (Process deadlock in `RunCommandTool` and PowerShell command execution bypassing security confirmations), 8 **High** severity issues (WPF memory leaks, `Dispatcher.Invoke` deadlocks, path traversal, credential isolation leakage, swallowed exceptions, infinite network timeouts, unhandled app crashes, and 0% test coverage for core services), 4 **Medium** severity issues, and 2 **Low** severity issues. Full technical resolutions with C# snippets have been compiled in `analysis.md`.

---

## 5. Verification Method
- **Test Suite Execution**:
  Run `dotnet test c:\Users\Carlo\CPAPS\AIB\AIB.Tests\AIB.Tests.csproj` to verify existing tests pass and observe warning `xUnit1031` on line 38 of `OllamaNativeClientTests.cs`.
- **Code Inspection**:
  - Check `AIBWindows/Services/Tools/RunCommandTool.cs:62-68` for synchronous stdout/stderr read.
  - Check `AIBWindows/Views/ChatWindow.xaml.cs:996-1003` for missing unsubscriptions.
  - Check `AIBWindows/Services/CredentialService.cs:64-77` for global search loop across `.bin` files.
- **Detailed Findings Reference**:
  Review `c:\Users\Carlo\CPAPS\AIB\.agents\teamwork_preview_explorer_ui_resilience_3\analysis.md`.
