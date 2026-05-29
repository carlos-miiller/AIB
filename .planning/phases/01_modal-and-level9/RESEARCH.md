# Phase 01: Modal confirmation + Level-9 alignment — Research

**Researched:** 2026-05-28
**Domain:** WPF UI threading + secure shell execution + append-only audit logging (.NET 8)
**Confidence:** HIGH

## Summary

This phase wires the existing-but-orphan `CommandConfirmationWindow` into `RunCommandTool.ExecuteAsync`, reshapes the Level-9 semantic so the human-in-the-loop survives at all levels, and adds a JSONL audit trail. All eight context decisions (D1–D8 in CONTEXT.md) are locked; this document is implementation guidance for that locked path, not exploration of alternatives.

Three Microsoft Learn pages back the core threading/dialog mechanics: `Dispatcher.InvokeAsync<TResult>` returns a `DispatcherOperation<TResult>` whose `.Task` can be awaited (HIGH), `Window.ShowDialog()` blocks the calling thread and returns a `Nullable<bool>` (HIGH), and `Button.IsCancel=true` causes Esc to fire the button's `Click` handler (HIGH). The pattern in D1 therefore composes correctly: a background-thread tool method awaits the dispatcher op, the dispatcher posts the modal on the UI thread, `ShowDialog` blocks the UI message pump but not the awaiting threadpool worker, and on close the dispatcher op completes with the modal's `IsAllowed` result.

**Primary recommendation:** Implement exactly per CONTEXT.md. Use `Application.Current?.Dispatcher.InvokeAsync(Func<bool>)`, treat `Application.Current == null` as deny-with-reason, store the `AlwaysAllow` set in a single static `AlwaysAllowSession.Instance` guarded by `lock`, append audit lines through a queue-fronted `AuditLogService` with `FileStream(Append, Write, Read)`, and extract the existing denylist into a single private method `ApplyDenylist(string cmdLower, int userLevel)` whose call site sits behind the `if (settings.ConfirmDangerousCommands == false && userLevel < 9)` gate AFTER the modal call.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|--------------|----------------|-----------|
| Show modal & capture user click | WPF UI thread (Application.Current.Dispatcher) | — | `ShowDialog` is a UI-thread-only operation per Microsoft Learn. |
| Decide allow/deny + return string to ReAct loop | Background tool thread (RunCommandTool.ExecuteAsync) | — | Tool result must flow back into the OpenAIService streaming loop; that loop runs off-UI. |
| AlwaysAllow set (read + write) | Background tool thread | UI thread (writes after modal close) | The check happens on the tool thread; the write happens on the UI thread inside the dispatcher callback. Both sides must be thread-safe. |
| Denylist enforcement | Background tool thread | — | Pure string check, no UI dependency. Only runs when modal returned Allow AND the gate condition (D8 OFF + level < 9) is true. |
| Audit log write | Threadpool (fire-and-forget) | — | Per D6: failures do not block the modal; outcome handler awaits nothing. |
| Settings read (ConfirmDangerousCommands, MessageCount/level) | Wherever needed | — | `SettingsService.LoadSettings()` is synchronous, returns a snapshot record. |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| .NET 8 WPF | 8.0 (`net8.0-windows10.0.19041.0`) | UI framework hosting `CommandConfirmationWindow` | Already pinned in `AIBWindows/AIB.csproj` line 5 [VERIFIED: codebase grep] |
| `System.Windows.Threading.Dispatcher` | bundled with WPF | Marshal calls onto the UI thread | Canonical WPF cross-thread mechanism [CITED: learn.microsoft.com/dotnet/api/system.windows.threading.dispatcher.invokeasync] |
| `System.Text.Json` | bundled with .NET 8 | Serialize audit log entries | Already used in `SettingsService.cs` line 5 — keep the dependency surface narrow [VERIFIED: codebase grep] |
| `System.IO.FileStream` | bundled | Append-only audit log writes | Standard append-mode pattern with `FileShare.Read` for concurrent readers [CITED: learn.microsoft.com/dotnet/api/system.io.filestream] |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `System.Collections.Generic.HashSet<string>` | bundled | Backing store for `AlwaysAllowSession` | Decision below picks this + `lock(_sync)` over `ConcurrentDictionary` (rationale in section 3 of guidance) |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `Dispatcher.InvokeAsync(Func<bool>)` returning a value | `TaskCompletionSource<bool>` set inside `Allow_Click`/`Deny_Click` | Manually wiring TCS works but couples the modal to async plumbing it does not need. D1 locks `InvokeAsync`. Documented here for context only. |
| `ConcurrentDictionary<string,byte>` for AlwaysAllow | `HashSet<string>` + `lock` | ConcurrentDictionary is more idiomatic for high-contention scenarios. AlwaysAllow is touched ≤ once per `run_command` call. Lock wins on simplicity. |
| `BlockingCollection<AuditEntry>` background writer | Fire-and-forget `Task.Run` per entry | A queue smooths out concurrency, but D6 ("fire-and-forget from the modal handler. Failures are logged to console but do not block the modal") matches the simpler pattern. We add a single `SemaphoreSlim(1,1)` around the write to serialize. |

**Installation:**
No new NuGet packages. Everything is in .NET 8 BCL or already declared in `AIB.csproj`.

**Version verification:** N/A — uses BCL only. Confirmed via Microsoft Learn pages dated 2025-07-01 (the API set is stable since .NET Framework 4.5 / WPF inception).

## Package Legitimacy Audit

No external packages introduced in this phase. Audit not required.

## Architecture Patterns

### System Architecture Diagram (RunCommandTool flow, post-fix)

```
ReAct loop (OpenAIService, threadpool)
       │
       ▼
RunCommandTool.ExecuteAsync(argsJson, userLevel)   ── background thread
       │
       ├─[1] Parse `command`. Bail if empty.
       │
       ├─[2] Build CommandConfirmationContext(tool, command, userLevel, cwd)
       │
       ├─[3] Check AlwaysAllowSession.Instance.Contains(command)?
       │        ├── HIT  → audit("always_allow") → goto [7]
       │        └── MISS → continue
       │
       ├─[4] Application.Current == null?
       │        ├── YES → audit("deny_no_ui") → return "Comando recusado (sem UI disponível)"
       │        └── NO  → continue
       │
       ├─[5] await App.Current.Dispatcher.InvokeAsync(() =>
       │         {
       │             var win = new CommandConfirmationWindow(ctx)
       │             { Owner = App.Current.MainWindow };
       │             return win.ShowDialog() == true
       │                 ? new ModalOutcome(win.IsAllowed, win.AlwaysAllow)
       │                 : new ModalOutcome(false, false);
       │         }).Task                              ── UI thread blocks; tool thread is parked
       │
       ├─[6] On outcome:
       │        Deny      → audit("deny")          → return "Comando recusado pelo usuário"
       │        Allow     → audit("allow")         → continue
       │        AlwaysAllow → audit("always_allow") + AlwaysAllowSession.Add(command) → continue
       │
       ├─[7] Denylist gate (D8 + D5):
       │        if (!settings.ConfirmDangerousCommands && userLevel < 9)
       │            ApplyDenylist(command, userLevel)   ── extracted method
       │        if denylist hit → return "ACESSO NEGADO (SANDBOX): ..."
       │
       └─[8] return await CommandService.ExecuteAsync(command, cwd)
```

The diagram shows two thread crossings: tool-thread → dispatcher (step 5 enter) and dispatcher → tool-thread (step 5 exit via `await`). `ShowDialog` blocks the UI message pump for the duration of the modal; the tool-thread `await` releases its threadpool worker. There is no nested dispatcher pumping problem because the tool method is never on the UI thread to begin with (the OpenAI streaming loop is a background flow).

### Recommended Project Structure (deltas only)

```
AIBWindows/
└── Services/
    ├── NativeTools.cs              # RunCommandTool gets rewritten body, denylist extracted
    ├── CommandService.cs           # ExecuteAsync gains optional `cwd` parameter (unchanged behavior)
    ├── CommandConfirmationContext.cs  # NEW — record type
    ├── AlwaysAllowSession.cs       # NEW — singleton with lock-guarded HashSet
    ├── AuditLogService.cs          # NEW — JSONL append-only writer
    └── SettingsService.cs          # field already declared at line 36; surface it (default true)
└── Views/
    ├── CommandConfirmationWindow.xaml      # add IsCancel="True" to Deny; add Grid rows for Tool/Nível/CWD
    └── CommandConfirmationWindow.xaml.cs   # add new ctor (CommandConfirmationContext); keep legacy ctor for compat
```

### Pattern 1: Dispatcher.InvokeAsync<T> awaited from a background thread

**What:** Call `Application.Current.Dispatcher.InvokeAsync(Func<TResult>).Task` from a non-UI thread to run a UI operation and get the result back asynchronously.

**When to use:** Any tool whose `ExecuteAsync` runs on the threadpool and needs a UI confirmation. `RunCommandTool`, and (Phase 3) `ExecuteSkillTool` and `MaterializeSkillTool`.

**Example:**
```csharp
// Source: https://learn.microsoft.com/dotnet/api/system.windows.threading.dispatcher.invokeasync
// Returns DispatcherOperation<TResult>; await its .Task property.
public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
{
    string command = ToolArgParser.Get(argumentsJson, "command");
    if (string.IsNullOrWhiteSpace(command)) return "ERRO: 'command' é obrigatório.";

    string cwd = DirectoryService.DataDir; // matches D3 + current CommandService default
    var ctx = new CommandConfirmationContext(
        Tool: "run_command",
        Command: command,
        UserLevel: userLevel,
        WorkingDirectory: cwd);

    // AlwaysAllow shortcut (D2).
    if (AlwaysAllowSession.Instance.Contains(command))
    {
        _ = AuditLogService.AppendAsync(AuditEntry.For(ctx, "always_allow", alwaysAllow: true));
        return await CommandService.ExecuteAsync(command, cwd);
    }

    // D1 reentrancy guard.
    var app = System.Windows.Application.Current;
    if (app == null)
    {
        _ = AuditLogService.AppendAsync(AuditEntry.For(ctx, "deny_no_ui", alwaysAllow: false));
        return "Comando recusado (sem UI disponível para confirmação).";
    }

    // Cross-thread modal call.
    var outcome = await app.Dispatcher.InvokeAsync(() =>
    {
        var win = new CommandConfirmationWindow(ctx)
        {
            Owner = app.MainWindow // null is OK; results in "modeless modal" feel, see pitfall
        };
        bool? result = win.ShowDialog();
        return new ModalOutcome(
            Allowed: result == true && win.IsAllowed,
            AlwaysAllow: win.AlwaysAllow);
    }).Task;

    if (!outcome.Allowed)
    {
        _ = AuditLogService.AppendAsync(AuditEntry.For(ctx, "deny", alwaysAllow: false));
        return "Comando recusado pelo usuário.";
    }

    if (outcome.AlwaysAllow) AlwaysAllowSession.Instance.Add(command);
    _ = AuditLogService.AppendAsync(AuditEntry.For(
        ctx, outcome.AlwaysAllow ? "always_allow" : "allow", outcome.AlwaysAllow));

    // D5 + D8: denylist gate sits AFTER the modal and only fires when settings opt out.
    var settings = new SettingsService().LoadSettings();
    if (!settings.ConfirmDangerousCommands && userLevel < 9)
    {
        string? denial = ApplyDenylist(command, userLevel);
        if (denial != null) return denial;
    }

    return await CommandService.ExecuteAsync(command, cwd);
}

private record ModalOutcome(bool Allowed, bool AlwaysAllow);
```

**Citations:** `Dispatcher.InvokeAsync<TResult>(Func<TResult>)` returns `DispatcherOperation<TResult>` per [Microsoft Learn — Dispatcher.InvokeAsync Method](https://learn.microsoft.com/dotnet/api/system.windows.threading.dispatcher.invokeasync). `Window.ShowDialog()` "opens a window and returns only when the newly opened window is closed" and returns `Nullable<bool>` per [Microsoft Learn — Window.ShowDialog Method](https://learn.microsoft.com/dotnet/api/system.windows.window.showdialog).

### Pattern 2: TaskCompletionSource alternative (context only, NOT chosen)

**What:** Wire `Allow_Click` and `Deny_Click` to complete a `TaskCompletionSource<bool>` instead of using `DialogResult`. The window exposes `Task<bool> Result`, the caller does `await win.Result`.

**Why we did NOT pick this:** D1 locks `Dispatcher.InvokeAsync`. The TCS pattern requires the caller to still post `win.Show()` (non-blocking) onto the dispatcher and to manage lifetime. Net code: more, not less. The InvokeAsync pattern delegates lifetime to `ShowDialog`. Documented here so a future contributor doesn't refactor toward TCS thinking it's cleaner — it isn't, given D1.

### Anti-Patterns to Avoid

- **`Dispatcher.Invoke` (sync) from the tool thread.** `Invoke` blocks the calling thread until the UI work completes. The tool runs on the threadpool; blocking a worker is wasteful but more importantly, if any code path within the ReAct loop ever runs the tool from the UI thread (defensive concern — should never happen, but `OpenAIService.StreamResponseAsync` is awaited from `ChatWindow` event handlers), `Invoke` deadlocks. `InvokeAsync().Task` does not.
- **`await Dispatcher.InvokeAsync(...)` without `.Task`.** `DispatcherOperation<T>` has a `GetAwaiter()` (verified via Microsoft Learn — the type is in `WindowsBase.dll`), so `await` syntactically compiles, but historically the awaitable returns `void`/`T` depending on overload. Always use `.Task` to get the `Task<T>` and a stable shape across .NET versions.
- **Calling `new CommandConfirmationWindow(...)` from the tool thread directly.** WPF `DispatcherObject` instances must be created on the thread that will own them. Construction inside the `InvokeAsync` lambda is correct; construction outside is a hard runtime error.
- **Re-using a closed modal.** `ShowDialog` throws `InvalidOperationException` "on a window that is closing or has been closed" per Microsoft Learn. Construct a fresh `CommandConfirmationWindow` per call.
- **Lazy/static modal instance.** Same reason as above plus reentrancy: two simultaneous tool calls would race on the same window.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Cross-thread UI marshalling | Custom synchronization with `ManualResetEvent` / `SemaphoreSlim` | `Application.Current.Dispatcher.InvokeAsync` | Canonical WPF mechanism; documented and battle-tested. |
| Modal result return | Custom event + flag on the Window | `DialogResult` + `ShowDialog`'s `bool?` return value | DialogResult drives the close lifecycle for free, including X-button → false. |
| Esc-to-cancel | Window-level `KeyDown` handler | `Button.IsCancel="True"` on Deny | Esc routes through `Click` automatically per Microsoft Learn. Less code, no event-tunnel bugs. |
| JSON line serialization | Manual string concatenation with `string.Format` | `JsonSerializer.Serialize(entry, options)` followed by `\n` append | Handles escaping, Unicode, control chars. Avoids audit-log injection where a command containing `\n"` would forge two lines. |
| Append-only file writes | Open/close per line via `File.AppendAllText` | Single `FileStream(FileMode.Append, FileAccess.Write, FileShare.Read)` guarded by `SemaphoreSlim` | Avoids file-handle thrash and gives a stable target for concurrent readers (e.g., a UAT-time `tail -f` analogue). |

**Key insight:** The "modal from background thread" problem in WPF is solved. The single most common failure is hand-rolling either the marshalling or the result-handshake when the platform already provides both. The whole RunCommandTool change is ~30 lines if and only if we use `InvokeAsync.Task` for marshalling and `DialogResult` for the handshake.

## Runtime State Inventory

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | `~/.AIB/profile.dat` already contains `ConfirmDangerousCommands` field (declared at SettingsService.cs:36 with default `true`). Existing user profiles serialized BEFORE this build will be missing the field — `JsonSerializer.Deserialize<UserAppSettings>` returns the C# default `true` per record initializer, so existing profiles auto-migrate. No data migration needed. | None — verified by reading `SettingsService.cs` line 36 and `JsonSerializer` default-value semantics. |
| Live service config | None — phase changes are entirely local to the AIB process. No external services (Ollama, OpenAI) touched. | None. |
| OS-registered state | None — no Task Scheduler / Services / pm2 changes. The Ctrl+Shift+Space global hotkey registered via NHotkey.Wpf (`App.xaml.cs` line 61) is unaffected. | None. |
| Secrets/env vars | None — phase does not touch `CredentialService`, `.env`, or DPAPI vault. | None. |
| Build artifacts | New file `AuditLogService.cs` will land in `AIBWindows/Services/`; new `CommandConfirmationContext.cs` and `AlwaysAllowSession.cs` same folder. After first build, `AIBWindows/obj/` will hold new `.g.cs` generated XAML partial (modified `CommandConfirmationWindow`). Standard `dotnet build` regenerates these. | None — incremental build handles it. If symptoms appear, `dotnet clean` is the canonical fix. |

**Audit log file** at `%APPDATA%/AIB/audit.log` (per D6): this is **created** by Phase 1, not migrated. No prior data exists. First write creates the file; subsequent writes append.

**Note on D6 path resolution:** D6 says `%APPDATA%/AIB/audit.log`. The codebase has TWO data roots: `Environment.SpecialFolder.ApplicationData` ("AppData/Roaming/AIB", legacy, referenced in `UserAppSettings.DataDir`) and `Environment.SpecialFolder.UserProfile` + ".AIB" (current, in `DirectoryService.DataDir`). D6's `%APPDATA%` literally maps to the legacy path; **but** the live system migrates from `%APPDATA%/AIB` to `~/.AIB` per `DirectoryService.EnsureDirectories()` lines 32-37. **Recommendation for plan:** put the audit log under `DirectoryService.LogsDir` (i.e., `~/.AIB/logs/audit.log`), and flag this as a deviation from D6's literal text for planner / user confirmation. The intent of D6 is "next to the encrypted profile"; `~/.AIB/logs/` matches that intent and avoids splitting data across two roots. `[ASSUMED — needs planner/user reconciliation with D6.]`

## Common Pitfalls

### Pitfall 1: Owner-less modal feels modeless
**What goes wrong:** `Application.Current.MainWindow` may be null if the user closed the main window but the tray icon kept the process alive (`ChatWindow.ToggleWindow()` hides; doesn't close, per the existing pattern in `App.xaml.cs` line 47). When MainWindow is null, the modal pops centered on screen with no owner, no parent in the taskbar dance, and feels disconnected.
**Why it happens:** WPF allows a null `Owner`. `ShowDialog` still works but loses parent-child window hierarchy, focus capture, and `WindowStartupLocation="CenterOwner"` (which is in the existing XAML at line 9) silently falls back to "CenterScreen" behavior — invisible regression.
**How to avoid:** If `Application.Current.MainWindow` is null, leave `Owner` unset (do NOT pass null explicitly into the property — the framework treats unset and null identically here, but defensive code is clearer). The modal still functions; it just centers on screen. Acceptable.
**Warning signs:** During UAT scenario 7 (no-UI case), if `Application.Current` is non-null but `MainWindow` is null, the dialog appears but `Owner == null`. Note this in VERIFICATION.md as expected behavior, not a bug.

### Pitfall 2: Deadlock if the tool ever runs on the UI thread
**What goes wrong:** The OpenAI streaming loop in `OpenAIService.StreamResponseAsync` (`OpenAIService.cs:240-502` per CONCERNS.md) is invoked from a `ChatWindow` button-click handler. Without `ConfigureAwait(false)`, the awaited continuation runs back on the UI thread. If the tool path ever resumes on the UI thread before reaching `Dispatcher.InvokeAsync`, calling `InvokeAsync(...).Task` and awaiting it is **still safe** — it does NOT deadlock — because `InvokeAsync` queues the work and the awaiter completes when the queued op runs. **However**, `ShowDialog` inside the queued lambda starts a nested message pump; if any code on the awaited continuation path holds a non-reentrant lock, dispatcher re-entry can hit it. This is a known WPF gotcha but not exercised by our codebase (no shared sync locks across UI / tool flows have been identified).
**Why it happens:** WPF dispatcher pumps messages during `ShowDialog`. Other timer events, mouse events, etc. fire while the modal is up.
**How to avoid:** Do not wrap the `InvokeAsync` call in any lock. The tool method does not hold any. Pass the `CommandConfirmationContext` by value (it's a `record`). Keep the lambda body short — construct, ShowDialog, return. No awaits inside the lambda.
**Warning signs:** UAT scenario 2 (Allow path) returning slowly; debugger paused inside `Dispatcher.PushFrame` for > 1s after click → suspect a re-entrant lock somewhere up the call stack.

### Pitfall 3: `DialogResult` set after close throws
**What goes wrong:** The current `CommandConfirmationWindow.xaml.cs:21-22` sets `DialogResult = true; Close();`. That order is correct. Reverse them (`Close(); DialogResult = true;`) and you get `InvalidOperationException` per the `Window.DialogResult` setter contract.
**Why it happens:** `Close` triggers the window's destruction; subsequent property writes throw.
**How to avoid:** Keep the existing order. When adding the new ctor that takes `CommandConfirmationContext`, do not refactor the click handlers.
**Warning signs:** Stack trace mentioning `set_DialogResult` after window close.

### Pitfall 4: `Application.Current` is null in unit tests AND in early startup
**What goes wrong:** D1 names "unit test or non-UI host". Two more cases: (1) `App.OnStartup` race — if some background warmup somehow calls `RunCommandTool.ExecuteAsync` before `App.OnStartup` finishes constructing `MainWindow`, `Application.Current` is non-null but `Current.MainWindow` is null. (2) After `Current.Shutdown()` triggered via tray "Sair" but before the process exits, `Current` may still be non-null while `MainWindow` is disposing. Both are edge cases; the guard `if (app == null)` catches only case (3) "no WPF host at all".
**Why it happens:** WPF `Application.Current` is set in `Application.OnStartup`'s base call (`base.OnStartup(e)` at line 28 of App.xaml.cs) but `MainWindow` is set later (`new ChatWindow()` at line 36 — note: assignment to `_chatWindow`, not to `App.MainWindow`; in this codebase `Application.MainWindow` is implicitly set when the first window's `Show` is called, which happens via `ChatWindow.ToggleWindow()`).
**How to avoid:** Recommended guard pattern:
```csharp
var app = System.Windows.Application.Current;
if (app?.Dispatcher == null)   // covers null app AND a torn-down dispatcher
{
    _ = AuditLogService.AppendAsync(AuditEntry.For(ctx, "deny_no_ui", alwaysAllow: false));
    return "Comando recusado (sem UI disponível para confirmação).";
}
```
**Warning signs:** Unit-test runner reporting `NullReferenceException` from `RunCommandTool.ExecuteAsync` — means the guard regressed.

### Pitfall 5: Audit log concurrent writes interleave
**What goes wrong:** If two `RunCommandTool` calls run concurrently (the OpenAI ReAct loop allows parallel tool calls per recent commit `9e0e00e feat(agent): tuning do ReAct loop, parallel tools, sanitizacao de streaming`), two fire-and-forget `AppendAsync` calls race on the file handle. Without serialization, lines can interleave mid-write → corrupt JSONL.
**Why it happens:** `FileStream` append-mode is atomic at the OS write granularity, but `JsonSerializer.Serialize` followed by stream `WriteAsync` does NOT correspond to a single syscall.
**How to avoid:** Single `SemaphoreSlim(1,1)` inside `AuditLogService` guarding the write. The whole "serialize + write + newline + flush" sequence runs inside `await _gate.WaitAsync()`.
**Warning signs:** `audit.log` containing lines like `{"ts":...{"ts":...` after UAT scenario with parallel calls.

### Pitfall 6: `IsCancel` on a templated button — verify Click still fires
**What goes wrong:** The Allow button uses `<Button.Template>` with a custom `ControlTemplate` (XAML lines 51-58). Adding `IsCancel="True"` to the Deny button (which has NO template) is safe. But future work that re-templates Deny must keep `ContentPresenter` and the Click event bubbling intact, else Esc may stop denying.
**Why it happens:** WPF routes Esc through `AccessKeyManager` to the button's invoke pipeline, which calls `OnClick`. Standard templates implement this correctly; custom templates that drop the default ControlTemplate root may break it.
**How to avoid:** Do not template the Deny button in Phase 1. If future phases redesign the modal, verify Esc still fires `Deny_Click` via a UAT scenario.
**Warning signs:** UAT scenario 6 (Esc on open modal) fails after a XAML refactor.

## Code Examples

### Common Operation 1: `CommandConfirmationContext` record + new ctor

```csharp
// Source: D3 (CONTEXT.md). New file: AIBWindows/Services/CommandConfirmationContext.cs
namespace AIB.Services;

public sealed record CommandConfirmationContext(
    string Tool,             // "run_command" today; "execute_skill" / "materialize_skill" in Phase 3
    string Command,          // full command text shown to user
    int UserLevel,           // current LevelService.GetLevel(settings.MessageCount)
    string WorkingDirectory  // cwd that CommandService will use
);
```

```csharp
// Source: D3. Updated file: AIBWindows/Views/CommandConfirmationWindow.xaml.cs
using System.Windows;
using AIB.Services;

namespace AIB.Views;

public partial class CommandConfirmationWindow : Window
{
    public bool IsAllowed { get; private set; }
    public bool AlwaysAllow { get; private set; }

    // New canonical ctor.
    public CommandConfirmationWindow(CommandConfirmationContext ctx)
    {
        InitializeComponent();
        CommandText.Text = ctx.Command;
        ToolText.Text = ctx.Tool;
        LevelText.Text = $"Nível: {ctx.UserLevel}/9";
        CwdText.Text = ctx.WorkingDirectory;
    }

    // Legacy ctor — KEEP for compat per D3. Removed in Phase 3.
    public CommandConfirmationWindow(string commandDescription)
        : this(new CommandConfirmationContext("run_command", commandDescription, 1, ""))
    { }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        IsAllowed = true;
        AlwaysAllow = AlwaysAllowCheckBox.IsChecked ?? false;
        DialogResult = true; // must precede Close()
        Close();
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        IsAllowed = false;
        DialogResult = false; // must precede Close()
        Close();
    }
}
```

XAML deltas (D4 + D3 — illustrative diff):
```xml
<!-- Existing single-line CommandText replaced by a small grid -->
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/> <!-- Tool name -->
        <RowDefinition Height="Auto"/> <!-- Command text (existing) -->
        <RowDefinition Height="Auto"/> <!-- Level -->
        <RowDefinition Height="Auto"/> <!-- CWD -->
    </Grid.RowDefinitions>
    <TextBlock Grid.Row="0" Foreground="#888" FontSize="11">
        Tool: <Run x:Name="ToolText" Foreground="#DDD"/>
    </TextBlock>
    <Border Grid.Row="1" Background="#1AFFFFFF" Padding="10" CornerRadius="6"
            BorderBrush="#2A2A30" BorderThickness="1" Margin="0,4,0,0">
        <TextBlock x:Name="CommandText" Text="git status" Foreground="White"
                   TextWrapping="Wrap" FontFamily="Consolas" FontSize="13"/>
    </Border>
    <TextBlock Grid.Row="2" Foreground="#888" FontSize="11" Margin="0,8,0,0">
        <Run x:Name="LevelText"/>
    </TextBlock>
    <TextBlock Grid.Row="3" Foreground="#888" FontSize="11" Margin="0,4,0,0">
        CWD: <Run x:Name="CwdText" Foreground="#BBB" FontFamily="Consolas"/>
    </TextBlock>
</Grid>

<!-- Deny button gains IsCancel=True (D4) -->
<Button Content="Recusar" Click="Deny_Click" IsCancel="True"
        Width="80" Height="35" Background="Transparent"
        Foreground="#AAA" BorderThickness="0" Cursor="Hand" Margin="0,0,10,0"/>
```

### Common Operation 2: `AlwaysAllowSession` singleton

```csharp
// New file: AIBWindows/Services/AlwaysAllowSession.cs
// D2: session-only, in-memory, exact-string match, no normalization.
using System.Collections.Generic;

namespace AIB.Services;

public sealed class AlwaysAllowSession
{
    public static AlwaysAllowSession Instance { get; } = new();

    private readonly HashSet<string> _set = new();
    private readonly object _sync = new();

    private AlwaysAllowSession() { }

    public bool Contains(string command)
    {
        lock (_sync) return _set.Contains(command);
    }

    public void Add(string command)
    {
        lock (_sync) _set.Add(command);
    }

    // No Clear() method: process exit clears (per D2).
    // No Remove() in v1: would surface a security-relevant UX decision we have not made.
}
```

**Rationale for `HashSet<string>` + `lock` over `ConcurrentDictionary<string,byte>`:**
- The set is touched at most twice per `run_command` call: once at `Contains` on the tool thread, once at `Add` on the UI thread inside the dispatcher callback after a confirmed AlwaysAllow click. Concurrency level: low.
- Reads and writes are individually cheap (string hash). Lock contention is a non-issue at < 10 ops/sec realistic.
- `ConcurrentDictionary` carries 8 internal locks (default `concurrencyLevel`) and per-operation atomic ceremony; overkill here.
- `HashSet<string>` + a single `object _sync` is 12 lines, trivially auditable, no surprises around `TryAdd` vs `Add` semantics.
- If Phase 3 introduces high-throughput skill execution (unlikely — every call still triggers a modal), revisit. For Phase 1: lock wins.

### Common Operation 3: `AuditLogService` (JSONL append)

```csharp
// New file: AIBWindows/Services/AuditLogService.cs
// D6: %APPDATA%/AIB/audit.log (see "Runtime State Inventory" note — recommend ~/.AIB/logs/audit.log).
// Fire-and-forget from caller. Failures log to Console, never throw.
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIB.Services;

public sealed record AuditEntry(
    [property: JsonPropertyName("ts")] string Timestamp,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("cmd")] string Command,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("outcome")] string Outcome,         // allow|deny|always_allow|deny_no_ui
    [property: JsonPropertyName("always_allow")] bool AlwaysAllow)
{
    public static AuditEntry For(CommandConfirmationContext ctx, string outcome, bool alwaysAllow)
        => new(
            Timestamp:   DateTime.UtcNow.ToString("o"),  // ISO 8601 with offset
            Tool:        ctx.Tool,
            Command:     ctx.Command,
            Level:       ctx.UserLevel,
            Cwd:         ctx.WorkingDirectory,
            Outcome:     outcome,
            AlwaysAllow: alwaysAllow);
}

public static class AuditLogService
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        // UnsafeRelaxedJsonEscaping keeps Unicode literal but still escapes control chars.
        // The command field is the only attacker-controlled input; JsonSerializer escapes \n, \r, \", \\.
    };

    public static async Task AppendAsync(AuditEntry entry)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            string line = JsonSerializer.Serialize(entry, _opts);
            string path = Path.Combine(DirectoryService.LogsDir, "audit.log");
            Directory.CreateDirectory(DirectoryService.LogsDir);

            // FileMode.Append guarantees seek-to-end on the kernel side; FileShare.Read allows
            // tail-style observation (UAT). Buffer size 1 forces flush-per-write.
            await using var fs = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            await fs.WriteAsync(payload).ConfigureAwait(false);
            await fs.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIT] write failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

**JSONL safety notes:**
- `JsonSerializer.Serialize` always produces single-line JSON when `WriteIndented = false`. The `\n` we append is the only newline in the produced bytes — no risk of split lines mid-record.
- `Command` is the only field where an attacker controls bytes; `JsonSerializer` escapes the dangerous set (`\n`, `\r`, `"`, `\\`, control chars). A command like `"\"X\"\nfake_entry"` becomes `"\"\\\"X\\\"\\nfake_entry"` inside the `cmd` JSON value — cannot forge a second line.
- No log rotation in v1 (CONTEXT.md "Open questions deferred"). Audit.log grows unbounded; acceptable for the verification window.

### Common Operation 4: Denylist extraction (Level-9 refactor)

```csharp
// AIBWindows/Services/NativeTools.cs — extract method from current 320-355 block.
// Pattern: Extract Method refactor (Fowler). Same observable behavior; same regexes;
// only difference is the wrapping `if (userLevel < 9)` is hoisted to the call site.

// Returns null when allowed, or a denial message when blocked.
private static string? ApplyDenylist(string command, int userLevel)
{
    string cmdLower = command.ToLowerInvariant();

    string[] sysDirs = { "appdata", "windows", "program files", "programdata" };
    if (sysDirs.Any(d => ContainsWord(cmdLower, d)))
        return "ACESSO NEGADO (SANDBOX): Diretórios de sistema protegidos.";

    if (userLevel <= 4)
    {
        if (cmdLower.Contains("c:\\") || cmdLower.Contains("d:\\"))
        {
            bool allow = false;
            if (ContainsWord(cmdLower, "documents") || ContainsWord(cmdLower, "documentos")) allow = true;
            if (userLevel >= 3 && ContainsWord(cmdLower, "downloads")) allow = true;
            if (!allow) return $"ACESSO NEGADO (SANDBOX): Nível {userLevel} restrito à Documentos/Downloads.";
        }
        if (userLevel <= 2 && (ContainsWord(cmdLower, "ls") || ContainsWord(cmdLower, "dir")))
            return "ACESSO NEGADO (SANDBOX): Listagem em massa bloqueada no Nível 2.";
    }

    if (userLevel < 8)
    {
        string[] destructives = { "rm", "del", "erase", "remove-item", "ri", "out-file",
                                  "set-content", "add-content", "new-item", ">", ">>",
                                  "mkdir", "md", "rmdir", "rd", "format" };
        if (destructives.Any(b => ContainsWord(cmdLower, b)))
            return "ACESSO NEGADO (SANDBOX): Comandos de gravação/exclusão requerem Nível 8.";
    }

    if (userLevel < 7)
    {
        string[] netCmds = { "curl", "wget", "invoke-webrequest", "iwr", "invoke-restmethod",
                             "irm", "ping", "tracert", "nslookup", "ftp", "scp", "ssh" };
        if (netCmds.Any(b => ContainsWord(cmdLower, b)))
            return "ACESSO NEGADO (SANDBOX): Comandos de rede requerem Nível 7.";
    }

    return null; // allowed
}
```

**Refactor pattern:** This is a textbook *Extract Method* (Fowler, *Refactoring*) with one structural change: the outer `if (userLevel < 9)` from the original is removed from the extracted method body — that gating is now expressed at the *call site* (D5 + D8). Internal level gates (`<= 4`, `< 7`, `< 8`) are preserved verbatim.

**Behavior preservation proof sketch:**
- For `userLevel == 9` AND old code path (`if (userLevel < 9)` false) → no denylist ran → returned to `CommandService.ExecuteAsync`. New code path: `ApplyDenylist` is never called (gate `userLevel < 9` false at call site) → returns to `CommandService.ExecuteAsync`. **Same.**
- For `userLevel < 9` AND old code path (denylist runs) → returns denial OR falls through. New code path: assuming `!settings.ConfirmDangerousCommands` (D8 default ON means the new path skips denylist entirely — **change in observable behavior**, but D8 documents this is intentional; modal already gated the call).
- For `userLevel < 9` AND `settings.ConfirmDangerousCommands == false` (D8 OFF) → new path runs `ApplyDenylist` post-modal → **same denial logic as old code**.

The behavior change is bounded to "denylist no longer fires for non-dangerous commands when the user explicitly opts out via D8 OFF". Per CONTEXT.md and SEC-02, this is the intended Level-9 semantic generalized to all levels.

### Common Operation 5: `CommandService` cwd parameter

```csharp
// AIBWindows/Services/CommandService.cs — existing signature already accepts workDir.
// Already at line 10: ExecuteAsync(string command, string? workDir = null, int timeoutMs = 20000)
// No change required. Pass DirectoryService.DataDir from RunCommandTool (matches current behavior at NativeTools.cs:357).
```

**Important:** The signature change in CONTEXT.md files-in-scope table ("accepts an optional `cwd` parameter for the modal text") is already satisfied by the existing API. Verified by reading `CommandService.cs:10`. No edit needed beyond passing `cwd` through `CommandConfirmationContext` to the modal.

### Common Operation 6: Closing CONCERNS.md findings

D-of-D item: "CONCERNS.md SEC-01 + SEC-02 entries moved to a 'Resolved' section with this phase's last commit SHA."

**Protocol:**
1. After the phase's last commit lands, capture the SHA: `git rev-parse HEAD`.
2. Edit `.planning/codebase/CONCERNS.md`:
   - Insert a new top-level section after the title block, BEFORE "## Security Considerations":
     ```markdown
     ## Resolved

     ### SEC-01 — Declared "Zero-Trust modal confirmation" not implemented for shell execution
     **Resolved in:** Phase 01 (`01_modal-and-level9`) — commit `<SHA>`
     **Original section:** Security Considerations (below). Original text preserved for audit.
     **Resolution:** `RunCommandTool.ExecuteAsync` now routes through `CommandConfirmationWindow`
     before any call to `CommandService.ExecuteAsync`. AlwaysAllow scoped to process lifetime.
     Audit log at `~/.AIB/logs/audit.log`. Verified by UAT scenarios 1–8 in
     `.planning/phases/01_modal-and-level9/VERIFICATION.md`.

     ### SEC-02 — `RunCommandTool` sandbox bypassed at user Level 9 entirely
     **Resolved in:** Phase 01 — commit `<SHA>`
     **Resolution:** Modal confirmation now fires at every level. Level 9 disables the denylist
     only; Level-9 destructive verbs reach the modal for human approval. Denylist guard
     extracted into `RunCommandTool.ApplyDenylist`. Verified by UAT scenario 5.
     ```
   - Leave the original CRITICAL entries in `## Security Considerations` UNCHANGED so audit history is intact. The "Resolved" section is purely additive and points back to them.
3. Commit the CONCERNS.md change as the final atomic commit of Phase 1 (per NFR-02). Suggested message: `docs(security): close SEC-01 + SEC-02 in CONCERNS.md, refs phase 01`.

**Resulting file shape (top-of-file):**
```
# Codebase Concerns
... preamble (lines 1-11) ...

## Resolved

### SEC-01 — Declared "Zero-Trust modal confirmation" not implemented for shell execution
(metadata block above)

### SEC-02 — RunCommandTool sandbox bypassed at user Level 9 entirely
(metadata block above)

## Security Considerations

### CRITICAL — Declared "Zero-Trust modal confirmation" is not implemented ... (original, untouched)
... rest of file unchanged ...
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `Dispatcher.BeginInvoke` returning `DispatcherOperation` (no Task) | `Dispatcher.InvokeAsync` returning `DispatcherOperation<T>` with `.Task` | .NET Framework 4.5 (2012) | `await` works natively, no manual completion handshake. |
| `TaskCompletionSource<bool>` for modal results | `bool? ShowDialog()` plus existing `IsAllowed` property | Always available in WPF | Less code; framework handles lifetime. |
| `File.AppendAllText` per line | `FileStream(FileMode.Append, FileShare.Read)` with explicit `FlushAsync` | .NET Core 2.0+ has `useAsync: true` for true async I/O | Avoids handle churn; concurrent readers work. |

**Deprecated/outdated:**
- `Dispatcher.Invoke` (synchronous) for tool-thread → UI: use `InvokeAsync` instead.
- `ProtectedData` for the audit log (D6 file is intentionally plaintext for UAT inspection; not deprecated, just not appropriate here).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Audit log should live at `~/.AIB/logs/audit.log` rather than literal `%APPDATA%/AIB/audit.log` from D6 text | Runtime State Inventory + Code Examples #3 | If user intended literal `%APPDATA%`, logs end up in the legacy migration path which `DirectoryService.EnsureDirectories()` copies-from-then-leaves-alone. Symptom: confusion about where to find logs. Mitigation: planner surfaces this choice. |
| A2 | `Application.Current.MainWindow` may be null at runtime even when `Application.Current` is not null (after `ChatWindow.ToggleWindow()` hides the window) | Pitfall 1 + Pattern 1 code sketch | If MainWindow is reliably non-null, the `Owner = null` fallback is dead code. No functional risk. |
| A3 | The recent commit `9e0e00e feat(agent): tuning do ReAct loop, parallel tools` enables actual concurrent `run_command` calls (vs sequential ReAct iterations) | Pitfall 5 + AuditLogService SemaphoreSlim design | If tools are still serialized by the FSM, the semaphore is unnecessary but adds no measurable overhead. Safe assumption. |
| A4 | The `ConfirmDangerousCommands` field at SettingsService.cs:36 is wired to the SAME `profile.dat` serialization that adds default values for missing fields on legacy profiles | Runtime State Inventory | If `JsonSerializer.Deserialize` does NOT default the field (e.g., custom converter), legacy profiles deserialize with the C# class-default `true`, which is identical → no observable problem. Risk only if a future migration changes default. |

**If this table is empty:** Not empty. Four assumptions tagged for planner consideration; A1 is the only one that may need user input.

## Open Questions

1. **Audit log path: `%APPDATA%/AIB/audit.log` (D6 literal) vs `~/.AIB/logs/audit.log` (codebase convention)?** — **RESOLVED 2026-05-28**: `~/.AIB/logs/audit.log` (matches codebase root via `DirectoryService`; `logs/` subdir created lazily). CONTEXT.md D6 updated to reflect this.

2. **Should the legacy ctor `CommandConfirmationWindow(string)` actually be kept?** — **RESOLVED 2026-05-28**: Drop entirely. Zero call sites today; no compat surface. CONTEXT.md D3 + file scope row updated.

3. **Should `RunCommandTool.RequiredLevel` stay at 2 once the modal is wired?**
   - What we know: Current value is 2 (NativeTools.cs:285). The modal is now the safety mechanism; the level check becomes a coarse pre-filter.
   - What's unclear: whether the user wants `run_command` accessible at Level 1 (modal-gated) or to keep the Level-2 floor.
   - Recommendation: out of scope for Phase 1 (would conflict with D-of-D "No regression to existing tools"). Document for future tuning.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | `dotnet build`, `dotnet run` | ✓ | 8.x (csproj line 5) | — |
| Windows 10 build 19041+ | WPF + WinRT projections | ✓ | Per STACK.md | — |
| `~/.AIB/` writable | AuditLogService + SettingsService | ✓ | Existing (created in App.OnStartup) | — |

**No external services touched** (no Ollama, no OpenAI). Phase is self-contained.

## Validation Architecture

`workflow.nyquist_validation` not present in `.planning/config.json` (file not located via this research session). Treating as default = enabled per protocol. NFR-01 explicitly states "no test suite required to ship this milestone" and D7 declares verification = manual UAT recorded in `VERIFICATION.md`. This is a documented exception, not an absent decision. The Validation Architecture table below reflects the manual-UAT reality.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | None (manual UAT per NFR-01 + D7) |
| Config file | none |
| Quick run command | n/a — UAT scenarios run in `dotnet run` debug session |
| Full suite command | `dotnet build` (compiles all new files) + 8-scenario UAT walkthrough |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| SEC-01 | Modal appears before CommandService.ExecuteAsync | manual-only | UAT scenarios 1–4 in `01_modal-and-level9/VERIFICATION.md` | ❌ Wave 0 (creates VERIFICATION.md) |
| SEC-02 | Level 9 routes through modal; denylist relaxed | manual-only | UAT scenario 5 | ❌ Wave 0 |
| D1 (no-UI guard) | `Application.Current==null` returns deny | manual-only (debug harness) | UAT scenario 7 | ❌ Wave 0 |
| D4 (Esc) | Esc denies | manual-only | UAT scenario 6 | ❌ Wave 0 |
| D6 (audit) | 8 entries land in audit.log | manual-only | UAT scenario 8 | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet build` (must succeed; warnings allowed but reviewed).
- **Per wave merge:** Full UAT walkthrough scenarios 1–8.
- **Phase gate:** `VERIFICATION.md` lists all 8 with PASS/FAIL/checked-by signature before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `.planning/phases/01_modal-and-level9/VERIFICATION.md` — UAT scenario template with 8 sections from D7
- [ ] No new framework install required (manual-UAT only)

## Security Domain

`security_enforcement` not detected in `.planning/config.json` (file not located). Treating as default = enabled.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Phase touches no auth surface. |
| V3 Session Management | no | No session concept beyond `AlwaysAllowSession` (in-memory, process-scoped). |
| V4 Access Control | **yes** | Modal confirmation IS the access-control gate. Level-gated `RequiredLevel` on tool stays. |
| V5 Input Validation | **yes** | `Command` field flows from LLM → JSON serializer → cmd.exe. JSON escaping prevents audit-log injection; cmd.exe is the still-unsafe shell (SEC-04, deferred to Phase 3). |
| V6 Cryptography | no | Audit log intentionally plaintext (D6). DPAPI continues to wrap `profile.dat` unchanged. |
| V7 Errors & Logging | **yes** | Audit log is the new control. JSONL ensures structured, append-only, per-record integrity. Failure to write is logged to Console but does NOT crash the modal (D6). |
| V11 Business Logic | **yes** | "Modal fires regardless of level" (D5) is a business-logic invariant. |

### Known Threat Patterns for WPF + cmd.exe

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| LLM-emitted destructive cmd via prompt-injection | Tampering / Elevation | Modal confirmation (this phase). Out-of-band: SEC-07 wraps tool inputs (Phase 4). |
| Audit-log line injection (command containing `\n`) | Tampering | `JsonSerializer.Serialize` escapes control chars; one line per record guaranteed by `WriteIndented=false`. |
| Concurrent write corruption | Tampering | `SemaphoreSlim(1,1)` in `AuditLogService.AppendAsync`. |
| Race between AlwaysAllow set and modal close | Tampering | `lock(_sync)` on both `Contains` and `Add`. |
| `cmd.exe /c {command}` argument injection | Tampering | NOT mitigated in this phase (deferred to SEC-04 / Phase 3). Phase 1 surfaces the raw command to the user in the modal so they see what will execute. |
| Re-entrant dispatcher pump while modal is open causing UI deadlock | DoS | No shared locks on the tool-call path; lambda body inside `InvokeAsync` is short and self-contained. |
| Setting `Application.Current = null` mid-call | Tampering / DoS | Single-read into local `var app` at start of method; subsequent code uses `app` not `Application.Current`. |

## Sources

### Primary (HIGH confidence)
- [Microsoft Learn — Dispatcher.InvokeAsync Method](https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatcher.invokeasync) — confirms `InvokeAsync<TResult>(Func<TResult>)` returns `DispatcherOperation<TResult>` whose `.Task` is awaitable; exceptions are stored in the returned task and re-thrown on await. Dated 2025-07-01 (last updated).
- [Microsoft Learn — Window.ShowDialog Method](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.showdialog) — confirms blocking semantics, `bool?` return mirroring `DialogResult`, `InvalidOperationException` when called on a closing/closed window, and that `Owner` must be set for UI Automation correctness. Dated 2025-07-01.
- [Microsoft Learn — Button.IsCancel Property](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.button.iscancel) — confirms Esc fires the button's `Click` handler via `AccessKeyManager`. Dated 2025-07-01.
- Direct codebase reads (HIGH for codebase facts):
  - `AIBWindows/Services/NativeTools.cs:281-359` (RunCommandTool body)
  - `AIBWindows/Services/CommandService.cs:1-73` (existing ExecuteAsync signature)
  - `AIBWindows/Views/CommandConfirmationWindow.xaml` + `.xaml.cs` (existing modal shape)
  - `AIBWindows/Services/SettingsService.cs:36` (ConfirmDangerousCommands declared, default true)
  - `AIBWindows/Services/DirectoryService.cs:1-78` (data paths and migration logic)
  - `AIBWindows/Services/LevelService.cs:1-47` (level thresholds — Level 9 at MessageCount ≥ 1500)
  - `AIBWindows/App.xaml.cs:1-94` (Application startup; MainWindow lifecycle)

### Secondary (MEDIUM confidence)
- Fowler's *Refactoring*, Extract Method — applied to denylist extraction. No URL needed; canonical refactor pattern.

### Tertiary (LOW confidence)
- None. All claims rest on either Microsoft Learn or direct codebase reads.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — every API confirmed against Microsoft Learn.
- Architecture: HIGH — pattern matches D1 verbatim and is the documented WPF idiom.
- Pitfalls: MEDIUM-HIGH — Pitfalls 1, 3, 5, 6 are direct from Microsoft Learn or codebase facts; Pitfall 2 (deadlock) is a defensible analysis but not a current bug.
- Audit log layout: MEDIUM — code is correct; the path question (A1) needs user confirmation.

**Research date:** 2026-05-28
**Valid until:** 2026-06-28 (the .NET 8 WPF API surface is stable; revisit only if .NET 9 migration is on the table).
