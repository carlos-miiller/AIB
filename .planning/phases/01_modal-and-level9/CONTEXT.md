# CONTEXT: Phase 1 — Modal confirmation + Level-9 alignment

**Phase slug:** 01_modal-and-level9
**Milestone:** security-remediation-v1
**Requirements covered:** SEC-01, SEC-02 (REQUIREMENTS.md)
**CONCERNS.md findings closed:** 2 CRITICAL
**Discuss-phase date:** 2026-05-28
**Source:** decisions captured from interactive discuss round

## Locked decisions

### D1 — Modal threading

**Pattern:** `App.Current.Dispatcher.InvokeAsync(() => new CommandConfirmationWindow(...).ShowDialog())` awaited from the tool's background thread.

- Tool method stays `async Task<string>` and awaits the InvokeAsync result.
- `ShowDialog` blocks the UI thread until close; the awaiting tool resumes on the threadpool.
- Reentrancy guard: if `Application.Current == null` (unit test or non-UI host), the tool MUST return a deny result with a clear message (no implicit "allow when no UI").

### D2 — AlwaysAllow scope

**Lifetime:** Session-only, in-memory `HashSet<string>`, cleared on app exit.
**Match:** Exact string match (no normalization). Owner: a static or DI singleton holding the set; `RunCommandTool` checks it before showing the modal and inserts after Allow + checkbox.
**Settings field:** None added in v1. Field stays a runtime-only concept.

### D3 — Modal text content

Each modal displays four pieces of context, in this order:

1. Tool name — `run_command` / `execute_skill` / `materialize_skill` (only the first ships in Phase 1; the latter two land in Phase 3, but the modal API must accept the tool name now).
2. Command / script-body text (existing `CommandText` TextBlock).
3. User level + threshold — `Nível: N/9` (where N is the current `LevelService.CurrentLevel`).
4. Working directory — the path `cmd.exe` will run in (defaults to `%USERPROFILE%` per `CommandService` today).

Denylist hit flag is **deferred to Phase 3** — its data structure depends on the denylist-vs-allowlist decision still open in Phase 3.

The window constructor signature changes from `CommandConfirmationWindow(string commandDescription)` to `CommandConfirmationWindow(CommandConfirmationContext ctx)` where `ctx` holds the four fields above. **The legacy string ctor is dropped entirely** (post-plan-check decision): the modal had zero call sites before this phase, so there is no compat surface to preserve.

### D4 — Esc key handling

**Behavior:** Esc denies, via `IsCancel="True"` added to the Deny button.
**Side effect:** Window also auto-handles X / Alt+F4 as deny (already the default for `DialogResult = false`).

### D5 — Level-9 semantics

**Rule:** L9 disables the denylist guard only. The modal fires for every command at every level. L9 changes what verbs may appear in the modal for approval; it does not remove the human-in-the-loop.

**Implementation:** the `if (userLevel < 9) { ApplyDenylist(...); }` guard at `NativeTools.cs:320` stays as a denylist toggle. The modal call happens BEFORE the denylist check at all levels.

### D6 — Audit log

**File:** `~/.AIB/logs/audit.log` (JSONL, append-only). Path resolved via `DirectoryService` so it matches the rest of the AIB data root (memory.db, profile.dat, chat history). The `logs/` subdir is created on first write if missing.
**Schema:** one line per modal outcome:
```json
{"ts":"2026-05-28T14:32:11Z","tool":"run_command","cmd":"git status","level":5,"cwd":"C:\\Users\\Carlo","outcome":"allow","always_allow":false}
```
**Outcomes:** `allow`, `deny`, `always_allow`, `deny_no_ui` (D1 fallback).
**Write path:** `AuditLogService.AppendAsync(entry)`, fire-and-forget from the modal handler. Failures are logged to console but do not block the modal.

### D7 — UAT mechanism

Phase 1 verification is **manual UAT** recorded in `VERIFICATION.md`. The scripted scenarios (codified in PLAN.md) are:

1. L1: `run_command("git status")` → modal appears → Deny → tool returns "Comando recusado pelo usuário".
2. L1: same command → Allow → tool returns expected output.
3. L1: same command → Allow + AlwaysAllow check → re-call → no modal → expected output.
4. L1: app restart → AlwaysAllow cleared → modal returns.
5. L9 (force via `/unlock_level 9`): `run_command("rm -rf /tmp/x")` → modal appears with the destructive verb visible → Deny.
6. Esc on open modal → Deny path taken.
7. No-UI case (set `Application.Current = null` in a debug harness or use a Headless build): `RunCommandTool` returns deny without crash.
8. Audit log: after the above, `%APPDATA%/AIB/audit.log` contains 8 entries with correct outcomes.

### D8 — `settings.ConfirmDangerousCommands` scope

**Clarification (post-plan-check):** Per D5 and SEC-01, the **modal ALWAYS fires** regardless of this setting. The setting controls only whether the post-modal denylist runs as a second-layer block at levels < 9. L9 always skips the denylist (D5).

**Wire it.** Semantics:
- ON (default): modal fires on every `run_command`. AFTER user clicks Allow, the denylist still runs at levels < 9 and can refuse a verb the user just approved. Most conservative — the denylist is treated as an additional hard floor below the modal.
- OFF: modal still fires on every `run_command`. AFTER user clicks Allow, the denylist is skipped entirely; user approval is the only gate. Useful when the user wants the modal to be the single authority.
- L9: denylist always skipped regardless of D8 (per D5).

**Phase 1 default:** ON. Migration: when reading old `profile.dat` that does not contain the field, treat as ON.

**The modal call is independent of this setting.** This matters for SEC-01 which explicitly forbids the setting from gating the modal for dangerous commands. T7 implements the gate at the denylist call site only, not at the modal call site.

## Files in scope

| File | Change |
|---|---|
| `AIBWindows/Services/NativeTools.cs` (RunCommandTool ~ lines 281-359) | Insert modal call before `CommandService.ExecuteAsync`. Move denylist behind a method. |
| `AIBWindows/Services/CommandService.cs` | Signature accepts an optional `cwd` parameter for the modal text. No behavior change. |
| `AIBWindows/Views/CommandConfirmationWindow.xaml` | Add IsCancel to Deny, add 2 rows for "Tool", "Nível", "CWD". |
| `AIBWindows/Views/CommandConfirmationWindow.xaml.cs` | Replace ctor with one accepting `CommandConfirmationContext`; old `string` ctor removed (zero call sites). |
| `AIBWindows/Services/CommandConfirmationContext.cs` | New record type (tool, command, level, cwd). |
| `AIBWindows/Services/AlwaysAllowSession.cs` | New singleton holding the `HashSet<string>`. |
| `AIBWindows/Services/AuditLogService.cs` | New. JSONL append-only writer. |
| `AIBWindows/Services/SettingsService.cs` | Surface `ConfirmDangerousCommands` (already declared at line 36), default `true`. Migration: missing field → `true`. |

## Files explicitly NOT touched in Phase 1

- `ExecuteSkillTool`, `MaterializeSkillTool`, `SkillService` — Phase 3.
- `GrepTool`, `GlobTool`, `ListDirTool` — Phase 5.
- `OpenAIService` streaming — Phase 4 / 6.
- Linux variant — separate milestone.

## Open questions deferred to other phases

- Denylist vs allowlist replacement strategy → Phase 3 discuss.
- Audit log retention / rotation → out of scope this milestone.
- Whether to add `/unlock_level` gating → separate milestone (accepted risk).

## Definition of done (phase)

- [ ] All 8 UAT scenarios pass and are recorded in VERIFICATION.md.
- [ ] `CommandConfirmationWindow` is invoked from `RunCommandTool.ExecuteAsync` and from nowhere else in this phase.
- [ ] At L1 with denylist verb, modal appears and Deny short-circuits execution.
- [ ] At L9 with denylist verb, modal appears (verb visible in modal text) and Deny short-circuits execution.
- [ ] Audit log file exists and contains correct entries after manual UAT.
- [ ] No regression to existing tools (memory, vault, read_screen, etc.) — modal does not fire for them.
- [ ] CONCERNS.md SEC-01 + SEC-02 entries moved to a "Resolved" section with this phase's last commit SHA.
- [ ] Atomic-commit policy preserved (one logical change per commit).
