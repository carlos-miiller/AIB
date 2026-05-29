---
phase: 01_modal-and-level9
milestone: security-remediation-v1
requirements: [SEC-01, SEC-02]
concerns_closed: [CRITICAL-1, CRITICAL-3]
type: execute
waves: [A, B, C, D, E]
tasks: 9
autonomous: false  # T9 is human UAT
files_modified:
  - AIBWindows/Services/CommandConfirmationContext.cs   # NEW
  - AIBWindows/Services/AlwaysAllowSession.cs           # NEW
  - AIBWindows/Services/AuditLogService.cs              # NEW
  - AIBWindows/Views/CommandConfirmationWindow.xaml
  - AIBWindows/Views/CommandConfirmationWindow.xaml.cs
  - AIBWindows/Services/SettingsService.cs
  - AIBWindows/Services/NativeTools.cs
  - .planning/codebase/CONCERNS.md
  - .planning/phases/01_modal-and-level9/VERIFICATION.md # NEW
decisions_implemented: [D1, D2, D3, D4, D5, D6, D7, D8]
---

# Phase 01 — Modal confirmation + Level-9 alignment — PLAN

## Phase goal

Wire the existing-but-orphan `CommandConfirmationWindow` into `RunCommandTool.ExecuteAsync` so every `run_command` invocation — at every user level, including Level 9 — gates through a human-in-the-loop modal that displays tool name, command text, current level (`Nível: N/9`), and the working directory `cmd.exe` will execute in. The modal hops from the tool's background thread to the WPF UI thread via `Application.Current.Dispatcher.InvokeAsync` (D1), captures Allow/Deny + AlwaysAllow, persists outcomes to a JSONL audit log at `~/.AIB/logs/audit.log` (D6), and respects a session-only `HashSet<string>` AlwaysAllow set (D2). Level-9 semantics are realigned per D5 — L9 disables only the denylist guard, never the modal. The `settings.ConfirmDangerousCommands` field already declared at `SettingsService.cs:36` is wired into the gating rule per D8. This phase closes CONCERNS.md CRITICAL #1 (orphan modal) and CRITICAL #3 (L9 bypass) and satisfies REQUIREMENTS.md SEC-01 + SEC-02. Verification is manual UAT (D7) captured in `VERIFICATION.md` — no automated test suite exists per `codebase/TESTING.md`.

---

## Wave A — New leaf files (parallel-safe)

T1, T2, T3 touch only new files. No file overlap with each other or with later waves. All three can be implemented and committed in any order.

### T1 — `CommandConfirmationContext` POCO

**Files:** `AIBWindows/Services/CommandConfirmationContext.cs` (NEW)

**Change:** Create a `public class CommandConfirmationContext` with file-scoped namespace `AIB.Services`, four `{ get; init; }` string/int properties with default values: `Tool` (string, default `""`), `Command` (string, default `""`), `Level` (int, default `0`), `Cwd` (string, default `""`). XML `///` summary on the public type. **No `record` keyword** — zero `record` types exist under `AIBWindows/Services/` (PATTERNS.md: copy `ShadowSuggestion` at `ShadowHistoryService.cs:10-15` for shape). Implements D3 modal text-content carrier (RESEARCH.md §"Recommended Project Structure" + PATTERNS.md row 1).

**Acceptance:**
- File compiles in isolation; class has exactly four public init-only properties matching the names above.
- Namespace is file-scoped (`namespace AIB.Services;`), not block-scoped.
- No `using` directives required beyond defaults (POCO references only BCL primitives).

**Commit:**
```
feat(security): add CommandConfirmationContext POCO for modal payload

Carries tool name, command text, user level, and working directory
from RunCommandTool to CommandConfirmationWindow per phase 01 D3.

Refs SEC-01.
```

**Depends on:** none

---

### T2 — `AlwaysAllowSession` static singleton

**Files:** `AIBWindows/Services/AlwaysAllowSession.cs` (NEW)

**Change:** `public static class AlwaysAllowSession` in `namespace AIB.Services;`. Private `static readonly HashSet<string> _allowed = new();` (underscore-camel per CONVENTIONS.md). Public surface: `static bool Contains(string command)`, `static void Add(string command)`, `static void Clear()` (used by tests/manual reset). Guard `Add`/`Contains` with `lock (_allowed)` — D2 requires exact-string match (no `StringComparer.OrdinalIgnoreCase`). Add `// Não persiste em disco — sessão única; quando o app fecha, a lista zera.` comment per PATTERNS.md (mirrors `ShadowHistoryService.cs:21-23`). Closes D2.

**Acceptance:**
- `Contains("git status")` returns `false` initially, returns `true` after `Add("git status")`.
- `Add` and `Contains` are thread-safe (both `lock (_allowed)`).
- No persistence; no file I/O; no Dispatcher marshaling.

**Commit:**
```
feat(security): add AlwaysAllowSession session-only allowlist

Static class with lock-guarded HashSet<string>. Exact string match,
no normalization, cleared on app exit. Read by RunCommandTool before
showing the confirmation modal; written after Allow+AlwaysAllow.

Implements phase 01 D2. Refs SEC-01.
```

**Depends on:** none

---

### T3 — `AuditLogService` JSONL writer

**Files:** `AIBWindows/Services/AuditLogService.cs` (NEW)

**Change:** `public static class AuditLogService` in `namespace AIB.Services;`. Private `static readonly string FilePath = Path.Combine(DirectoryService.DataDir, "logs", "audit.log");` — path resolution **uses `DirectoryService.DataDir` (the `~/.AIB/` codebase root)** per CONTEXT.md D6 (revised) and RESEARCH.md Open Question #1 (RESOLVED). The `logs/` subdir is created lazily on first append. Private `static readonly SemaphoreSlim _writeLock = new(1, 1);` to serialize concurrent appends (RESEARCH.md §Alternatives). Public `static async Task AppendAsync(object entry)` accepting an anonymous-object/POCO with fields `ts`, `tool`, `cmd`, `level`, `cwd`, `outcome`, `always_allow`. Inside: ensure parent dir via `Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!)`, acquire semaphore, serialize with `JsonSerializer.Serialize(entry)` (NO `WriteIndented = true` — JSONL is single-line), append `line + "\n"` (LF only per JSONL spec) using `File.AppendAllTextAsync(FilePath, line + "\n", Encoding.UTF8)`, release semaphore. Wrap the whole body in `try { ... } catch (Exception ex) { Console.WriteLine($"[AUDIT] Falha ao gravar audit.log: {ex.Message}"); }` (D6: console-log, do not throw; `[AUDIT]` tag per PATTERNS.md "Logging tag" section). Closes D6.

**Acceptance:**
- Calling `await AuditLogService.AppendAsync(new { ts="2026-05-29T00:00:00Z", tool="run_command", cmd="x", level=1, cwd="C:\\", outcome="allow", always_allow=false })` appends exactly one line ending in `\n` to `~/.AIB/logs/audit.log`.
- Two concurrent `AppendAsync` calls produce two complete, non-interleaved JSON lines (serialized by the semaphore).
- An I/O failure (e.g. locked file) writes a `[AUDIT]` line to console and does not throw.

**Commit:**
```
feat(security): add AuditLogService JSONL writer

Append-only writer to ~/.AIB/logs/audit.log. Serializes a small
POCO/anonymous record per line. SemaphoreSlim serializes concurrent
appends; failures log to console with [AUDIT] tag, never throw.

Implements phase 01 D6. Refs SEC-01.
```

**Depends on:** none

---

## Wave B — View + settings surface (parallel-safe; depends on Wave A)

T4, T5, T6 all depend on T1 (`CommandConfirmationContext` must exist before the new ctor compiles). T4 and T5 both touch `CommandConfirmationWindow.*` but DIFFERENT files (`.xaml` vs `.xaml.cs`), so they can be commits in parallel. T6 touches only `SettingsService.cs`. No cross-overlap.

### T4 — Modal XAML: Esc-as-Deny + Tool/Nível/CWD rows

**Files:** `AIBWindows/Views/CommandConfirmationWindow.xaml`

**Change:**
1. On the "Recusar" Button (current line 50), add `IsCancel="True"` so WPF auto-routes Esc, X, and Alt+F4 to its `Click` handler (D4).
2. Inside the row-1 `StackPanel` (current lines 40-47), insert three new labeled rows ABOVE the existing "A AIB solicitou..." block:
   - **Tool row:** `TextBlock x:Name="ToolText"` rendering `Tool: <name>` (default text `Tool: run_command`).
   - **Level row:** `TextBlock x:Name="LevelText"` rendering `Nível: N/9` (default text `Nível: 1/9`).
   - **CWD row:** wrap an inline `Border Background="#1AFFFFFF" Padding="6" CornerRadius="4"` (re-use the existing chip style from the command Border at line 42) containing `TextBlock x:Name="CwdText"` with `FontFamily="Consolas" FontSize="12"` (paths get monospace too). Add a small `Foreground="#AAA"` "CWD:" caption above it.
3. Re-use existing brushes only (`FlyoutBg`, `SystemBorder`, `SystemText`, `WarningAccent`); do NOT add new palette entries (per VISUAL.MD discipline noted in PATTERNS.md).
4. Bump `Height` from `300` to `380` to accommodate the new rows.

**Acceptance:**
- Project still builds (`dotnet build AIBWindows/AIB.csproj`).
- Modal preview (visual inspection at runtime in T9 UAT) shows four ordered content blocks: Tool, Command (existing), Nível, CWD.
- Pressing Esc with the modal focused triggers the Deny path (T9 scenario 6 will validate).

**Commit:**
```
feat(security): add IsCancel + tool/level/cwd rows to confirmation modal

- Deny button now IsCancel=True: Esc, X, Alt+F4 all route to deny.
- Three new TextBlocks (ToolText, LevelText, CwdText) populated by
  the new context-aware constructor (T5).
- Height bumped 300 -> 380 for the added rows.
- Re-uses existing FlyoutBg/SystemBorder/WarningAccent brushes.

Implements phase 01 D3 + D4. Refs SEC-01.
```

**Depends on:** T1 (xaml.cs ctor in T5 needs the new TextBlocks AND `CommandConfirmationContext` — T4 alone only adds the named TextBlocks, but T5 ships in the same wave and codifies the contract).

---

### T5 — Modal code-behind: context-aware ctor

**Files:** `AIBWindows/Views/CommandConfirmationWindow.xaml.cs`

**Change:**
1. Add `using AIB.Services;` to imports.
2. **Replace** the existing one-arg ctor `public CommandConfirmationWindow(string commandDescription)` with a single ctor: `public CommandConfirmationWindow(CommandConfirmationContext ctx) { InitializeComponent(); CommandText.Text = ctx.Command; ToolText.Text = $"Tool: {ctx.Tool}"; LevelText.Text = $"Nível: {ctx.Level}/9"; CwdText.Text = ctx.Cwd; }`. The legacy `string` ctor is **dropped entirely** — it had zero call sites (RESEARCH.md Open Question #2 RESOLVED; CONTEXT.md D3 revised).
3. `IsAllowed` and `AlwaysAllow` properties stay unchanged — they are the contract the caller reads (PATTERNS.md row 6).

**Acceptance:**
- `new CommandConfirmationWindow(new CommandConfirmationContext { Tool="run_command", Command="git status", Level=5, Cwd=@"C:\Users\Carlo\.AIB" })` compiles and produces a window whose four TextBlocks show those four values when shown.
- The legacy `new CommandConfirmationWindow("git status")` no longer compiles (the string ctor is gone). `grep -rn "new CommandConfirmationWindow(\"" AIBWindows/` returns zero hits.
- No new `IsAllowed`/`AlwaysAllow` semantics introduced.

**Commit:**
```
feat(security): CommandConfirmationWindow ctor accepts context object

Replaces the legacy string-only ctor (zero call sites pre-phase) with
a single ctor that accepts CommandConfirmationContext and populates
the Tool, Nível, and CWD TextBlocks introduced in T4. Drop of the
legacy ctor is safe because the modal was never wired before this
phase.

Implements phase 01 D3. Refs SEC-01.
```

**Depends on:** T1 (uses `CommandConfirmationContext`), T4 (references `ToolText`/`LevelText`/`CwdText` named elements).

---

### T6 — Surface `settings.ConfirmDangerousCommands` for consumption

**Files:** `AIBWindows/Services/SettingsService.cs`

**Change:** Per PATTERNS.md row 8 and CONTEXT.md D8 (revised), the property is **already declared** at `SettingsService.cs:36` as `public bool ConfirmDangerousCommands { get; set; } = true;` and round-trips through `JsonSerializer.Deserialize<UserAppSettings>` (missing field → C# default → `true`, satisfying D8's "missing field → true" migration rule with zero code change). **No structural change required.** This task verifies the current state, adds an XML `/// <summary>` to the property documenting D8 revised semantics ("Modal sempre dispara em run_command (independente desta flag). ON (default): o denylist roda como segunda camada após o modal em níveis < 9. OFF: denylist é ignorado; o modal é o único portão. L9: denylist sempre ignorado."), and confirms via `Grep` that no other code path overrides the default. The implementing executor MUST NOT introduce a new constructor, new migration logic, or any behavior change here — just the XML doc.

**Acceptance:**
- `SettingsService.cs:36` retains `= true;` default.
- Property has a `/// <summary>` block describing D8 ON/OFF semantics in Portuguese (matches file's existing comment language), clarifying that the flag controls ONLY the post-modal denylist, not the modal itself.
- Loading a `profile.dat` that lacks the field still yields `ConfirmDangerousCommands == true` (verified by inspection — `JsonSerializer` default behavior).

**Commit:**
```
docs(security): document ConfirmDangerousCommands D8 semantics

XML summary on the existing property at SettingsService.cs:36. No
behavior change — property was already declared with default true
and JsonSerializer handles missing-field migration automatically.

Implements phase 01 D8. Refs SEC-01.
```

**Depends on:** none structurally; sequenced in Wave B for tidy grouping.

---

## Wave C — Central wiring (sequential; depends on Wave A + Wave B)

### T7 — Restructure `RunCommandTool.ExecuteAsync` (THE wiring task)

**Files:** `AIBWindows/Services/NativeTools.cs` (lines 281-359 = `RunCommandTool` class)

**Change:** Restructure the body of `ExecuteAsync(string argumentsJson, int userLevel = 1)` per the post-fix architecture diagram in RESEARCH.md §"System Architecture Diagram (RunCommandTool flow, post-fix)" (lines 60-101). Also extract the existing denylist body (current lines 320-355) into a new private method `private static string? ApplyDenylist(string cmdLower, int userLevel)` that returns `null` on pass or the existing Portuguese `ACESSO NEGADO (SANDBOX): ...` string on hit. The new top-level `ExecuteAsync` body in order:

1. Parse `command`; bail with the existing `"ERRO: 'command' é obrigatório."` if empty.
2. Compute `string cwd = DirectoryService.DataDir;` (this matches what's already passed to `CommandService.ExecuteAsync` at line 357 — the actual directory `cmd.exe` runs in, satisfying D3's "the path cmd.exe will run in").
3. Build `var ctx = new CommandConfirmationContext { Tool = "run_command", Command = command, Level = userLevel, Cwd = cwd };`.
4. **AlwaysAllow fast path:** `if (AlwaysAllowSession.Contains(command)) { _ = AuditLogService.AppendAsync(BuildEntry(ctx, "always_allow", true)); goto step 7; }` — implement as an early branch (NOT a literal `goto`; use an `if/else if/else` or a local flag). The audit call is fire-and-forget per D6 (PATTERNS.md "Fire-and-forget" + `_ = InitVoiceAsync();` analog at `ChatWindow.xaml.cs:103`).
5. **No-UI guard (D1):** `if (System.Windows.Application.Current == null) { _ = AuditLogService.AppendAsync(BuildEntry(ctx, "deny_no_ui", false)); return "ACESSO NEGADO: interface de confirmação indisponível."; }` (Portuguese error idiom per PATTERNS.md "Portuguese error strings").
6. **Modal hop (D1, PATTERNS.md row 4 — copy `ManageClipboardTool.ExecuteAsync` shape at `NativeTools.cs:535-572`):**
   ```
   var (allowed, alwaysAllow) = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
   {
       var win = new CommandConfirmationWindow(ctx) { Owner = System.Windows.Application.Current.MainWindow };
       bool result = win.ShowDialog() == true;
       return (result && win.IsAllowed, win.AlwaysAllow);
   }).Task;
   ```
   - On `!allowed`: `_ = AuditLogService.AppendAsync(BuildEntry(ctx, "deny", false));` then `return "Comando recusado pelo usuário";` (matches D7 UAT scenario 1 expected string).
   - On `allowed && alwaysAllow`: `AlwaysAllowSession.Add(command); _ = AuditLogService.AppendAsync(BuildEntry(ctx, "always_allow", true));`
   - On `allowed && !alwaysAllow`: `_ = AuditLogService.AppendAsync(BuildEntry(ctx, "allow", false));`
7. **Denylist gate (D5 + D8 fused):** Read `var settings = new SettingsService().LoadSettings();` (or the project's existing accessor — confirm the call site in `OpenAIService` for the canonical accessor before settling). Apply the rule per **revised** CONTEXT.md D8:
   - **L9 always skips the denylist** (D5), regardless of `ConfirmDangerousCommands`.
   - For `userLevel < 9`: if `settings.ConfirmDangerousCommands == true` (default, most conservative) → **run** the denylist as an additional hard floor below the modal. If `settings.ConfirmDangerousCommands == false` → **skip** the denylist; the modal is the only gate.
   - Concretely: `if (userLevel < 9 && settings.ConfirmDangerousCommands) { var deny = ApplyDenylist(command.ToLowerInvariant(), userLevel); if (deny != null) return deny; }`.
8. **Execute:** `return await CommandService.ExecuteAsync(command, cwd);` (no behavior change — the existing call at line 357 already passes `DirectoryService.DataDir`).

Also add a private helper `private static object BuildEntry(CommandConfirmationContext ctx, string outcome, bool alwaysAllow) => new { ts = DateTime.UtcNow.ToString("o"), tool = ctx.Tool, cmd = ctx.Command, level = ctx.Level, cwd = ctx.Cwd, outcome, always_allow = alwaysAllow };` so all five audit call sites share one shape (D6 schema).

**NO change** to: `RequiredLevel`, `ChatToolDefinition`, `ContainsWord`. Only `ExecuteAsync` body + new private `ApplyDenylist` + new private `BuildEntry`.

**Acceptance:**
- Project builds (`dotnet build AIBWindows/AIB.csproj`) with zero new warnings.
- Manual smoke: launching the app and triggering `run_command` from a chat turn opens the modal (T9 will codify formal UAT).
- A `Grep` for `userLevel < 9` in `NativeTools.cs` confirms the denylist guard now lives inside `ApplyDenylist` and the call site is gated on `settings.ConfirmDangerousCommands == true` per D8 (revised).
- Diff scope is confined to `RunCommandTool` (lines ~281-359 + a few lines for the two new helpers). Other tool classes in the file (e.g. `ManageClipboardTool` at line 535) are untouched.

**Commit:**
```
feat(security): wire CommandConfirmationWindow into RunCommandTool

Restructures RunCommandTool.ExecuteAsync per phase 01 D1/D2/D5/D6/D8:

- All levels (incl. L9) now gate through the WPF modal via
  Application.Current.Dispatcher.InvokeAsync (D1, D5).
- AlwaysAllowSession checked first for session-only fast path (D2).
- Application.Current == null returns "ACESSO NEGADO:
  interface de confirmação indisponível." (D1 reentrancy guard).
- Existing denylist extracted into private ApplyDenylist(); now
  gated behind (userLevel < 9 && !settings.ConfirmDangerousCommands)
  per D8 — modal is the HITL when the setting is ON.
- Every outcome fire-and-forgets one AuditLogService.AppendAsync
  entry (allow / deny / always_allow / deny_no_ui) per D6.

Closes CONCERNS.md CRITICAL #1 (orphan modal) and CRITICAL #3 (L9
bypass). Refs SEC-01, SEC-02.
```

**Depends on:** T1, T2, T3, T4, T5, T6 (T6 not structurally required but logically grouped — settings semantics finalized before the consuming code lands).

---

## Wave D — Findings closure (depends on T7 commit SHA)

### T8 — Move SEC-01 + SEC-02 entries to CONCERNS.md "Resolved" section

**Files:** `.planning/codebase/CONCERNS.md`

**Change:** Move the CRITICAL #1 (orphan modal) and CRITICAL #3 (L9 bypass) entries from their current location to a new `## Resolved` section at the bottom of the file. Each moved entry MUST be annotated with:

- The phase that resolved it (`Phase 01 — modal-and-level9 (security-remediation-v1)`).
- The SHA of the T7 commit (the executor obtains this from `git log -1 --format=%H` after T7 lands).
- A one-line summary of how it was resolved (e.g. "Modal wired into `RunCommandTool.ExecuteAsync` via `Application.Current.Dispatcher.InvokeAsync`; all levels gate through HITL.").
- The resolution date (today).

If `## Resolved` does not yet exist in CONCERNS.md, create it as the last `##` heading in the file.

**Acceptance:**
- CONCERNS.md no longer lists CRITICAL #1 and CRITICAL #3 under their original "CRITICAL" heading.
- CONCERNS.md contains a `## Resolved` section with both entries, each carrying phase tag + SHA + one-line resolution + date.
- A `git log -1 --format=%H` of T7's commit matches the SHA recorded in this update.

**Commit:**
```
docs(concerns): mark SEC-01 + SEC-02 (CRITICAL #1, #3) resolved

Moves CONCERNS.md CRITICAL #1 (orphan CommandConfirmationWindow)
and CRITICAL #3 (Level-9 bypass of denylist + modal) to a new
Resolved section, citing phase 01 commit <SHA-of-T7>.

Closes CONCERNS.md CRITICAL #1, #3 from security-remediation-v1.
```

**Depends on:** T7 (needs the T7 commit SHA before this commit can be authored).

---

## Wave E — Manual UAT scaffolding (depends on T7)

### T9 — Author `VERIFICATION.md` UAT checklist

**Files:** `.planning/phases/01_modal-and-level9/VERIFICATION.md` (NEW)

**Change:** Create the file with one section per D7 scenario (8 total). Each scenario is a checkbox with: preconditions, exact steps, expected result, observed result (left blank), pass/fail. The owner runs the app and fills in observed/pass-fail manually after T7 ships. Include a header noting the build SHA under test (T7 commit) and the test date.

**Template body (copy verbatim into VERIFICATION.md):**

```
# Phase 01 — VERIFICATION (Manual UAT)

**Phase:** 01_modal-and-level9
**Milestone:** security-remediation-v1
**Build under test:** <commit SHA of T7>
**Tester:** <name>
**Date:** <YYYY-MM-DD>

> Manual UAT per CONTEXT.md D7. No automated test suite exists for AIBWindows
> (see .planning/codebase/TESTING.md). Each scenario MUST be executed against
> a fresh launch of the WPF app unless the scenario explicitly chains state.

## Scenarios

### S1 — L1 Deny path
- [ ] Pre: level == 1 (default fresh launch).
- [ ] Step: trigger `run_command` with `git status` via a chat turn.
- [ ] Expected: modal appears showing Tool=`run_command`, Command=`git status`, Nível=`1/9`, CWD=`<~/.AIB path>`.
- [ ] Step: click "Recusar".
- [ ] Expected: tool result string returned to the chat is exactly `Comando recusado pelo usuário`.
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S2 — L1 Allow path
- [ ] Pre: level == 1.
- [ ] Step: trigger `run_command("git status")` → click "Permitir" (do NOT check AlwaysAllow).
- [ ] Expected: tool returns the output of `git status` (or `[Comando executado, mas não retornou saída]` if outside a git repo).
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S3 — AlwaysAllow within a session
- [ ] Pre: level == 1, same app session as S2.
- [ ] Step: trigger `run_command("git status")` → click "Permitir" with the "Sempre permitir" CheckBox checked.
- [ ] Step: trigger `run_command("git status")` again (same exact string).
- [ ] Expected: second invocation does NOT show the modal; tool returns output directly.
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S4 — AlwaysAllow cleared on app restart
- [ ] Pre: complete S3 in this session (allowlist now contains "git status").
- [ ] Step: fully close the app (tray menu → Sair) and relaunch.
- [ ] Step: trigger `run_command("git status")` again.
- [ ] Expected: modal appears (AlwaysAllow state did NOT persist).
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S5 — L9 still shows modal for destructive verb
- [ ] Pre: elevate to level 9 via `/unlock_level 9` (manual chat command).
- [ ] Step: trigger `run_command("rm -rf /tmp/x")`.
- [ ] Expected: modal appears. The Command field shows `rm -rf /tmp/x` verbatim. Nível shows `9/9`.
- [ ] Step: click "Recusar".
- [ ] Expected: tool returns `Comando recusado pelo usuário`. Command is NOT executed.
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S6 — Esc denies
- [ ] Pre: any level.
- [ ] Step: trigger any `run_command`. With the modal focused, press Esc.
- [ ] Expected: modal closes via the Deny path. Tool returns `Comando recusado pelo usuário`. Audit log records outcome `deny`.
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S7 — No-UI guard
- [ ] Pre: a debug harness or build configuration where `System.Windows.Application.Current == null` (e.g. a headless console driver of `RunCommandTool`).
- [ ] Step: instantiate `RunCommandTool` and call `ExecuteAsync(argsJson: "{ \"command\": \"git status\" }", userLevel: 5)` directly.
- [ ] Expected: returns `ACESSO NEGADO: interface de confirmação indisponível.` Audit log records outcome `deny_no_ui`. NO crash, no exception bubbles.
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S8 — Audit log contents
- [ ] Pre: scenarios S1-S6 (and S7 if exercised) have run in order in a single test session.
- [ ] Step: open `~/.AIB/logs/audit.log` in a text editor.
- [ ] Expected: file exists; contains one JSON object per line (JSONL); each line has fields `ts`, `tool`, `cmd`, `level`, `cwd`, `outcome`, `always_allow`. Outcomes across the run map 1:1 to scenarios as follows:
  - S1 → `deny`
  - S2 → `allow`
  - S3 first call → `always_allow`; S3 second call → `always_allow`
  - S5 → `deny`
  - S6 → `deny`
  - S7 (if run) → `deny_no_ui`
- Observed (line count, paste a couple of lines): ________________
- Result: [ ] Pass  [ ] Fail

## Sign-off

- [ ] All 8 scenarios pass.
- [ ] CONCERNS.md CRITICAL #1 and #3 are listed in "Resolved" with the build SHA above.
- [ ] No regression observed in non-`run_command` tools (memory, vault, read_screen, manage_clipboard) — they MUST NOT show the confirmation modal.

Tester signature: ________________   Date: ____________
```

**Acceptance:**
- File exists at `.planning/phases/01_modal-and-level9/VERIFICATION.md`.
- Contains all 8 scenarios with checkbox structure shown above.
- Header has placeholders for build SHA, tester name, date.
- Sign-off section enumerates the three completion criteria.

**Commit:**
```
docs(verification): add manual UAT checklist for phase 01

Codifies the 8 scenarios from CONTEXT.md D7 (Deny, Allow, AlwaysAllow
in-session + cleared on restart, L9 modal, Esc, no-UI, audit log).
Tester fills observed/pass-fail by hand against a fresh build of T7.

Refs SEC-01, SEC-02.
```

**Depends on:** T7 (UAT cannot start until the code under test exists; this task only ships the scaffolding file — execution of the scenarios is a manual step outside the commit graph).

---

## Verification (UAT checklist — same as VERIFICATION.md, summarized)

The 8 D7 scenarios are the phase's exit criteria. They are codified verbatim in `VERIFICATION.md` (T9) and re-summarized here for the planner/checker pass:

- [ ] **S1** L1 `git status` → modal → Recusar → tool returns `Comando recusado pelo usuário`.
- [ ] **S2** L1 `git status` → modal → Permitir → tool returns command output.
- [ ] **S3** L1 `git status` → Permitir + AlwaysAllow check → re-invoke → no modal → tool returns output.
- [ ] **S4** App restart → AlwaysAllow cleared → modal returns for `git status`.
- [ ] **S5** L9 `rm -rf /tmp/x` → modal appears with destructive verb visible → Recusar → tool returns refusal.
- [ ] **S6** Esc on open modal → Deny path taken, tool returns refusal, audit logs `deny`.
- [ ] **S7** `Application.Current == null` harness: `RunCommandTool` returns deny without crash, audit logs `deny_no_ui`.
- [ ] **S8** `~/.AIB/logs/audit.log` contains 8 entries with correct outcomes and full schema.

Additional regression gates (definition of done per CONTEXT.md):
- [ ] `dotnet build AIBWindows/AIB.csproj` succeeds with no new warnings.
- [ ] `CommandConfirmationWindow` is invoked from exactly one call site: `RunCommandTool.ExecuteAsync` (verified by `Grep "new CommandConfirmationWindow"` returning exactly one source-code hit).
- [ ] Modal does NOT fire for `manage_memory`, `manage_vault`, `read_screen`, `manage_clipboard`, or any other `ITool` outside `RunCommandTool` (verified by manual smoke against each tool in S2-adjacent sessions).
- [ ] CONCERNS.md CRITICAL #1 and #3 are in `## Resolved` with the T7 SHA (T8 acceptance).

---

## Rollback

If a regression appears post-merge (e.g. modal never opens, modal deadlocks the UI thread, audit log fills the disk), revert the phase's commits in REVERSE topological order so each revert leaves the tree in a buildable state:

1. **Revert T9** (`docs(verification): add manual UAT checklist for phase 01`) — safe, doc-only, no code impact.
2. **Revert T8** (`docs(concerns): mark SEC-01 + SEC-02 (CRITICAL #1, #3) resolved`) — restores findings under "CRITICAL" so the next planning round sees them again.
3. **Revert T7** (`feat(security): wire CommandConfirmationWindow into RunCommandTool`) — this is the surgical fix. Reverting it alone (steps 4-6 left intact) restores the pre-phase behavior of `RunCommandTool` (orphan modal, L9 bypass) but keeps the supporting infrastructure (`AuditLogService`, `AlwaysAllowSession`, `CommandConfirmationContext`, the expanded XAML) on disk and inert. In most regression cases, **reverting only T7 is sufficient** — the supporting commits cause no behavior change without a caller.
4. **Revert T6** (`docs(security): document ConfirmDangerousCommands D8 semantics`) — doc-only, optional.
5. **Revert T5** (`feat(security): CommandConfirmationWindow ctor accepts context object`) — restores the original `string commandDescription` ctor and removes the context-based one. Must precede T4 revert because T4's named elements (`ToolText`, `LevelText`, `CwdText`) are referenced here.
6. **Revert T4** (`feat(security): add IsCancel + tool/level/cwd rows to confirmation modal`) — removes the XAML rows and `IsCancel`. Safe once T5 is gone.
7. **Revert T1, T2, T3** (the three new-file Wave-A commits) — `git revert` each in any order. Each deletes a single new file with no consumers (after T7 is gone).

**Minimum effective rollback for an emergency:** revert ONLY T7. The remaining commits are dormant (new files have no callers; XAML changes are visual-only on a window that nobody opens). Re-deploy. Schedule a proper unwinding of T1-T6 later if needed.

**Fast-rollback command (minimum case):**
```
git revert --no-edit <SHA-of-T7>
git push
```

**Full-rollback command (all 9, reverse order):**
```
git revert --no-edit <SHA-T9> <SHA-T8> <SHA-T7> <SHA-T6> <SHA-T5> <SHA-T4> <SHA-T3> <SHA-T2> <SHA-T1>
git push
```

---

## Decision coverage trace

| Decision | Implemented by |
|---|---|
| D1 (Modal threading) | T7 (Dispatcher.InvokeAsync hop + `Application.Current == null` deny path) |
| D2 (AlwaysAllow scope) | T2 (HashSet<string> static, lock-guarded) + T7 (read/write call sites) |
| D3 (Modal text content) | T1 (`CommandConfirmationContext` carrier) + T4 (XAML rows) + T5 (ctor populates rows) + T7 (builds ctx with tool/command/level/cwd) |
| D4 (Esc denies) | T4 (`IsCancel="True"` on Deny button) |
| D5 (L9 semantics) | T7 (modal always fires; `userLevel < 9` only gates the denylist via `ApplyDenylist`) |
| D6 (Audit log) | T3 (`AuditLogService`) + T7 (five call sites: allow / deny / always_allow / deny_no_ui / fast-path always_allow) |
| D7 (Manual UAT) | T9 (`VERIFICATION.md` with 8 scenarios) |
| D8 (`ConfirmDangerousCommands` wiring) | T6 (XML doc on the existing property) + T7 (gate at `if (userLevel < 9 && !settings.ConfirmDangerousCommands) ApplyDenylist(...)`) |
