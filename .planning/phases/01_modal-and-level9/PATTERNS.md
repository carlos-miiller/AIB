# Phase 1: modal-and-level9 — Pattern Map

**Mapped:** 2026-05-28
**Files in scope (per CONTEXT.md):** 8 (5 new, 3 modified)
**Analogs found:** 7 / 8 (one explicit "no analog" for the Dispatcher-bound modal pattern from a tool)

---

## File Classification

| New / Modified file | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `Services/CommandConfirmationContext.cs` (NEW) | POCO / data carrier | in-memory ctor → consumed | `Services/ShadowHistoryService.cs:10` (`ShadowSuggestion`) | exact (immutable POCO with `{ get; init; }`) |
| `Services/AlwaysAllowSession.cs` (NEW) | static service, process-lifetime in-memory state | HashSet add/contains, no persistence | `Services/ShadowHistoryService.cs:24` (`ShadowHistoryService` static class with collection) + `Services/ContextService.cs:33` | exact-role match (static-class + collection state) |
| `Services/AuditLogService.cs` (NEW) | static service, append-only file I/O | one-line JSONL append per call (write-only) | `Services/ReminderService.cs:68` (`SaveReminders`) + `Services/ChatHistoryService.cs:36` (`SaveHistory`) | role-match (JSON-to-file, full-rewrite — JSONL append is new) |
| `Services/NativeTools.cs` `RunCommandTool` (line 281, MODIFY) | `ITool`, request-response | call into UI Dispatcher then resume on threadpool | `Services/NativeTools.cs:535` (`ManageClipboardTool.ExecuteAsync`, uses `Dispatcher.InvokeAsync` from inside a tool) | **role+flow exact** (only existing `ITool` that crosses to the UI thread) |
| `Views/CommandConfirmationWindow.xaml` (MODIFY) | WPF Window XAML | declarative layout, two new rows + `IsCancel` | itself, plus `Views/SettingsWindow.xaml` for row-stack pattern | direct edit of the same file |
| `Views/CommandConfirmationWindow.xaml.cs` (MODIFY) | WPF Window code-behind, dialog | ctor takes context object | `Views/SettingsWindow.xaml.cs:8` + this same file's existing ctor at line 11 | direct edit of the same file |
| `Services/CommandService.cs` (MODIFY) | static service wrapping cmd.exe | request-response, add optional `cwd` arg | itself — signature already has `string? workDir = null` at line 10 | trivial (parameter already exists) |
| `Services/SettingsService.cs` (MODIFY) | DPAPI-encrypted settings | property already declared at line 36 | itself — wire `ConfirmDangerousCommands` consumer (no struct change) | trivial (field exists, only consumption is new) |

---

## Pattern Assignments

### `Services/CommandConfirmationContext.cs` (NEW — POCO, data carrier)

**Analog:** `AIBWindows/Services/ShadowHistoryService.cs:10-15` (`ShadowSuggestion`)

**Why:** Same role — an immutable, ctor-populated data carrier passed across a method boundary. `ShadowSuggestion` is the only POCO in the codebase that uses `{ get; init; }` (every other data class uses `{ get; set; }`). For a context that flows tool → window and is never mutated, `init` matches intent.

**Copy:**
- File-scoped namespace `namespace AIB.Services;` (matches the project's dominant style; CONVENTIONS.md lists `ReminderService.cs` and `ContextService.cs` as legacy block-namespace holdouts — new files use file-scoped).
- XML `///` summary on the public type (CONVENTIONS.md notes most service classes have one — see ShadowSuggestion comment block at `ShadowHistoryService.cs:6-9`).
- **No `record` keyword.** Zero `record` types exist in `AIBWindows/Services/`. Use `public class CommandConfirmationContext` with `{ get; init; }` properties. Per CONTEXT.md D3 the fields are: tool name (string), command text (string), level (int), cwd (string).
- Default values on every string property (`= ""`) — matches `SkillMetadata` (`SkillService.cs:12-14`) and `ContextFile` (`ContextService.cs:10`). Avoids `NullableReference` warnings under `<Nullable>enable</Nullable>` (csproj line 6-7).
- One public type per file (CONVENTIONS.md naming rule; exception is `ShadowHistoryService.cs` which intentionally pairs the POCO with its service).

---

### `Services/AlwaysAllowSession.cs` (NEW — static singleton holding `HashSet<string>`)

**Analog:** `AIBWindows/Services/ShadowHistoryService.cs:24-63` (in-memory bounded collection, static class, dispatcher-aware) + `AIBWindows/Services/ContextService.cs:33-74` (static class owning `ObservableCollection<>` state for the process lifetime).

**Why:** Both are the only Services that hold mutable process-lifetime in-memory state with no persistence (CONTEXT.md D2 explicitly says session-only, cleared on app exit). `ShadowHistoryService` is the closer analog because it is also a "log" of strings rather than a UI-bound collection.

**Copy:**
- `public static class AlwaysAllowSession` — static class, no constructor.
- `private static readonly HashSet<string> _allowed = new();` — underscore-prefix camelCase per CONVENTIONS.md ("Private fields prefixed with underscore"). Use the default `EqualityComparer` since CONTEXT.md D2 says **exact string match (no normalization)** — do NOT pass `StringComparer.OrdinalIgnoreCase`.
- Public surface: `public static bool Contains(string command)` and `public static void Add(string command)` — mirrors the small public API of `ShadowHistoryService.Add` (`ShadowHistoryService.cs:34`).
- **No Dispatcher marshaling.** `ShadowHistoryService` only marshals because it mutates an `ObservableCollection<>` bound to the UI. A plain `HashSet<string>` does not need the UI thread; do not import the dispatcher pattern for it. Calls come from `RunCommandTool` on a background thread, so keep it free-threaded — but if write races are a concern, use `lock (_allowed)` around add/contains. None of the existing static services use locks today, so default to lock-free unless the planner requires otherwise.
- **No persistence.** Match `ShadowHistoryService` (comment at `ShadowHistoryService.cs:21-23` explicitly says "Não persiste em disco — quando o app fecha, o histórico zera"). Replicate that comment style.

**Wiring (no-DI Composition Root):** CONVENTIONS.md rule #1 + the divergence note ("App.xaml.cs:36 also `new`s `ChatWindow()` — the real root is `App.OnStartup`") confirms there is no DI container. Because `AlwaysAllowSession` is a **static** class, it needs no wiring at all — `RunCommandTool` references it directly as `AlwaysAllowSession.Contains(...)` / `.Add(...)`, exactly like `RunCommandTool` already references `LevelService.GetLevel`, `DirectoryService.DataDir`, and `CommandService.ExecuteAsync` (`NativeTools.cs:357`). No constructor injection, no field on `ChatWindow`. This is the project's idiom for stateless or process-singleton services.

---

### `Services/AuditLogService.cs` (NEW — append-only JSONL writer to `%APPDATA%/AIB/audit.log`)

**Analog (primary):** `AIBWindows/Services/ReminderService.cs:68-80` (`SaveReminders`) — JSON-to-disk under `%APPDATA%\AIB\`, swallows exceptions, ensures parent dir with `Directory.CreateDirectory`.
**Analog (path convention):** `AIBWindows/Services/DirectoryService.cs:8` shows `~/.AIB` is the new root, but `ReminderService.cs:36` and `ChatHistoryService.cs:20` deliberately keep their files at `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\AIB"`. CONTEXT.md D6 specifies `%APPDATA%/AIB/audit.log` — copy the `ReminderService` path expression verbatim, **not** `DirectoryService.DataDir` (which is `~/.AIB`). This is consistent with the existing `%AppData%\AIB\` convention for sibling log-style files.

**Why:** `ReminderService` is the canonical "static class, JSON file under %AppData%\AIB, try/catch swallow on write" template. `ChatHistoryService.SaveHistory` (`ChatHistoryService.cs:36-53`) is a slightly older block-namespace variant of the same pattern.

**Copy:**
- File-scoped namespace `namespace AIB.Services;` (match `ReminderService.cs:6`, not the block style in `ChatHistoryService.cs:8`).
- `public static class AuditLogService` + `private static readonly string FilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIB", "audit.log");` — direct copy from `ReminderService.cs:36`.
- **Append, not rewrite.** This is the one place the analog diverges. Existing services rewrite the whole file (`File.WriteAllText`). For JSONL append-only (CONTEXT.md D6), use `await File.AppendAllTextAsync(FilePath, line + "\n", Encoding.UTF8)`. The newline is `"\n"` not `Environment.NewLine` — JSONL is LF-only by spec.
- `AppendAsync(entry)` method signature per CONTEXT.md D6. The `entry` is a small POCO (or anonymous object) with fields `ts`, `tool`, `cmd`, `level`, `cwd`, `outcome`, `always_allow`. Serialize with `System.Text.Json.JsonSerializer.Serialize(entry)` — match `ReminderService.cs:76` (`JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })`) BUT drop `WriteIndented = true` because JSONL must be one record per line.
- **Error handling.** CONTEXT.md D6 says "Failures are logged to console but do not block the modal." This is a slight deviation from the dominant "empty `catch { }`" pattern (CONVENTIONS.md lists 15 occurrences of swallowed catch). Instead, copy the `[HOTKEY]` tagged log style from `App.xaml.cs:69`: `Console.WriteLine($"[AUDIT] Falha ao gravar audit.log: {ex.Message}");` Use a new bracket tag (`[AUDIT]`) consistent with CONVENTIONS.md's "structured prefixes in brackets" convention.
- **Fire-and-forget.** CONTEXT.md D6 says "fire-and-forget from the modal handler." The caller in `RunCommandTool` should do `_ = AuditLogService.AppendAsync(entry);` — matches the discard pattern used at `ChatWindow.xaml.cs:103` (`_ = InitVoiceAsync();`).
- Ensure parent directory exists per `ReminderService.cs:72-73`:
  ```csharp
  var dir = System.IO.Path.GetDirectoryName(FilePath);
  if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir!);
  ```
  Do this once on first append (cheap idempotent) or in a static ctor; `ReminderService` does it on every save and that is acceptable.

---

### `Services/NativeTools.cs` — modified `RunCommandTool` (line 281, modal wiring)

**Analog — EXACT MATCH FOUND.** Contrary to a first guess, the codebase already has a tool that hops to the UI thread via `Dispatcher.InvokeAsync`: `ManageClipboardTool.ExecuteAsync` at `AIBWindows/Services/NativeTools.cs:535-572`.

**Why:** This is the only `ITool` in the codebase that calls `System.Windows.Application.Current.Dispatcher.InvokeAsync(...)` from background → UI and awaits the result. Same data flow as the new modal call: async tool → marshal to UI thread → return result.

**Copy (the exact shape from `NativeTools.cs:547-563`):**
```csharp
// Background → UI marshal pattern, ExecuteAsync style:
await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
{
    // UI-thread-only work here
});

// Or returning a value:
return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
{
    // ... do UI-thread work, return value
});
```

**Adapt for the modal:**
- CONTEXT.md D1 requires `App.Current.Dispatcher.InvokeAsync(() => new CommandConfirmationWindow(ctx).ShowDialog())`. Match the `System.Windows.Application.Current.Dispatcher` form in `NativeTools.cs:547` (fully qualified — there is no `using System.Windows.Application;` alias in `NativeTools.cs`).
- **Reentrancy guard:** D1 mandates "if `Application.Current == null` (unit test or non-UI host), the tool MUST return a deny result." Match the error-string-return idiom (CONVENTIONS.md rule #3, `NativeTools.cs:325, 335, 339, 346, 353`):
  ```csharp
  if (System.Windows.Application.Current == null)
      return "ACESSO NEGADO: interface de confirmação indisponível (sem UI).";
  ```
  Or use the planned `deny_no_ui` audit outcome and Portuguese error message — string format matches the existing `ACESSO NEGADO (SANDBOX):` family of errors in `RunCommandTool`.
- **Order:** CONTEXT.md D5 + D8 require modal to fire BEFORE the existing denylist guard at line 320. New flow inside `ExecuteAsync(argumentsJson, userLevel)`:
  1. Parse `command`.
  2. Check `AlwaysAllowSession.Contains(command)` → if true, skip modal, jump to step 5.
  3. Build `CommandConfirmationContext { Tool = "run_command", Command = command, Level = userLevel, Cwd = DirectoryService.DataDir }` (cwd matches what's currently passed to `CommandService.ExecuteAsync` at line 357).
  4. `Dispatcher.InvokeAsync(() => new CommandConfirmationWindow(ctx).ShowDialog())` → read `window.IsAllowed` and `window.AlwaysAllow` post-close. If allowed && always-allow, `AlwaysAllowSession.Add(command)`. Fire-and-forget `AuditLogService.AppendAsync(...)`.
  5. If allowed: keep the existing `if (userLevel < 9) { ... denylist ... }` block at lines 320-355 (CONTEXT.md D5 keeps the guard, just behind the modal). Then `return await CommandService.ExecuteAsync(command, DirectoryService.DataDir);` (line 357 stays).
  6. If denied: return `"Comando recusado pelo usuário"` (matches the Portuguese error idiom and the D7 UAT script #1).
- **Level read:** `RunCommandTool.ExecuteAsync(string argumentsJson, int userLevel = 1)` already receives `userLevel` directly from `ToolRegistry.ExecuteToolAsync` (`ToolRegistry.cs:32-42`). No new call to `LevelService.GetLevel` needed inside the tool — just put `userLevel` into the context object for D3's "Nível: N/9" display.

**Where the level originates (for D3 modal text):** All tools today receive the level via the `ExecuteAsync` parameter, sourced once per LLM turn in `OpenAIService` from `LevelService.GetLevel(_settingsService.LoadSettings().MessageCount)` (call site visible at `ChatWindow.xaml.cs:69`, also `:166`, `:170`, `:897`). The tool does not need to re-read it.

---

### `Views/CommandConfirmationWindow.xaml.cs` — new ctor + keep old ctor

**Analog (same-file):** existing ctor at `AIBWindows/Views/CommandConfirmationWindow.xaml.cs:11-15`.
**Analog (sibling window taking a context object):** none. `SettingsWindow.xaml.cs:8` and `ShadowWidget.xaml.cs` use parameterless constructors. `CommandConfirmationWindow` is the only dialog in the project today, and its existing one-arg ctor (`string commandDescription`) is the only "context-bearing" precedent.

**Copy:**
- Add a second ctor: `public CommandConfirmationWindow(CommandConfirmationContext ctx) : this(ctx.Command)`. Per CONTEXT.md D3 "Backward-compatible by adding a second ctor or by replacing the call sites" — chain to the existing ctor so `CommandText.Text` continues to be set the same way, then populate the three new TextBlocks added in the XAML (Tool, Level "N/9", CWD).
- Keep the existing one-arg ctor (CONTEXT.md says "will be removed in Phase 3").
- No new imports needed; `AIB.Services.CommandConfirmationContext` is in the same root namespace and already implicitly available, but add `using AIB.Services;` at the top if not present.
- `IsAllowed` and `AlwaysAllow` properties (`xaml.cs:8-9`) stay as-is — they are the contract the caller reads.

---

### `Views/CommandConfirmationWindow.xaml` — add `IsCancel="True"` + 3 rows

**Analog (same file):** the XAML at `AIBWindows/Views/CommandConfirmationWindow.xaml`. Already structured as a `Grid` with row-definitions (lines 29-33) and a content `StackPanel` at row 1 (line 40-47). Pattern to copy: extend that `StackPanel` with three additional rows for Tool / Nível / CWD.

**Copy:**
- For the Deny button (line 50), add `IsCancel="True"` per CONTEXT.md D4 — WPF auto-routes Esc to it.
- Keep visual style consistent: same `Foreground="#AAA"`, same `FontSize`, same `Margin` rhythm as the existing "A AIB solicitou..." label at line 41. Re-use the inline `Border Background="#1AFFFFFF"` chip style from line 42 for the CWD path (paths get monospace too, like the command text).
- WPF resources already declared at the top (`FlyoutBg`, `SystemBorder`, `SystemText`, `WarningAccent` — lines 13-21) — re-use, do not add new brushes (`Regras de Identidade/VISUAL.MD` discourages new palette entries; reference existing `FlyoutBg` family).

---

### `Services/CommandService.cs` — optional `cwd` parameter

**Analog:** itself. `CommandService.cs:10` already exposes `string? workDir = null` — the signature is ready. CONTEXT.md change is a no-op at this layer; the only change is the caller (`RunCommandTool`) passes the cwd it just displayed in the modal (already does at line 357 with `DirectoryService.DataDir`).

**Copy:** nothing to add. Verify the existing fall-back at line 23 (`string.IsNullOrWhiteSpace(workDir) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : workDir`) — that is the `%USERPROFILE%` default referenced by CONTEXT.md D3 as the "defaults to %USERPROFILE% per CommandService today" anchor.

---

### `Services/SettingsService.cs` — surface `ConfirmDangerousCommands`

**Analog:** itself. `SettingsService.cs:36` already declares `public bool ConfirmDangerousCommands { get; set; } = true;` with default `true` — matches CONTEXT.md D8 "Phase 1 default: ON."

**Copy:** nothing structural. The field is auto-round-tripped by `System.Text.Json.JsonSerializer` (`SettingsService.cs:63, 84`). Missing-field migration is automatic — `JsonSerializer.Deserialize<UserAppSettings>` populates missing properties with the C# property default, which is already `true`. CONTEXT.md D8 "missing field → true" is satisfied with **zero code change** here. The only change is consumption: `RunCommandTool` (or the modal-gate path) reads `settings.ConfirmDangerousCommands` to decide whether to bypass the modal when the denylist doesn't fire.

---

## Shared Patterns

### File-scoped namespace, no DI

**Source:** `AIBWindows/Services/LevelService.cs:3`, `Services/DirectoryService.cs:4`, `Services/ShadowHistoryService.cs:4`.
**Apply to:** All 3 new files in this phase.
**Pattern:** `namespace AIB.Services;` at top, no enclosing braces. Avoid the legacy block-namespace style still used in `ReminderService.cs`/`ChatHistoryService.cs`/`ContextService.cs` (CONVENTIONS.md flags these as inconsistency holdouts).

### Static class + process-singleton state

**Source:** `Services/LevelService.cs`, `Services/DirectoryService.cs`, `Services/ShadowHistoryService.cs`, `Services/ContextService.cs`.
**Apply to:** `AlwaysAllowSession`, `AuditLogService`.
**Pattern:** `public static class` + `private static readonly` fields. No constructor, no `new`. Consumers reference by `TypeName.Method(...)` — no wiring in `ChatWindow.xaml.cs` constructor needed, no entry in `ToolRegistry.RegisterNativeTools` (these are services, not `ITool`s).

### Error swallowing on file I/O

**Source:** `ReminderService.cs:79`, `ChatHistoryService.cs:52` (`catch { }`), `SettingsService.cs:65-78` (try/catch with legacy fallback).
**Apply to:** `AuditLogService` — but **upgrade slightly** to console-log with `[AUDIT]` tag per CONTEXT.md D6 ("Failures are logged to console but do not block the modal"). Do not `throw` from `AppendAsync`.

### Portuguese error strings with prefix taxonomy

**Source:** `NativeTools.cs:325-353` (`ACESSO NEGADO (SANDBOX): ...`), `NativeTools.cs:570` (`ERRO ao interagir com...: ...`).
**Apply to:** New tool-level errors in `RunCommandTool`: use `ACESSO NEGADO:` prefix for the no-UI rejection ("ACESSO NEGADO: interface de confirmação indisponível.") and a plain "Comando recusado pelo usuário" for the standard deny path (matches D7 UAT scenario #1's expected string).

### Logging tag

**Source:** CONVENTIONS.md tag taxonomy: `[REGISTRY]`, `[STREAM-DBG]`, `[WARMUP]`, `[DEBUG-COFRE]`, `[HOTKEY]`.
**Apply to:** A new `[AUDIT]` tag for `AuditLogService` console error logs. A new `[CONFIRM]` tag may be appropriate for `RunCommandTool` modal-related console traces if planner wants observability — both are consistent with the established convention.

### Dispatcher hop from a tool

**Source:** `NativeTools.cs:547` and `:555` (`ManageClipboardTool` — the only existing tool that uses the dispatcher).
**Apply to:** `RunCommandTool`'s new modal call. Copy the fully-qualified `System.Windows.Application.Current.Dispatcher.InvokeAsync(...)` form (not the unqualified `Dispatcher.InvokeAsync` form used in code-behind, which only works because `Window` inherits a `Dispatcher` property).

---

## No Analog Found

| Item | Why no analog |
|---|---|
| `ShowDialog()` invoked **from a background thread** via `Dispatcher.InvokeAsync` and awaited on the threadpool | The closest existing pattern (`ManageClipboardTool`) marshals to the UI but does not open a modal window from a tool. `ChatWindow.xaml.cs:868` calls `settingsWin.ShowDialog()` but it does so already on the UI thread. CONTEXT.md D1 is therefore a genuinely new pattern in this codebase. Planner should write it once in `RunCommandTool` and document the reentrancy-guard rule for future tools (D1 second bullet). |
| JSONL **append** (vs. JSON full-rewrite) | All existing JSON persistence in `AIBWindows/Services/` uses `File.WriteAllText` with `WriteIndented = true`. JSONL append is new. Use `File.AppendAllTextAsync` and explicit `"\n"` newline. |

---

## Metadata

**Analog search scope:** `AIBWindows/Services/`, `AIBWindows/Views/`, `AIBWindows/App.xaml.cs`.
**Files read for pattern extraction:** `App.xaml.cs`, `ITool.cs`, `LevelService.cs`, `DirectoryService.cs`, `CommandService.cs`, `SettingsService.cs`, `ReminderService.cs`, `ChatHistoryService.cs`, `ContextService.cs`, `ShadowHistoryService.cs`, `SkillService.cs` (head), `ToolRegistry.cs`, `NativeTools.cs` (RunCommandTool + ManageClipboardTool slices), `CommandConfirmationWindow.xaml`, `CommandConfirmationWindow.xaml.cs`, `ChatWindow.xaml.cs` (composition root + settings open).
**Files NOT read (intentionally):** `OpenAIService.cs`, `MemoryService.cs`, `VoiceService.cs`, `OcrService.cs`, `ShadowAssistantService.cs` — not relevant to any Phase 1 file. The Linux side (`AIBLinux/`) is out of scope per CONTEXT.md "Files explicitly NOT touched."
**Pattern extraction date:** 2026-05-28
