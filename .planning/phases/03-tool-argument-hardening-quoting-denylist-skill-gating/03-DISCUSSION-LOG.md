# Phase 3: Tool argument hardening (quoting + denylist + skill gating) - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-31
**Phase:** 03-tool-argument-hardening-quoting-denylist-skill-gating
**Areas discussed:** Denylist replacement (SEC-05), CommandService dual-path refactor (SEC-04), materialize_skill modal UX (SEC-06), Dead code (InstallFromOnlineAsync + DynamicSkillTool)

---

## Denylist replacement strategy (SEC-05)

### Q1: Core choice

| Option | Description | Selected |
|--------|-------------|----------|
| Delete denylist entirely (modal-only) | Rip ApplyDenylist + tables; modal is single authority. ROADMAP-supported. | |
| Allowlist of approved verbs per level | Positive security model; explicit list per level; parser splits command. | |
| Hybrid: tiny denylist of destructive verbs + modal-only at L≥7 | Floor of explicitly destructive verbs at L<7; modal-only at L≥7. | ✓ |

**User's choice:** Hybrid floor + modal-only at L≥7. Locked as D-01.

### Q2: Floor list contents

| Option | Description | Selected |
|--------|-------------|----------|
| Recursive deletion | rm -rf, del /s, Remove-Item -Recurse, ... | ✓ |
| Format / partition | format, diskpart, wmic logicaldisk, cipher /w | ✓ |
| Shutdown / reboot / logoff | shutdown, Restart-Computer, logoff | ✓ |
| Registry destructive ops | reg delete, Remove-Item -Path HK* | ✓ |

**User's choice:** All four categories. Locked as D-02.

### Q3: Matching algorithm

| Option | Description | Selected |
|--------|-------------|----------|
| Normalize-then-regex | Lowercase + strip concat + alias map + reject -EncodedCommand + regex word boundaries | ✓ |
| Tokenize via Win32 CommandLineToArgvW | API parser to argv; match argv[0] only | |
| Hard refuse shell metacharacters at L<7 | Block any `|`, `&`, `;`, etc. at L<7 | |
| Substring contains check | Plain string.Contains; simplest, vulnerable to bypass | |

**User's choice:** Normalize-then-regex. Locked as D-03.

### Q4: Modal-vs-floor order

| Option | Description | Selected |
|--------|-------------|----------|
| Modal first, warn in modal text, floor refuses post-Allow | Phase 1 D5/D8 invariant preserved; new DenylistHit + DenylistReason fields | ✓ |
| Floor refuses pre-modal at L<7 (no modal) | Less UI noise; breaks Phase 1 D5 invariant | |
| Modal first, no warning text — surprise refusal | Today's behavior pattern; confusing UX | |

**User's choice:** Modal first with warning banner. Locked as D-04. Closes Phase 1 D3 deferred "denylist hit" slot.

---

## CommandService dual-path refactor (SEC-04)

### Q1: ArgumentList path location

| Option | Description | Selected |
|--------|-------------|----------|
| Extend CommandService with ExecuteWithArgListAsync | One service, two methods; shared output/timeout/truncation | ✓ |
| SkillService spawns ProcessStartInfo directly | Total separation; duplicates capture/timeout logic | |
| Single new method, deprecate string-based ExecuteAsync | Breaks run_command's by-design cmd-string interface | |

**User's choice:** Extend CommandService. Locked as D-05.

### Q2: How to split LLM `arguments` string

| Option | Description | Selected |
|--------|-------------|----------|
| Treat whole string as a SINGLE argument | ArgumentList = [scriptPath, argumentsString]; SEC-04 acceptance-aligned | ✓ |
| Split by whitespace into argv[1..N] | Loses paths with spaces unless quoted | |
| Quoted-aware tokenizer | Re-introduces mini-shell parser | |

**User's choice:** Single literal argument. Locked as D-06.

### Q3: Interpreter mapping

| Option | Description | Selected |
|--------|-------------|----------|
| Stable static Dictionary in SkillService | Single source of truth; shared by all consumers | ✓ |
| Inline switch expression at each call site | Drift risk | |
| Reflective lookup from skill.json `argv_pattern` | Expands skill attack surface | |

**User's choice:** Static Dictionary. Locked as D-07.

---

## materialize_skill modal UX (SEC-06)

### Q1: How modal shows script body

| Option | Description | Selected |
|--------|-------------|----------|
| Extend existing CommandConfirmationWindow with ScrollViewer row | Reuses Phase 1 lock + audit; one window for everything | ✓ |
| New ScriptConfirmationWindow.xaml(.cs) | Duplicates boilerplate | |
| Truncate to first/last N + "View full" expander | Attacker hides destructive lines mid-script | |
| Show script body for execute_skill too | Closes "edited skill" attack | (combined into Q2) |

**User's choice:** Extend in place. Locked as D-08.

### Q2: execute_skill body display

| Option | Description | Selected |
|--------|-------------|----------|
| Yes — read scriptPath off disk and put in ctx.ScriptBody | User sees what they authorize; closes edit-after-AlwaysAllow attack | |
| No — execute_skill modal shows only command line | Faster; smaller modal; loses defense | |
| Yes, but only first time per session (content-hash cache) | Cache (skillName, contentHash); re-fires modal when disk content changes | ✓ |

**User's choice:** Yes with content-hash cache. Locked as D-09.

### Q3: AlwaysAllow + content-hash interplay

| Option | Description | Selected |
|--------|-------------|----------|
| Extend AlwaysAllowSession to key by (toolName, command, contentHash) | Single source of truth; minor schema change | ✓ |
| Two separate session sets | Divergence risk between two cleanup paths | |
| Skip the cache | AlwaysAllow useless for skills | |

**User's choice:** Re-keyed AlwaysAllowSession. Locked as D-10.

---

## Dead code (InstallFromOnlineAsync + DynamicSkillTool)

### Q1: InstallFromOnlineAsync

| Option | Description | Selected |
|--------|-------------|----------|
| Wire SEC-06 regex validation + ArgumentList + future readiness | Defensive; closes SEC-06 acceptance even though dead today | ✓ |
| Delete InstallFromOnlineAsync entirely | Skips SEC-06 acceptance criterion | |
| Leave untouched | Leaves documented vuln | |

**User's choice:** Wire validation + ArgumentList. Locked as D-11.

### Q2: DynamicSkillTool

| Option | Description | Selected |
|--------|-------------|----------|
| Delete DynamicSkillTool.cs entirely | Closes parallel attack surface; matches ToolRegistry comment | ✓ |
| Apply SEC-04 migration, leave registered as dead | Hardens code no one calls | |
| Leave untouched | Bit-rot risk | |

**User's choice:** Delete. Locked as D-12.

---

## Claude's Discretion

- XAML margins / padding / gradient choice for the new ScrollViewer row + DenylistHit banner (within Phase 1 D7 visual language).
- Exact Portuguese phrasing of floor-list reasons + DenylistReason banner (follow existing `"ACESSO NEGADO (SANDBOX): ..."` pattern, switch SANDBOX → FLOOR).
- Whether `CommandConfirmationWindow.ShowAsync(ctx)` helper lives as a static window method, a context method, or a tiny new `ModalConfirmationService` — pick whichever minimizes coupling.
- Exact extraction shape of `RunProcessAsync(Process)` helper in CommandService (D-05).

## Deferred Ideas

- **"Factory reset" button** — owner raised during gray-area selection. New capability for a future Settings UX milestone; outside Phase 3 boundary.
- Live API validation of `installArg` (manifest fetch) — future quality phase.
- AlwaysAllow persistence across sessions — future milestone; D-10's tuple key is serializer-ready.
- Unit tests for `CommandFloorList.Match` — tests milestone.
- Splitting `NativeTools.cs` god-file — refactor milestone.
- Re-enabling `DynamicSkillTool` — only if a future feature wants per-skill first-class tool entries.
- `/unlock_level` chat backdoor — accepted risk.
- Settings UI for individual floor entries — out of scope.
