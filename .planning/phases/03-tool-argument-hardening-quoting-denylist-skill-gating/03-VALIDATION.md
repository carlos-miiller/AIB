---
phase: 3
slug: tool-argument-hardening-quoting-denylist-skill-gating
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-31
---

# Phase 3 — Validation Strategy

> Per-phase validation contract. NFR-01 defers automated tests project-wide; this phase uses manual UAT codified in `03-VERIFICATION.md`. See RESEARCH.md `## Validation Architecture` for the canonical Phase Requirements → Test Map (12+ rows for SEC-04, SEC-05, SEC-06, Floor regression, Audit log, Build).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | None — manual UAT only (NFR-01 in REQUIREMENTS.md) |
| **Config file** | none |
| **Quick run command** | `dotnet build AIBWindows/AIB.csproj` (must report 0 errors, 0 new warnings) |
| **Full suite command** | Manual UAT scenarios codified in `03-VERIFICATION.md` (mirrors `01-VERIFICATION.md` shape) |
| **Estimated runtime** | Build ~30s; full UAT ~45min tester time |

---

## Sampling Rate

- **After every task commit:** `dotnet build AIBWindows/AIB.csproj` (must be 0/0)
- **After every plan wave:** `dotnet build` + spot-check completed UAT scenarios from `03-VERIFICATION.md` for tasks in that wave
- **Before `/gsd-verify-work 3`:** All UAT scenarios in `03-VERIFICATION.md` pass + tester sign-off + Phase 1 modal regression check
- **Max feedback latency:** ~30s for build; UAT batched at phase gate

---

## Per-Task Verification Map

> Canonical row source = RESEARCH.md §Validation Architecture "Phase Requirements → Test Map". Each task gets one row referencing the matching RESEARCH map entry. Plan-checker validates this table is complete after planning.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| {N}-01-XX | 01 | 1 | SEC-04 | T-3-Argv-Injection | Skill argv `; rm -rf …` is single literal element | manual UAT | `dotnet build` (build gate); UAT scenario from 03-VERIFICATION.md | ❌ W0 (VERIFICATION.md to author) | ⬜ pending |
| {N}-01-XX | 02 | 2 | SEC-05 | T-3-Floor-Bypass | L<7 quote-concat / EncodedCommand / alias rejected by floor after modal Allow | manual UAT | `dotnet build`; UAT scenarios | ❌ W0 | ⬜ pending |
| {N}-01-XX | 03 | 2 | SEC-06 | T-3-Skill-EoP | `execute_skill` L1 = "permissão negada"; L6 = modal w/ script body; `materialize_skill` L8 = modal w/ script body | manual UAT | `dotnet build`; UAT scenarios | ❌ W0 | ⬜ pending |
| {N}-01-XX | 03 | 2 | SEC-06 | T-3-InstallArg-Traversal | `InstallFromOnlineAsync` rejects `..badrepo`; accepts `owner/repo@1.0.0` | manual UAT | `dotnet build`; UAT scenario | ❌ W0 | ⬜ pending |
| {N}-01-XX | (any) | (any) | Build/regression | — | `grep -rn DynamicSkillTool AIBWindows/` returns 0 hits post-delete | build | `dotnet build` + grep | ✅ inline | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*
*Final task IDs filled after planner emits PLAN.md frontmatter.*

---

## Wave 0 Requirements

- [ ] Planner MUST include a task to author `.planning/phases/03-tool-argument-hardening-quoting-denylist-skill-gating/03-VERIFICATION.md` following Phase 1 `01-VERIFICATION.md` shape (header + scenarios + checkbox observed/pass-fail + tester sign-off). Canonical scenario set = RESEARCH.md §Validation Architecture map.
- [ ] Planner MUST include a task to provision a temporary "echo argv" skill in `~/.AIB/skills/_test_echo_args/` as part of SEC-04 UAT preparation. This skill is deleted at end of UAT.
- [ ] No test framework install — NFR-01 explicitly defers to a future "Tests + CI" milestone.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| LLM-supplied skill argv with metachars stays literal | SEC-04 | Requires WPF dispatcher round-trip + temp echo skill on disk; no headless harness | See RESEARCH.md §Validation Architecture row 1 — temp echo skill, observe argv echoed verbatim |
| `cmd.exe /c` carve-out preserved for `run_command` | SEC-04 | Behavioral assertion across UI + service layer | See RESEARCH.md row 2 — `dir C:\` round-trip, signature diff |
| Floor rejects `-EncodedCommand`, quote-concat, aliases at L<7 | SEC-05 | Requires modal+floor sequencing; tester observes amber AVISO banner | See RESEARCH.md rows 3–5 |
| L≥7 reaches modal verbatim, floor inactive | SEC-05 | Requires `/unlock_level 9` then modal observation | See RESEARCH.md row 5 |
| `execute_skill` / `materialize_skill` modal + level enforcement | SEC-06 | UI + level gate + script-body preview; visual confirmation needed | See RESEARCH.md rows 6–8 |
| `InstallFromOnlineAsync` regex hardening | SEC-06 | Smoke harness or debug call; no unit test framework | See RESEARCH.md row 9 — both inputs |
| Edit-in-place skill detection (content hash invalidates AlwaysAllow) | SEC-06 / D-10 | Mutate file on disk between exec calls; observe modal re-fire | See RESEARCH.md row 10 |
| Audit log schema: `allow_then_floor_deny`, `content_hash` null vs hex | SEC-05 / SEC-06 | Inspect JSONL audit file post-scenario | See RESEARCH.md rows 11–12 |

---

## Validation Sign-Off

- [ ] All planner tasks have UAT scenario references in 03-VERIFICATION.md OR are pure build-gate (covered by `dotnet build`)
- [ ] Sampling continuity: every wave has at least one task that exercises a UAT scenario or build gate (no wave passes without a build)
- [ ] Wave 0 covers all MISSING references — VERIFICATION.md scaffolding task scheduled in Wave 1; echo-argv skill provisioning scheduled before SEC-04 UAT
- [ ] No watch-mode flags (build is one-shot)
- [ ] Feedback latency < 30s (build) / batched at phase gate (UAT)
- [ ] `nyquist_compliant: true` set in frontmatter once per-task IDs are populated and VERIFICATION.md exists

**Approval:** pending
