# Phase 2: Key rotation + `.env` hardening — Research

**Researched:** 2026-05-30
**Domain:** Windows DPAPI credential vault wiring + dead-package removal + first-run UX
**Confidence:** HIGH (every claim verified by file Read against the working tree)

<user_constraints>
## User Constraints (from 02-CONTEXT.md)

### Locked Decisions

- **D-01 — Trigger lazily on hotkey, not app launch.** Tray-app pattern. `FirstRunWindow` opens *instead of* `ChatWindow` when the global hotkey fires AND the D-03 detector returns true.
- **D-02 — Dedicated `FirstRunWindow` XAML + code-behind.** New files: `AIBWindows/Views/FirstRunWindow.xaml` + `AIBWindows/Views/FirstRunWindow.xaml.cs`. Not reusing `SettingsWindow`.
- **D-03 — Detector is provider-aware.** Trigger condition: `CredentialService.RetrieveCredential("openai", "ApiKey")` returns an error sentinel (no entry) **AND** `settings.AiProvider != "Ollama"`. Ollama users never see FirstRunWindow.
- **D-04 — Cancel = app quits cleanly.** Cancel / X / Alt+F4 / Esc → `DialogResult = false` → `Application.Current.Shutdown()`. Tray icon remains. Audit-log line: `outcome=firstrun_cancelled`.
- **D-05 — Key validation is regex-only.** Pattern `^sk-[a-zA-Z0-9_-]{20,}$`. No network call on Save. Inline error: "Chave inválida — deve começar com `sk-`".
- **D-06 — Save target: `CredentialService` vault ONLY.** Writes to `CredentialService.StoreCredentialAsync("openai", "ApiKey", <value>)`. `settings.ApiKey` is overwritten with the literal `"use-vault"` sentinel. `OpenAIService.cs:657` and `:705` rewritten to honor the sentinel (vault lookup + fallback to `"placeholder"` on `ERRO`-prefixed return). No dual-write. Ollama path keeps `settings.ApiKey = "ollama"`.
- **D-07 — Visual style: match `ChatWindow` theme, centered.** Reuse ChatWindow's dark theme, gradient accents, resource dictionary. `WindowStartupLocation = CenterScreen`. Layout: title ("Bem-vindo ao AIB") → subtitle → provider radio group → conditional key input + Ollama model dropdown.
- **D-08 — Lifecycle: FirstRunWindow → ChatWindow strictly sequential.** ChatWindow hidden until FirstRunWindow Save closes successfully. ChatWindow's warmup + `/api/tags` fetches do NOT run until then.
- **D-09 — Ollama branch: model picker via `/api/tags`.** FirstRunWindow calls `SettingsService.GetOllamaModelsAsync(apiUrl)`. On Save: `settings.ModelName = selected` AND `settings.ShadowModelName = selected`. If `/api/tags` empty/throws: show "Ollama não detectado em `localhost:11434`" with `Tentar novamente` + `Usar qwen2.5:7b mesmo assim` fallback link.
- **D-10 — Delete `.env` on disk AND remove `DotNetEnv` package.** `rm .env` (live key off disk). Edit `AIBWindows/AIB.csproj` line 17: remove `DotNetEnv`. Add `.env.example` at repo root with placeholders.
- **D-11 — Force re-entry; do NOT auto-migrate.** One-shot migration step in `App.OnStartup` overwrites `settings.ApiKey = "use-vault"` UNCONDITIONALLY (regardless of current value). Old key value is NEVER copied into vault — rotation must happen first. Audit-log line: `outcome=migration_clear_apikey`.
- **D-12 — Rotation evidence: screenshot in `.planning/phases/02-key-rotation-env-hardening/evidence/`.** Filename `openai-console-rotation-2026-MM-DD.png` showing OLD key revoked + NEW key last4 only. Manual step.

### Claude's Discretion

- FirstRunWindow XAML layout details (margins, spacing, control sizing) within D-07's "match ChatWindow theme" constraint.
- Welcome-message copy polish.
- Audit-log entry **schema** for the new outcomes (`firstrun_cancelled`, `firstrun_saved`, `migration_clear_apikey`) — must follow existing JSONL pattern from Phase 1 D6.

### Deferred Ideas (OUT OF SCOPE)

- **AIBLinux key path migration** — `AIBLinux/app/openai_client.py:15` (`os.getenv("OPENAI_API_KEY")`) and `AIBLinux/main.py:6` (`load_dotenv()`) stay unchanged this phase. `AIBLinux/.env` left in place. Per owner: "keep Linux as it is for now". Live-key rotation in D-12 invalidates the literal in that file anyway.
- **Live API validation of key on Save** — D-05 chose regex-only. Could add `models.list` ping in a follow-up phase.
- **Polishing `SettingsWindow.KeyTextBox` into "Alterar chave" button** — optional; not strictly required for SEC-03 acceptance.
- **Audit-log retention / rotation** — same posture as Phase 1 D6 (deferred).
- **`/unlock_level` chat backdoor** — accepted risk per PROJECT.md; unchanged.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| **SEC-03** | OpenAI service-account key rotation + `.env` hardening. Runtime key resolution MUST prefer DPAPI-backed `CredentialService`. App MUST refuse to start with placeholder key. | This research locates the 2 OpenAIService call sites (verified), confirms zero `DotNetEnv` consumers (verified — only the package reference exists), maps the App.OnStartup insertion point for the D-11 migration, documents the exact `CredentialService` return-string contract for the D-03 detector, and identifies the hotkey handler at `App.xaml.cs:83` as the FirstRunWindow branch point. |
</phase_requirements>

## Summary

Phase 2 is a low-risk, high-operational-urgency phase: the actual security win is the manual key rotation (D-12) and the deletion of three on-disk `.env` files (D-10). The code change is small — two 4-line rewrites in `OpenAIService.cs`, one new ~150-line `FirstRunWindow` (XAML + code-behind), one App.OnStartup migration block, one package-reference deletion in `AIB.csproj`.

The research surfaced **two facts the CONTEXT.md did not account for**:

1. **There are THREE `.env` files on disk, not two.** CONCERNS.md says "two `.env` files"; `git status`-style enumeration finds: `.env` (531B), `AIBLinux/.env` (531B), and `AIBWindows/.env` (508B). All three contain the SAME live `sk-svcacct-…` key. The phase must delete all three (or D-12 rotation invalidates them, but the leak surface remains until they're gone).

2. **The existing `SettingsWindow` provider combo offers "Ollama" and "Google Gemini" — NOT "OpenAI".** CONTEXT.md D-07 says FirstRunWindow's radio group should be "Ollama / OpenAI". This is internally consistent (`AiProvider` is a free-form string field — the Settings combo just happens to enumerate two specific values), but the planner needs to decide whether FirstRunWindow's "OpenAI" branch sets `AiProvider = "OpenAI"` (a new value that SettingsWindow doesn't enumerate) or treats the OpenAI flow as "any non-Ollama provider" (keeping AiProvider blank/whatever, just relying on the key in vault). Recommendation: `AiProvider = "OpenAI"` and extend the SettingsWindow combo in a follow-up; see Open Questions.

**Primary recommendation:** Land the code changes in an order that keeps the working tree continuously valid for the running developer (D-11 migration before vault write means an existing dev's session is briefly broken until they re-enter the key — the FirstRunWindow detector handles this gracefully on next hotkey).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Key storage (at-rest secret) | DPAPI vault (CredentialService) | — | Already exists; D-06 designates as sole writer/reader for OpenAI key |
| Settings persistence (POCO) | Settings file (`profile.dat`, DPAPI-encrypted) | — | Existing `SettingsService` pattern; D-11 piggybacks on it |
| First-run UX | WPF Window (FirstRunWindow.xaml + .cs) | — | D-02: dedicated, not reused |
| First-run trigger gate | App.xaml.cs hotkey handler (`OnHotkeyDetected`) | — | D-01 + D-08: branch at `ToggleWindow` site |
| Migration logic (force re-entry) | App.OnStartup | — | D-11: one-shot, before ChatWindow constructed |
| Key resolution at request time | OpenAIService (`EnsureClient` + `AskStatelessAsync`) | CredentialService | D-06: sentinel branch in service, vault is data source |
| Audit trail | AuditLogService (existing JSONL writer) | — | Phase 1 D6 reused per CONTEXT.md "Claude's Discretion" |
| Dead-code purge | `AIB.csproj` package reference | filesystem (`.env` deletes) | D-10: pure deletion, no replacement service |
| Doc update (declared posture) | `Regras de Identidade/SEGURANCA.MD` | README.md | NFR-03: present-tense shipped behavior |

## Standard Stack

This phase does NOT introduce new dependencies. It REMOVES one (`DotNetEnv`) and reuses what already exists.

### Core (existing, verified by Read)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `System.Security.Cryptography.ProtectedData` | 10.0.7 | DPAPI primitive (`ProtectedData.Protect` / `Unprotect`) | `[VERIFIED: AIB.csproj:29]` Already used by `CredentialService.cs:42,105` and `SettingsService.cs:73,98` |
| `OpenAI` (official .NET SDK) | 2.10.0 | `ChatClient`, `ApiKeyCredential`, `OpenAIClientOptions` | `[VERIFIED: AIB.csproj:25]` Already constructed at `OpenAIService.cs:671,720` — no change to constructor surface |
| WPF + `Hardcodet.NotifyIcon.Wpf` | 2.0.1 (tray) | New FirstRunWindow uses WPF; tray remains for D-04 "Cancel keeps tray" | `[VERIFIED: AIB.csproj:18; App.xaml.cs:38]` |
| `NHotkey.Wpf` | 4.0.0 | Global hotkey wiring; the branch point for D-01 | `[VERIFIED: AIB.csproj:24; App.xaml.cs:61-65]` |

### Supporting (existing, verified)
| Service | Reuse Pattern |
|---------|---------------|
| `CredentialService.StoreCredentialAsync(system, key, value)` | D-06 Save target. `[VERIFIED: CredentialService.cs:21]` Returns string (success message or `ERRO…`). Caller checks prefix. |
| `CredentialService.RetrieveCredential(system, key)` | D-03 detector + D-06 read path. `[VERIFIED: CredentialService.cs:53]` Returns the value on hit; returns `"ERRO: Credencial não encontrada em nenhum sistema."` when file missing (line 79) — see Pitfall 1 for the global-fallback wrinkle. |
| `SettingsService.LoadSettings()` / `SaveSettings()` | D-11 migration uses `Load → mutate ApiKey → Save`. `[VERIFIED: SettingsService.cs:61,94]` DPAPI-encrypted round-trip already correct. |
| `SettingsService.GetOllamaModelsAsync(baseUrl)` | D-09 dropdown source. `[VERIFIED: SettingsService.cs:102]` Returns empty list on failure (catch-all at line 128). No throw — safe to await directly. |
| `AuditLogService.AppendAsync(object)` | New outcomes log through this. `[VERIFIED: AuditLogService.cs:34]` Fire-and-forget, `SemaphoreSlim`-guarded, JSONL append. |
| `DirectoryService.DataDir` | Vault path derives from `~/.AIB/credentials/openai.bin`. `[VERIFIED: DirectoryService.cs:11; CredentialService.cs:13]` |

### Removed
| Library | Version | Action | Rationale |
|---------|---------|--------|-----------|
| `DotNetEnv` | 3.1.1 | DELETE the `<PackageReference>` line | `[VERIFIED]` Zero call sites in `AIBWindows/` — `Grep("DotNetEnv\|Env\.Load\|using DotNetEnv", AIBWindows)` returns one hit (the csproj line itself). Build remains clean with the package gone. |

**Installation:** N/A — no new packages.

**Version verification:** All package versions already pinned in `AIB.csproj`. No new fetches needed.

## Package Legitimacy Audit

> Not applicable. Phase 2 installs ZERO new packages. The only package-graph change is the deletion of `DotNetEnv 3.1.1`. Removal cannot introduce supply-chain risk.

## Architecture Patterns

### System Architecture Diagram

```
                                  ┌─────────────────────────────────────┐
                                  │     User physical action            │
                                  │     (Ctrl+Shift+Space hotkey)       │
                                  └────────────────┬────────────────────┘
                                                   │
                                                   ▼
                ┌──────────────────────────────────────────────────────────────────┐
                │  App.xaml.cs  OnHotkeyDetected (line 83)                         │
                │  ─────────────────────────────────────────                       │
                │  NEW BRANCH (Phase 2):                                           │
                │    if (NeedsFirstRun(settings))   ◄──── D-03 detector            │
                │        ShowFirstRunWindow();                                     │
                │    else                                                          │
                │        _chatWindow?.ToggleWindow();   (existing path)            │
                └─────────┬────────────────────────────────────────┬───────────────┘
                          │                                        │
              detector returns true                     detector returns false
                          │                                        │
                          ▼                                        ▼
       ┌─────────────────────────────────┐         ┌──────────────────────────────┐
       │  FirstRunWindow (NEW)           │         │  ChatWindow.ToggleWindow     │
       │  ─────────────────────          │         │  (unchanged)                 │
       │  Provider radio:                │         └──────┬───────────────────────┘
       │    ○ Ollama  ○ OpenAI           │                │
       │  ─Ollama branch:                │                │ User sends message
       │    ComboBox ← GetOllamaModels   │                ▼
       │  ─OpenAI branch:                │      ┌───────────────────────────────┐
       │    TextBox + regex validate     │      │  OpenAIService.EnsureClient   │
       │  ─Cancel/X/Esc/Alt-F4 →         │      │  OpenAIService.AskStateless   │
       │    Application.Shutdown()       │      │  ────────────────────────     │
       │    audit: firstrun_cancelled    │      │  NEW LOGIC (D-06):            │
       │  ─Save click:                   │      │    apiKey = settings.ApiKey;  │
       │    1. validate regex            │      │    if (apiKey=="use-vault") { │
       │    2. CredentialService.Store   │      │      apiKey = Credential      │
       │       ("openai","ApiKey",val)   │      │         .RetrieveCredential   │
       │    3. settings.ApiKey ←         │      │         ("openai","ApiKey");  │
       │       "use-vault"               │      │      if (apiKey.StartsWith   │
       │    4. Save settings             │      │         ("ERRO"))             │
       │    5. audit: firstrun_saved     │      │        apiKey="placeholder";  │
       │    6. Close → Show ChatWindow   │      │    }                          │
       └─────────────────────────────────┘      └────────┬──────────────────────┘
                                                         │
                                                         ▼
                                            ┌─────────────────────────────┐
                                            │  CredentialService          │
                                            │  ── DPAPI ProtectedData     │
                                            │  ~/.AIB/credentials/        │
                                            │     openai.bin              │
                                            └─────────────────────────────┘

  ┌──────────────────────────────────────────────────────────────────────────┐
  │  ONE-SHOT BOOT MIGRATION (D-11) — App.OnStartup, runs every boot:        │
  │  ─────────────────────────────────────────────────────────────           │
  │  var s = settingsService.LoadSettings();                                 │
  │  if (s.ApiKey != "use-vault" && s.ApiKey != "ollama") {                  │
  │      s.ApiKey = "use-vault";                                             │
  │      settingsService.SaveSettings(s);                                    │
  │      _ = AuditLogService.AppendAsync(new {                               │
  │          ts=..., outcome="migration_clear_apikey",                       │
  │          previous_key_present=true });                                   │
  │  }                                                                       │
  │  (Existing users with sk-svcacct-… → wiped; D-03 detector triggers       │
  │   on next hotkey; user re-pastes the ROTATED key.)                       │
  └──────────────────────────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | File | Responsibility (post-Phase 2) |
|-----------|------|-------------------------------|
| Hotkey handler | `AIBWindows/App.xaml.cs:83-87` | Branch on D-03 detector; show FirstRunWindow OR ChatWindow |
| Boot migration | `AIBWindows/App.xaml.cs` `OnStartup` (lines 26-81) | One-shot: overwrite `ApiKey` to `"use-vault"` and audit |
| First-run UX | `AIBWindows/Views/FirstRunWindow.{xaml,xaml.cs}` (NEW) | Provider picker + key/model input + regex validate + audit + Save → vault |
| First-run trigger detector | helper (NEW) — recommend in `AIBWindows/Services/FirstRunService.cs` or inline static helper in `App.xaml.cs` | `(s.AiProvider != "Ollama") && RetrieveCredential("openai","ApiKey").StartsWith("ERRO")` |
| Key resolution at request | `AIBWindows/Services/OpenAIService.cs:651-690` (`AskStatelessAsync`) and `:696-725` (`EnsureClient`) | Honor `"use-vault"` sentinel; fall back to `"placeholder"` on `ERRO`-prefixed return |
| Vault | `AIBWindows/Services/CredentialService.cs` | UNCHANGED. Reuse `StoreCredentialAsync` + `RetrieveCredential`. |
| Settings storage | `AIBWindows/Services/SettingsService.cs` | UNCHANGED. Reuse `LoadSettings` + `SaveSettings`. |
| Audit | `AIBWindows/Services/AuditLogService.cs` | UNCHANGED. New outcomes: `firstrun_cancelled`, `firstrun_saved`, `firstrun_invalid_key`, `migration_clear_apikey`. |
| Package graph | `AIBWindows/AIB.csproj:17` | DELETE `DotNetEnv` reference |
| Filesystem | `.env`, `AIBWindows/.env`, `AIBLinux/.env` | DELETE all three on disk |
| Filesystem | `.env.example` (root, NEW) | Create with placeholders |
| Doc | `Regras de Identidade/SEGURANCA.MD` | Update §2 to describe DPAPI vault as the canonical OpenAI key store |
| Doc | `README.md` | Add key-setup instructions pointing to FirstRunWindow |

### Recommended Project Structure
No structural change. New files slot into existing folders:
```
AIBWindows/
├── Views/
│   └── FirstRunWindow.xaml        # NEW
│   └── FirstRunWindow.xaml.cs     # NEW
├── Services/
│   └── (optional) FirstRunService.cs   # NEW — only if detector helper extracted
│                                       # (recommend keep inline in App.xaml.cs
│                                       #  for a 6-line helper; not worth a file)
└── AIB.csproj                    # EDIT: delete DotNetEnv line
.env                              # DELETE
AIBWindows/.env                   # DELETE
AIBLinux/.env                     # DELETE (CONCERNS.md missed this one)
.env.example                      # NEW
```

### Pattern 1: Sentinel-string for "fetch from elsewhere"

**What:** A POCO field stores not the value but a literal sentinel like `"use-vault"` that the consumer interprets as "look this up at runtime via the dedicated provider."

**When to use:** When you want the existing serialization path (DPAPI-encrypted `profile.dat`) to remain the single source of truth for *configuration*, but want a different store for the actual secret.

**Why this over double-write:** Single source of truth. No sync bugs. Settings file backups don't accidentally include the secret. Existing `profile.dat` schema unchanged (NFR-04 compliance).

**Example (the D-06 pattern, verbatim from CONTEXT.md):**
```csharp
// In OpenAIService.AskStatelessAsync (line 657) and EnsureClient (line 705):
string apiKey = settings.ApiKey;
if (apiKey == "use-vault") {
    apiKey = CredentialService.RetrieveCredential("openai", "ApiKey");
    if (apiKey.StartsWith("ERRO")) {
        // Vault missing — should never reach here if D-03 detector ran.
        // Treat as deny: return error string upstream so the ChatWindow
        // can show "Configure sua chave em Configurações".
        apiKey = "placeholder";
    }
}
```

### Pattern 2: Lazy-trigger UX gate (D-01 tray-app pattern)

**What:** The app boots silently into the tray. The first time the user invokes the action (hotkey), a detector decides whether the action UI or a setup UI fires.

**When to use:** Tray apps where the app's value proposition is "be ready when I need it." A modal popup at boot violates this contract.

**Example (the D-01 + D-08 sequencing, derived from existing `App.xaml.cs:83`):**
```csharp
private void OnHotkeyDetected(object? sender, HotkeyEventArgs e)
{
    e.Handled = true;
    var settings = _settingsService.LoadSettings();
    if (NeedsFirstRun(settings))
    {
        ShowFirstRunWindow(settings);    // strictly sequential — does NOT
        return;                          // call ToggleWindow until Save success
    }
    _chatWindow?.ToggleWindow();
}

private static bool NeedsFirstRun(UserAppSettings s)
{
    if (s.AiProvider == "Ollama") return false;
    string r = CredentialService.RetrieveCredential("openai", "ApiKey");
    return r.StartsWith("ERRO");
}
```

### Pattern 3: One-shot boot migration via existing serializer

**What:** Phase 1 D8 established that schema migrations happen "in place" via the existing `LoadSettings → mutate → SaveSettings` round-trip — no migration framework, no version field, just a one-shot mutation at OnStartup.

**Example (the D-11 migration step):**
```csharp
// In App.OnStartup, between line 33 (LoadSettings) and line 36 (new ChatWindow):
if (settings.ApiKey != "use-vault" && settings.ApiKey != "ollama")
{
    settings.ApiKey = "use-vault";
    settingsService.SaveSettings(settings);
    _ = AuditLogService.AppendAsync(new {
        ts = DateTime.UtcNow.ToString("o"),
        outcome = "migration_clear_apikey",
        previous_key_present = true
    });
}
```

The "ollama" exception keeps an Ollama-only user from being needlessly bounced into FirstRunWindow on upgrade.

### Anti-Patterns to Avoid

- **Don't auto-copy the existing `settings.ApiKey` value into the vault.** D-11 explicitly forbids this. The rationale: the existing key is the LEAKED key (`sk-svcacct-…` from the deleted `.env` files); copying it preserves the security hole, just relocates it. Rotation MUST happen first.
- **Don't add a version field to `UserAppSettings` for the migration.** Phase 1 D8 + this phase D-11 both use "missing/sentinel → mutate → save" — keep that pattern. A schema version creates ceremony with no benefit (no rollback path exists; the app does not need to know it migrated).
- **Don't reuse `SettingsWindow` for first-run.** D-02 is explicit. The shapes of the flows differ enough (welcome copy, provider radio vs combo, no "Cancel keeps existing settings" semantic, Esc=Shutdown vs Esc=Close) that reuse causes more bugs than DRY saves.
- **Don't run the D-11 migration silently in a `try/catch` that swallows.** CONCERNS.md flagged "Pervasive silent `catch { }`" as a HIGH tech-debt issue. The migration must fail loudly to console + audit on error; the app then degrades gracefully (FirstRunWindow handles the missing-vault case anyway).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| At-rest secret encryption | Custom AES wrapper | `ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)` | DPAPI binds the key to the Windows user account; no key management; aligns with `SEGURANCA.MD §2`. Already used. |
| Vault key/value store | New SQLite/JSON schema | `CredentialService` — already there | `[VERIFIED]` Fully working today, used by `manage_vault` tool. Just call `StoreCredentialAsync` / `RetrieveCredential`. |
| Settings persistence | New file | `SettingsService.LoadSettings/SaveSettings` | DPAPI-encrypted `profile.dat` already exists; `UserAppSettings` POCO has the field needed (`ApiKey`). |
| First-run state detection | New marker file (e.g., `.firstrun-done`) | D-03 detector (vault lookup) | The vault IS the state. No new file. Self-healing: clear the vault → FirstRunWindow returns. |
| Audit log writer | New writer | `AuditLogService.AppendAsync` | Phase 1 already shipped this; SemaphoreSlim-guarded, JSONL, fire-and-forget. |
| Ollama model enumeration | New HTTP wrapper | `SettingsService.GetOllamaModelsAsync` | `[VERIFIED: SettingsService.cs:102]` Already handles failure → empty list. |
| Regex validation framework | FluentValidation, etc. | Inline `Regex.IsMatch(input, @"^sk-[a-zA-Z0-9_-]{20,}$")` | D-05 explicitly chose regex-only. One line. |
| Modal lifecycle | `Window.Closing` workarounds | Set `IsCancel="True"` on Cancel button + `DialogResult = false` in Cancel handler | Phase 1's D4 pattern. WPF handles X/Alt+F4/Esc automatically when DialogResult is set. |

**Key insight:** The entire vault and DPAPI subsystem already exists and is correct. This phase is 90% wiring (and 10% UX). Resist the urge to "improve" `CredentialService` while you're in there — that's out of scope (see MEDIUM concerns about global-fallback leak, scheduled for Phase 4 if it fits).

## Runtime State Inventory

> Phase 2 is partially a migration (existing users with `settings.ApiKey = "sk-svcacct-…"` need to be moved to the sentinel). Inventory below covers what carries the secret today.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| **Stored data** | `~/.AIB/profile.dat` — DPAPI-encrypted, contains `UserAppSettings.ApiKey` field with the live `sk-svcacct-…` value on existing dev installs. **Code edit + data migration.** | D-11 one-shot: overwrite the field to `"use-vault"` on next boot. NOT a separate migration script — runs inline in `App.OnStartup`. |
| **Stored data** | `~/.AIB/credentials/openai.bin` — DPAPI-encrypted, EMPTY today (no Phase 2 user has written it yet). | None. FirstRunWindow Save creates it. |
| **Stored data** | `~/.AIB/memory.db` (LiteDB), `~/.AIB/chat_history.json`, `~/.AIB/logs/audit.log` — verified do NOT contain the key. | None. |
| **Live service config** | OpenAI service-account key in OpenAI console — live on disk + previously committed-in-spirit (gitignored but present in working tree). | D-12 manual rotation in OpenAI console. Screenshot evidence. |
| **OS-registered state** | Global hotkey registration via `NHotkey.Wpf` at `App.xaml.cs:61`. Registers `Ctrl+Shift+Space` → `OnHotkeyDetected`. The handler body changes (NEW branch for FirstRunWindow) but the registration stays. | None. Re-registration handled automatically on next launch. |
| **OS-registered state** | Tray icon registration via `Hardcodet.NotifyIcon.Wpf` at `App.xaml.cs:38`. | None. Survives FirstRunWindow lifecycle per D-04. |
| **Secrets / env vars** | THREE `.env` files on disk (CONCERNS.md says two, working tree has three: `.env`, `AIBWindows/.env`, `AIBLinux/.env`). All gitignored (line 11 of `.gitignore`). All contain `OPENAI_API_KEY=sk-svcacct-…`. **Filesystem delete.** | D-10 extended: `rm` all THREE, not two. (Linux file removal does NOT change `AIBLinux/main.py` behavior — `load_dotenv()` silently does nothing when file absent — so Linux deferral is unaffected.) |
| **Secrets / env vars** | No CI/CD env vars carry the key (no CI/CD pipeline exists per `.planning/codebase/TESTING.md`). | None. |
| **Build artifacts / installed packages** | `AIBWindows/bin/`, `AIBWindows/obj/` — may contain compiled `DotNetEnv.dll`. After package removal, `dotnet build` rebuilds without it. | `dotnet build` post-csproj edit verifies. No special clean needed. |
| **Build artifacts / installed packages** | `AIBWindows/AIB.csproj:17` carries `<PackageReference Include="DotNetEnv" Version="3.1.1" />`. | D-10: delete the line. |

**Nothing found in category:** No Windows Task Scheduler entries, no pm2/systemd registrations, no SOPS keys, no Datadog/Tailscale/Cloudflare configs reference the key (verified by working knowledge — this is a single-machine WPF app).

## Common Pitfalls

### Pitfall 1: `CredentialService.RetrieveCredential` has TWO failure modes — both return `ERRO`-prefixed strings, but one of them performs a GLOBAL search across all systems before failing

**What goes wrong:** `RetrieveCredential("openai", "ApiKey")` first checks if `~/.AIB/credentials/openai.bin` exists. If NOT, it falls into a "global search" loop (lines 65-77) that iterates EVERY `.bin` file in `credentials/` and returns the first one that has a key named `"ApiKey"`. If some other system (say, a user stored a "github" credential with key="ApiKey" in `github.bin`) happens to have that key, the OpenAI lookup returns the GitHub key.

**Why it happens:** The fallback is documented in CONCERNS.md as the MEDIUM finding "Vault retrieval falls back to global search across all systems". Scheduled to be fixed in Phase 4 (it might piggyback). For Phase 2, the practical implication is:

**Detector contract verification:**
| Scenario | What `RetrieveCredential("openai", "ApiKey")` returns | D-03 detector verdict |
|----------|-------------------------------------------------------|----------------------|
| Fresh install, no `credentials/` dir | `"ERRO: Credencial não encontrada em nenhum sistema."` (line 79) | starts with `ERRO` → triggers FirstRunWindow ✓ |
| `credentials/` dir exists but no `openai.bin` AND no other `.bin` has key `"ApiKey"` | `"ERRO: Credencial não encontrada em nenhum sistema."` (line 79) | starts with `ERRO` → triggers ✓ |
| `credentials/` dir exists but no `openai.bin` AND some OTHER `.bin` HAS key `"ApiKey"` | the OTHER system's value (line 75) | does NOT start with `ERRO` → DOES NOT trigger ✗ — **FALSE NEGATIVE** |
| `openai.bin` exists, key absent | `"ERRO: Chave não encontrada para este sistema."` (line 92) | starts with `ERRO` → triggers ✓ |
| `openai.bin` exists, key present | the value | does NOT start with `ERRO` → does NOT trigger ✓ |
| DPAPI decrypt fails | `"ERRO: Falha ao descriptografar arquivo."` (line 83) | starts with `ERRO` → triggers ✓ |
| Exception in retrieve | `"ERRO ao recuperar credencial: {ex.Message}"` (line 96) | starts with `ERRO` → triggers ✓ |

**How to avoid:** The false-negative window is small (it requires a user to have stored a different system's credential with the literal key name `"ApiKey"` before ever running an Phase-2+ build). Recommend documenting in PLAN.md that the D-03 detector implementation MAY want to bypass the global-fallback by checking `File.Exists("~/.AIB/credentials/openai.bin")` directly OR by using a UNIQUE key name like `"OpenAIApiKey"` for this phase's writes. **Recommended:** keep key=`"ApiKey"` per D-06 (it's the canonical name) and rely on the rarity of the false negative; flag for Phase 4 follow-up.

**Warning signs:** During testing, if a dev has been using `manage_vault` tool in chats and stored "ApiKey"-named credentials for other systems, FirstRunWindow may unexpectedly NOT trigger.

### Pitfall 2: `CredentialService` writes `[DEBUG-COFRE]` lines to console with key/system names

**What goes wrong:** Every `RetrieveCredential` and `StoreCredentialAsync` call prints lines like:
```
[DEBUG-COFRE] Buscando sistema: openai | Chave: ApiKey
[DEBUG-COFRE] SUCESSO: Chave 'ApiKey' encontrada.
```
to `Console.WriteLine`. After Phase 2 ships, EVERY OpenAIService request from a non-Ollama user triggers `RetrieveCredential` inside `EnsureClient` / `AskStatelessAsync` — so the console window (if attached) emits these lines on every chat turn.

**Why it happens:** This is the MEDIUM finding "Vault retrieval prints debug info" in CONCERNS.md, also scheduled for Phase 4. The phase-2 wire-up amplifies the noise.

**How to avoid:** Out of scope for THIS phase but the planner should mention it: the noise is harmless (it does not log the VALUE, only the key name + system name) but it's worth a courtesy heads-up in the PR description. If it turns out to be too noisy in practice, gate behind `settings.VerboseConsoleLogging` (existing field) as a one-line follow-up.

**Warning signs:** Console output during chat turns gets noticeably noisier post-phase-2.

### Pitfall 3: Migration runs at OnStartup, but the user has the app open already at the moment they update — `SaveSettings` mid-session blows away in-memory state

**What goes wrong:** The D-11 migration runs in `App.OnStartup`, which only fires on a fresh process launch — so this is NOT an issue for an updating user (their old process exits before the new one starts). But during DEVELOPMENT, a dev re-running `dotnet run` while another instance is open could cause two processes to both touch `profile.dat` simultaneously.

**Why it happens:** `SettingsService.SaveSettings` uses `File.WriteAllBytes` with no lock. The previous Phase 1 work didn't address this and treated it as acceptable (`profile.dat` is small, write is atomic at the OS level for small files, last-writer-wins is fine for a single-user app).

**How to avoid:** No code change needed. Document in VERIFICATION.md that the dev should fully close any running AIB instance (tray → Sair) before launching the post-phase-2 build for the first time.

**Warning signs:** Race between two AIB processes — dev sees apparently-vanishing settings.

### Pitfall 4: Removing `DotNetEnv` package while a stale `bin/`/`obj/` carries the DLL

**What goes wrong:** Editing `AIB.csproj` deletes the package reference, but `AIBWindows/bin/Debug/net8.0-windows10.0.19041.0/DotNetEnv.dll` may persist on disk until a clean build. If any runtime code path were to reflect on the assembly (none in the current code, per Grep), behavior would diverge from a fresh clone.

**Why it happens:** Incremental MSBuild does not delete artifacts of removed packages — it only adds new ones.

**How to avoid:** After editing `AIB.csproj`, the planner should include a task: `dotnet clean AIBWindows/AIB.csproj && dotnet build AIBWindows/AIB.csproj` to ensure a from-scratch build. Verify by `dir AIBWindows/bin/Debug/.../DotNetEnv.dll` returning not-found.

**Warning signs:** Phantom DLLs in `bin/` post-removal.

### Pitfall 5: `.gitignore` ignores `.env` but does NOT explicitly NOT-ignore `.env.example`

**What goes wrong:** `.gitignore:11` has `.env` — this pattern in gitignore semantics matches `.env` and `.env*` (verify: gitignore patterns without trailing `/` match files and dirs). Specifically, `.env` matches only the file literally named `.env`, NOT `.env.example` (which is a different name). So `.env.example` is NOT inadvertently ignored.

**Verification:** `git check-ignore .env.example` would return 0 (not ignored). Confirmed by Read of `.gitignore`: only `.env` is listed; no `.env*` glob.

**How to avoid:** No action needed. The current `.gitignore` correctly allows `.env.example` to be tracked. The planner should `git add .env.example` and verify it shows up in `git status`.

**Warning signs:** N/A — false alarm; raised only to preempt a planner question.

### Pitfall 6: FirstRunWindow's `Application.Current.Shutdown()` on cancel kills the tray icon Dispose path

**What goes wrong:** `App.OnExit` (line 89) calls `_notifyIcon?.Dispose()`. `Application.Current.Shutdown()` triggers `OnExit` automatically. But IF the FirstRunWindow handler calls `Shutdown()` from a non-UI thread, or if a Cancel handler doesn't first call `Hide()` on the window, the tray icon disposal may race with the window close and leave a ghost tray icon until mouse hover refreshes the tray.

**Why it happens:** Known Windows tray-icon quirk (the tray shell doesn't poll for dead processes; it only refreshes on hover). `Hardcodet.NotifyIcon.Wpf` 2.0.1 handles disposal correctly when `Dispose()` is called on the UI thread.

**How to avoid:** FirstRunWindow's Cancel handler calls `Close()` first (DialogResult=false), THEN the calling site (App.xaml.cs) checks DialogResult and calls `Application.Current.Shutdown()`. WPF guarantees this runs on the UI thread. The Phase 1 modal already uses this exact lifecycle correctly — copy that pattern.

**Warning signs:** "Ghost" AIB tray icon after Cancel that disappears on hover.

## Code Examples

Verified patterns from the existing codebase (sources are file:line references):

### Vault Read (existing call pattern; D-06 detector + resolver use it)
```csharp
// Source: AIBWindows/Services/CredentialService.cs:53 (signature)
//         AIBWindows/Services/OpenAIService.cs:657 (current ApiKey read site to be modified)

// D-06 rewrite at OpenAIService.cs:657 (and identical at :705):
string apiKey = settings.ApiKey;
if (apiKey == "use-vault") {
    apiKey = CredentialService.RetrieveCredential("openai", "ApiKey");
    if (apiKey.StartsWith("ERRO")) {
        apiKey = "placeholder";  // OpenAI SDK will fail loudly with auth error;
                                 // user sees error in chat → opens Settings → re-runs FirstRunWindow
    }
}
// existing line 670/719: var localClient = new ChatClient(modelName, new ApiKeyCredential(apiKey), options);
```

### Vault Write (D-06 Save path)
```csharp
// Source: AIBWindows/Services/CredentialService.cs:21 (signature)
//         New code in FirstRunWindow.xaml.cs Save_Click handler

private async void Save_Click(object sender, RoutedEventArgs e)
{
    string key = KeyTextBox.Text.Trim();
    if (!Regex.IsMatch(key, @"^sk-[a-zA-Z0-9_-]{20,}$"))
    {
        ErrorLabel.Text = "Chave inválida — deve começar com `sk-`";
        ErrorLabel.Visibility = Visibility.Visible;
        _ = AuditLogService.AppendAsync(new {
            ts = DateTime.UtcNow.ToString("o"),
            outcome = "firstrun_invalid_key",
            provider = "OpenAI"
        });
        return;
    }

    string result = await CredentialService.StoreCredentialAsync("openai", "ApiKey", key);
    if (result.StartsWith("ERRO"))
    {
        ErrorLabel.Text = $"Erro ao salvar: {result}";
        ErrorLabel.Visibility = Visibility.Visible;
        return;
    }

    var settings = _settingsService.LoadSettings();
    settings.AiProvider = "OpenAI";     // see Open Question Q1 — confirm with planner
    settings.ApiKey = "use-vault";
    _settingsService.SaveSettings(settings);

    _ = AuditLogService.AppendAsync(new {
        ts = DateTime.UtcNow.ToString("o"),
        outcome = "firstrun_saved",
        provider = "OpenAI",
        key_last4 = key.Length >= 4 ? key[^4..] : "----"
    });

    DialogResult = true;
    Close();
}
```

### Boot Migration (D-11)
```csharp
// Source: AIBWindows/App.xaml.cs OnStartup (insertion between existing lines 33 and 36)
//         Reuses SettingsService.SaveSettings (line 94) + AuditLogService.AppendAsync (line 34)

DirectoryService.EnsureDirectories();
var settingsService = new SettingsService();
var settings = settingsService.LoadSettings();
DirectoryService.ApplyFromSettings(settings);

// D-11 one-shot migration: force re-entry by clearing any non-sentinel, non-Ollama key.
if (settings.ApiKey != "use-vault" && settings.ApiKey != "ollama")
{
    bool hadKey = !string.IsNullOrEmpty(settings.ApiKey);
    settings.ApiKey = "use-vault";
    settingsService.SaveSettings(settings);
    _ = AuditLogService.AppendAsync(new {
        ts = DateTime.UtcNow.ToString("o"),
        outcome = "migration_clear_apikey",
        previous_key_present = hadKey
    });
    Console.WriteLine("[MIGRATION] settings.ApiKey replaced with 'use-vault' sentinel (D-11).");
}

_chatWindow = new ChatWindow();
// … rest unchanged …
```

### Hotkey Branch (D-01 + D-08)
```csharp
// Source: AIBWindows/App.xaml.cs:83 (existing OnHotkeyDetected)
//         Reuses SettingsService.LoadSettings + CredentialService.RetrieveCredential

private void OnHotkeyDetected(object? sender, HotkeyEventArgs e)
{
    e.Handled = true;
    var settings = _settingsService.LoadSettings();
    if (NeedsFirstRun(settings))
    {
        ShowFirstRunWindow();
        return;  // D-08: ChatWindow does NOT show until FirstRunWindow.Save succeeds
    }
    _chatWindow?.ToggleWindow();
}

private static bool NeedsFirstRun(UserAppSettings s)
{
    if (s.AiProvider == "Ollama") return false;
    string r = CredentialService.RetrieveCredential("openai", "ApiKey");
    return r.StartsWith("ERRO");
}

private void ShowFirstRunWindow()
{
    var win = new FirstRunWindow();
    bool? ok = win.ShowDialog();
    if (ok == true)
    {
        _chatWindow?.ToggleWindow();   // D-08 sequencing: show chat only after Save
    }
    else
    {
        _ = AuditLogService.AppendAsync(new {
            ts = DateTime.UtcNow.ToString("o"),
            outcome = "firstrun_cancelled"
        });
        Current.Shutdown();   // D-04: cancel = clean shutdown
    }
}
```

### Audit-log Schema (planner discretion per CONTEXT.md)

Existing schema (from `NativeTools.cs:451-460` — `BuildEntry`):
```json
{"ts":"2026-05-30T14:32:11Z","tool":"run_command","cmd":"git status","level":5,"cwd":"...","outcome":"allow","always_allow":false}
```

Recommended Phase 2 schema (anonymous objects, JSONL one-per-line):
```json
{"ts":"2026-05-30T14:50:01Z","outcome":"firstrun_cancelled"}
{"ts":"2026-05-30T14:50:33Z","outcome":"firstrun_invalid_key","provider":"OpenAI"}
{"ts":"2026-05-30T14:51:05Z","outcome":"firstrun_saved","provider":"OpenAI","key_last4":"YIA"}
{"ts":"2026-05-30T14:00:00Z","outcome":"migration_clear_apikey","previous_key_present":true}
```

**Schema rules:**
- `ts` (ISO 8601 UTC) is required on every entry.
- `outcome` (string) is required.
- All other fields are optional — JSONL tolerates heterogeneous shapes per line.
- **NEVER include the full key.** `key_last4` is the canonical "fingerprint" field for the rotated key per D-12 (matches the screenshot evidence format).

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `.env` file with plaintext `OPENAI_API_KEY=…` loaded via `DotNetEnv` | DPAPI-encrypted `~/.AIB/credentials/openai.bin` via `CredentialService` | This phase (2026-05) | Eliminates plaintext secret on disk; binds key to Windows user |
| Settings UI as the only key-entry path | Dedicated FirstRunWindow on first hotkey | This phase | Guarantees user explicit action at install/upgrade; no silent fallback |
| Auto-migrate (copy on first detected schema gap) | Force re-entry via sentinel | This phase (D-11 decision) | Forces user to rotate (the existing key is leaked); prevents silently preserving the leak |
| Linux parity at the key path | Deferred to feature-parity milestone | This phase (Deferred Ideas) | Linux `AIBLinux/app/openai_client.py:15` still uses `os.getenv` — acceptable per "lite client" posture |

**Deprecated/outdated (post-phase):**
- `DotNetEnv` package — REMOVED. Zero call sites today; dead weight that encouraged plaintext-`.env` antipattern.
- `settings.ApiKey` as a value field for OpenAI — repurposed to a sentinel field (`"use-vault"`, `"ollama"`, or `"placeholder"`).
- README key-setup instructions (currently absent for Windows; mentions only `Modelfile-Core` references that were deleted per CONCERNS.md LOW finding) — must be replaced with FirstRunWindow walkthrough.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The OpenAI SDK 2.10.0 `ChatClient` continues to accept `new ApiKeyCredential("placeholder")` and fail only at the first request, not at construction. | Pattern 1 fallback | Low — if it rejects at construction, the fallback path would throw before `EnsureClient` returns. Existing `OpenAIService.cs:716-720` already uses `"placeholder"` as a sentinel for "no key" — proven path. |
| A2 | The `[DEBUG-COFRE]` console noise from `CredentialService.RetrieveCredential` is acceptable per-chat-turn after Phase 2. | Pitfall 2 | Low — output is harmless; user can disable via `VerboseConsoleLogging`. |
| A3 | The D-11 migration running every boot (because `if (ApiKey != "use-vault" && != "ollama")`) is idempotent and cheap. After the first run, `ApiKey == "use-vault"` and the block is skipped. | Pattern 3 | Verified by code reading — single `LoadSettings` per boot, branch is O(1) string compare. |
| A4 | `Application.Current.Shutdown()` from a UI-thread Cancel handler does NOT leak the tray icon (it calls `App.OnExit` which disposes it). | Pitfall 6 | Low — Phase 1 modal uses identical lifecycle. |
| A5 | `git check-ignore .env.example` would return 0. The `.gitignore` line `.env` matches only the literal `.env` filename, not the `.env.example` filename. | Pitfall 5 | Verified by Read of `.gitignore` (no `.env*` glob; only `.env`). Standard git-ignore semantics for non-glob patterns. |
| A6 | The `Cancel = Shutdown` semantic (D-04) is intentional even though the user might just want to dismiss and try later. | Locked decision | Per CONTEXT.md D-04 rationale ("without a key the app simply does not provide a UI"). Locked, not researched. |
| A7 | The detector check is fast enough to run on EVERY hotkey press (D-03: "the check runs on every hotkey invocation until the vault contains a key OR the provider is `Ollama`"). | Pattern 2 | `CredentialService.RetrieveCredential` does a File.Exists + DPAPI decrypt — sub-10ms on warm SSD. Acceptable for hotkey latency. |

## Open Questions

1. **What value does `settings.AiProvider` take when the user picks "OpenAI" in FirstRunWindow?**
   - **What we know:** `SettingsWindow.xaml:102-105` only enumerates `Ollama` and `Google Gemini` in its provider ComboBox. CONTEXT.md D-07 says FirstRunWindow's radio group is "Ollama / OpenAI". Setting `AiProvider = "OpenAI"` introduces a value the SettingsWindow combo cannot display.
   - **What's unclear:** Should we (a) keep `AiProvider = "Ollama"` for the Ollama branch and `AiProvider = "OpenAI"` for the OpenAI branch, extending the SettingsWindow combo in this phase too — or (b) set `AiProvider` to anything-non-Ollama and treat any non-Ollama value as "uses the vault"?
   - **Recommendation:** Option (a) — extend `SettingsWindow.xaml` combo to include "OpenAI" alongside the existing "Ollama" and "Google Gemini". One line of XAML, zero risk. Plan task: "Update SettingsWindow.xaml provider combo to include `<ComboBoxItem Content=\"OpenAI\"/>`". Defer to discuss-phase if planner disagrees; flag this as an open question rather than locking.

2. **Does the D-11 migration's `previous_key_present` audit field carry any user-identifying info?**
   - **What we know:** The field is `bool` (true/false). No key prefix, no last4, no system identifier. Safe by construction.
   - **What's unclear:** Nothing — included for completeness; the planner can adopt verbatim.

3. **Should the Save path also write `settings.ApiUrl` for the OpenAI branch?**
   - **What we know:** D-06 specifies vault write + sentinel. Doesn't mention `ApiUrl`. The existing `EnsureClient` at `OpenAIService.cs:704-720` uses `settings.ApiUrl` if non-empty AND only falls back to the Ollama URL pattern when `AiProvider == "Ollama"`.
   - **What's unclear:** For an OpenAI-branch user, what should `ApiUrl` be? Empty (let the OpenAI SDK use its default `https://api.openai.com/v1`)? Or explicitly `"https://api.openai.com/v1"`?
   - **Recommendation:** Set `settings.ApiUrl = ""` (empty) on OpenAI-branch save. The `EnsureClient` block at `:719` already does `if (!string.IsNullOrEmpty(apiUrl)) options.Endpoint = new Uri(apiUrl);` — so empty means the SDK uses its default endpoint. This is the path of least surprise.

4. **Where does the `evidence/` subdirectory get created — by the planner, by the user, or automatically?**
   - **What we know:** D-12 says rotation evidence lives at `.planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-2026-MM-DD.png`. The directory does NOT exist today.
   - **Recommendation:** Add a plan task: "Create `evidence/` subdir; commit a `.gitkeep` so the directory tracks even before D-12 screenshot exists. VERIFICATION.md references the eventual file path." This is a 1-minute task; gives the user a clear receptacle.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | All AIBWindows build/test | ✓ | 8.0.403 (verified `dotnet --version`) | — |
| `dotnet build AIBWindows/AIB.csproj` | Build verification | ✓ | (passes — 0 errors, 24 pre-existing warnings) | — |
| Windows DPAPI (`ProtectedData`) | Vault, settings | ✓ | Built into Windows 10/11 (CurrentUser scope) | — — must be Windows |
| Ollama running on `localhost:11434` | D-09 `/api/tags` model picker (FirstRunWindow Ollama branch) | unknown at research time | — | D-09 fallback already designed: error message + `Tentar novamente` + `Usar qwen2.5:7b mesmo assim` link |
| OpenAI account access (browser) | D-12 manual key rotation | unknown at research time | — | None — D-12 is a mandatory manual step. If owner cannot rotate, phase blocks. |
| `rm` (cmd `del`, PowerShell `Remove-Item`) | D-10 `.env` file deletion | ✓ | OS-native | — |
| `git` | Tracking `.env.example` add + commits | ✓ (repo is a git repo per init context) | — | — |
| `pip`, `npm` | Not needed (no Python/Node tooling this phase) | N/A | — | — |
| Test framework | N/A — per NFR-01, no automated tests required | N/A | — | Manual UAT via VERIFICATION.md |

**Missing dependencies with no fallback:** None this phase.

**Missing dependencies with fallback:** Ollama running locally — D-09 designs a graceful "Ollama não detectado" branch; the phase ships even if Ollama is offline at FirstRunWindow open.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | **None.** `[VERIFIED: .planning/codebase/TESTING.md]` — no xUnit, NUnit, MSTest, FluentAssertions in `AIB.csproj`. No `*.Tests.csproj` anywhere. Phase 2 follows Phase 1 precedent and per NFR-01 ships with manual UAT only. |
| Config file | none |
| Quick run command | `dotnet build AIBWindows/AIB.csproj` (compile-as-test — catches signature mistakes, missing usings, regex syntax errors) |
| Full suite command | Manual UAT via 02-VERIFICATION.md (scenarios listed below); `dotnet build AIBWindows/AIB.csproj` clean |
| Phase gate | All manual UAT scenarios green + build clean + D-12 evidence file committed before `/gsd-verify-work` |

### Phase Requirements → Test Map

Per Nyquist methodology, each phase requirement is sampled at the smallest validation increment that proves the requirement closes the CONCERNS.md finding. Phase 2 has six functional concerns to validate (SEC-03 acceptance + the D-04/D-08/D-09/D-11/D-12 explicit decisions). Six is the practical minimum sampling rate for this phase (greater than the 4 listed in REQUIREMENTS.md acceptance — adds three for the D-decisions that introduce new code paths).

| Req ID | Behavior | Test Type | Validation Command / Action | File Exists? |
|--------|----------|-----------|------------------------------|--------------|
| SEC-03.A1 | Live `sk-svcacct-…` key removed from disk | smoke (filesystem) | `Test-Path .env, AIBWindows/.env, AIBLinux/.env` returns `False, False, False` after Phase 2 ships | ❌ Wave 0 (add to 02-VERIFICATION.md) |
| SEC-03.A2 | Rotation evidence committed | manual + filesystem | `Test-Path .planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-*.png` returns `True`; screenshot shows OLD revoked + NEW last4 + date | ❌ Wave 0 |
| SEC-03.A3 | Fresh clone (or vault deleted) → FirstRunWindow on first hotkey | manual smoke (UI) | Delete `~/.AIB/credentials/openai.bin`. Set `settings.ApiKey = "use-vault"`, `settings.AiProvider = "OpenAI"`. Launch app. Press `Ctrl+Shift+Space`. Expected: FirstRunWindow opens, NOT ChatWindow. | ❌ Wave 0 |
| SEC-03.A4 | App refuses to function with placeholder key | manual smoke (chat) | After fresh setup, paste `sk-PLACEHOLDER` in FirstRunWindow. Expected: regex rejects (D-05); inline error appears; audit emits `firstrun_invalid_key`. | ❌ Wave 0 |
| D-01 + D-08 | Ollama-branch user never sees FirstRunWindow | manual smoke (UI) | Set `settings.AiProvider = "Ollama"`. Press hotkey. Expected: ChatWindow opens directly. No FirstRunWindow. | ❌ Wave 0 |
| D-04 | Cancel/X/Esc/Alt+F4 → audit + clean shutdown | manual smoke (UI) + audit-log inspect | Open FirstRunWindow. Press Esc. Expected: app shuts down (no tray icon ghost after 30s hover-refresh); `~/.AIB/logs/audit.log` last line has `outcome:firstrun_cancelled`. Repeat with X. Repeat with Cancel button. Repeat with Alt+F4. | ❌ Wave 0 |
| D-05 | Regex validation accepts real OpenAI keys, rejects placeholders | manual smoke (UI) | Try `sk-` (too short — reject). Try `sk-PLACEHOLDER` (reject). Try `sk-XXXXXXXXXXXXXXXXXXXX` (20-char tail — accept, write to vault). | ❌ Wave 0 |
| D-06 | Save writes vault, sentinel, audits | manual smoke (UI) + filesystem inspect | After valid Save: `Test-Path ~/.AIB/credentials/openai.bin` → True; `LoadSettings().ApiKey == "use-vault"`; audit-log last line `outcome:firstrun_saved`. | ❌ Wave 0 |
| D-06 (read) | `OpenAIService` consumes vault on chat | manual smoke (chat) | After Save with real key, send a chat message. Expected: OpenAI request succeeds (200 OK at network layer; AIB returns a normal LLM reply). Audit log NOT triggered (this path is silent). | ❌ Wave 0 |
| D-09 | Ollama branch lists installed models | manual smoke (UI) | With Ollama running locally: open FirstRunWindow → pick Ollama → dropdown populates with installed models. Pick one → Save. Verify `settings.ModelName == settings.ShadowModelName == <picked>`. | ❌ Wave 0 |
| D-09 | Ollama unavailable → fallback link | manual smoke (UI) | Stop Ollama. Open FirstRunWindow → pick Ollama → expected: "Ollama não detectado…" message + `Tentar novamente` button + `Usar qwen2.5:7b mesmo assim` link. | ❌ Wave 0 |
| D-10 | `DotNetEnv` package removed; build clean | automated (Bash) | `Select-String "DotNetEnv" AIBWindows/AIB.csproj` returns nothing. `dotnet build AIBWindows/AIB.csproj` → 0 errors. | ❌ Wave 0 (`dotnet build` already passes today — re-verify post-edit) |
| D-11 | Existing-user migration runs once and is idempotent | manual smoke (boot) + audit-log | Set `settings.ApiKey = "sk-svcacct-test"` (DPAPI-encrypt manually). Launch app. Expected: `settings.ApiKey` is now `"use-vault"`; audit-log has `outcome:migration_clear_apikey, previous_key_present:true`. Re-launch. Expected: NO new migration line. | ❌ Wave 0 |
| D-12 | Rotation evidence captured | manual + filesystem | Owner rotates key. Saves PNG to `evidence/openai-console-rotation-2026-MM-DD.png`. VERIFICATION.md references the file path. | ❌ Wave 0 |
| NFR-04 | `profile.dat` schema unchanged | smoke (deserialize) | Post-phase-2 build can read a Phase-1-era `profile.dat`. (`UserAppSettings` POCO field set unchanged — Phase 2 adds NO new fields, only mutates an existing one.) Verified by code reading: `SettingsService.cs:12-54` POCO untouched in this phase. | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet build AIBWindows/AIB.csproj` (compile-as-smoke-test; catches 80% of typos)
- **Per wave merge:** Full UAT scenario set (above table) re-run; audit-log inspection
- **Phase gate:** All scenarios green + screenshot evidence committed + SEGURANCA.MD updated + CONCERNS.md "Resolved" section updated with this phase's last commit SHA

### Wave 0 Gaps
- [ ] `.planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md` — translates the table above into Phase 1-style numbered scenarios (S1, S2, …) with pre/step/expected/observed fields
- [ ] `.planning/phases/02-key-rotation-env-hardening/evidence/.gitkeep` — directory marker so D-12 screenshot has a tracked home
- [ ] No automated test infrastructure to add — NFR-01 explicit waiver applies

*(If no test framework gap: the project ships without xUnit; that posture is locked by NFR-01. No bootstrapping in this phase.)*

## Security Domain

`security_enforcement` is not explicitly disabled in `.planning/config.json` — the project IS the security remediation milestone, so this domain is required.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| **V1 Architecture, Design** | yes | Separation of secret storage from configuration storage (vault vs `profile.dat`). Sentinel pattern (D-06) is a deliberate architecture choice documented in CONTEXT.md and reinforced by RESEARCH.md Pattern 1. |
| **V2 Authentication** | no | App does not authenticate users; DPAPI binds to Windows user implicitly. |
| **V3 Session Management** | no | App is single-user, single-session. |
| **V4 Access Control** | partial | Level-based tool gating (Phase 1 D5) covers tool access. This phase: only the OS-level Windows user can decrypt DPAPI-protected secrets — that IS the access control. |
| **V5 Input Validation** | yes | D-05 regex validation `^sk-[a-zA-Z0-9_-]{20,}$` for the OpenAI key. Inline error UI per FirstRunWindow XAML. **Critical:** no string interpolation of the user-entered key into shell or SQL — `CredentialService.StoreCredentialAsync` uses parameterized JSON serialization. Verified by Read. |
| **V6 Cryptography** | yes | DPAPI `ProtectedData.Protect` with `DataProtectionScope.CurrentUser`. No hand-rolled crypto. No keys in source. `[VERIFIED: CredentialService.cs:42; SettingsService.cs:73,98]` |
| **V7 Error Handling, Logging** | yes | Audit-log emits structured JSONL via `AuditLogService` (no plaintext key in logs — D-12 + Pitfall 2 + key_last4 convention). Errors in vault read return `ERRO`-prefixed strings, NOT exceptions; consumers branch on the prefix. |
| **V8 Data Protection** | yes | Live key removed from disk (D-10). Vault binds to user (DPAPI CurrentUser). `.env.example` carries only placeholders. `SEGURANCA.MD` updated per NFR-03 to describe the shipped reality. |
| **V9 Communication** | no | Not in scope this phase (HTTPS for OpenAI is OpenAI SDK's concern). |
| **V10 Malicious Code** | no | Not in scope. |
| **V11 Business Logic** | no | Not in scope. |
| **V12 Files & Resources** | yes | `.env` files deleted from filesystem; `.env.example` checked in with placeholders; `.gitignore` already prevents future `.env` from being tracked (verified `.gitignore:11`). |
| **V13 API & Web Service** | no | Not in scope. |
| **V14 Configuration** | yes | `DotNetEnv` package removal eliminates a "configuration via dotfile" path that conflicts with the DPAPI vault stance. Single source of truth (vault) for the secret. |

### Known Threat Patterns for WPF + DPAPI + tray-app stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Live secret on disk (the original sin) | Information Disclosure | Delete the file; replace with placeholder template; rotate the leaked secret in the upstream issuer |
| Silent migration of a leaked secret into a "more secure" store | Information Disclosure | D-11: force re-entry, do NOT auto-copy. Decision is the entire reason the migration exists. |
| Vault lookup confused across systems (CredentialService global fallback) | Information Disclosure | Out of scope here, but flagged in Pitfall 1; tracked as MEDIUM finding for Phase 4 |
| Console log leaking key names + system names (`[DEBUG-COFRE]`) | Information Disclosure | Out of scope here (MEDIUM, Phase 4); flagged in Pitfall 2 |
| Tray-app boot fires UI dialog → user clicks through without reading | Spoofing / UX | D-01 lazy-trigger pattern: UI fires on user-initiated hotkey, not on boot. User's first interaction with FirstRunWindow is intentional. |
| Audit log itself becomes a leak vector (if it logs the key) | Information Disclosure | Schema design (Code Examples → audit-log) only logs `key_last4`. NEVER log full key. **MUST be enforced in code review.** |
| Vault read on every chat turn becomes a performance regression | Denial-of-Service (self-inflicted) | DPAPI decrypt of a 16-byte payload is sub-millisecond; acceptable. If problem ever surfaces, cache the decrypted key in `OpenAIService` after first read (out of scope this phase). |
| Race between dev's running AIB and dev's newly-launched post-phase build | Tampering (settings file) | Documented in Pitfall 3. Mitigation: VERIFICATION.md instructs full close before re-launch. |
| FirstRunWindow visible to over-the-shoulder observer | Information Disclosure | Standard `PasswordBox` would mask input but breaks paste UX (WPF `PasswordBox` is paste-hostile). Recommendation: use `TextBox` (matching Settings UI today) + warning copy ("Será armazenada com criptografia DPAPI"). Decision is implicit in D-07. |

## Sources

### Primary (HIGH confidence — all verified by direct file Read)
- `[VERIFIED]` `AIBWindows/Services/CredentialService.cs` (full file, 122 lines) — vault contract
- `[VERIFIED]` `AIBWindows/Services/SettingsService.cs` (full file, 133 lines) — POCO + Load/Save + Ollama tags
- `[VERIFIED]` `AIBWindows/Services/OpenAIService.cs` (lines 1-100, 640-725) — call sites + warmup confirms no extra ApiKey reads
- `[VERIFIED]` `AIBWindows/Services/AuditLogService.cs` (full file, 62 lines) — JSONL append pattern
- `[VERIFIED]` `AIBWindows/Services/DirectoryService.cs` (full file, 78 lines) — DataDir derivation
- `[VERIFIED]` `AIBWindows/Services/CommandConfirmationContext.cs` (full file) — Phase 1 record-type pattern
- `[VERIFIED]` `AIBWindows/Services/NativeTools.cs` (lines 280-460) — Phase 1 audit + BuildEntry schema reference
- `[VERIFIED]` `AIBWindows/App.xaml.cs` (full file, 94 lines) — OnStartup + OnHotkeyDetected insertion points
- `[VERIFIED]` `AIBWindows/App.xaml` — bare resources block; no dictionary imports to disrupt
- `[VERIFIED]` `AIBWindows/Views/ChatWindow.xaml` (lines 1-80) — theme reference for D-07
- `[VERIFIED]` `AIBWindows/Views/ChatWindow.xaml.cs` (lines 1-210) — ToggleWindow lifecycle + welcome-bubble pattern
- `[VERIFIED]` `AIBWindows/Views/SettingsWindow.xaml` (lines 1-200) — theme + provider combo confirms D-07 mismatch flagged in Open Question Q1
- `[VERIFIED]` `AIBWindows/Views/SettingsWindow.xaml.cs` (full file) — the 2 ApiKey UI bindings
- `[VERIFIED]` `AIBWindows/AIB.csproj` (full file, 35 lines) — DotNetEnv reference at line 17 confirmed
- `[VERIFIED]` `.env`, `AIBWindows/.env`, `AIBLinux/.env` (all 3 files, all 3 contain same `sk-svcacct-…` key) — CONCERNS.md miscount surfaced
- `[VERIFIED]` `.gitignore` (51 lines) — confirms `.env` ignored, `.env.example` would NOT be ignored
- `[VERIFIED]` `.planning/phases/01_modal-and-level9/CONTEXT.md` — D6, D8 patterns reused this phase
- `[VERIFIED]` `.planning/phases/01_modal-and-level9/VERIFICATION.md` — UAT format template for this phase
- `[VERIFIED]` `.planning/codebase/TESTING.md` (lines 1-100) — confirms no automated test suite

### Secondary (verified via tool execution)
- `[VERIFIED]` `dotnet build AIBWindows/AIB.csproj` — 0 errors, 24 pre-existing warnings, ~19s. Confirms baseline build is green before Phase 2 edits.
- `[VERIFIED]` `dotnet --version` — 8.0.403 (matches `TargetFramework=net8.0-windows10.0.19041.0`)
- `[VERIFIED]` `Grep("DotNetEnv|Env\\.Load|using DotNetEnv", AIBWindows)` — 1 hit total, only the csproj line. Zero code call sites.
- `[VERIFIED]` `Grep("ApiKey", AIBWindows)` — exactly 4 hits in C# files: OpenAIService:657, OpenAIService:705, SettingsWindow:40, SettingsWindow:124 + 1 POCO declaration at SettingsService:15. CONTEXT.md "TWO call sites" verified for OpenAIService.
- `[VERIFIED]` `Grep("FirstRun|FirstLaunch|WelcomeWindow|first_run|firstrun", AIBWindows)` — zero hits. No existing first-run pattern; greenfield surface.
- `[VERIFIED]` `Grep("AuditLogService.AppendAsync", AIBWindows)` — 5 hits, all in NativeTools.cs (Phase 1 modal outcomes). Pattern locked.

### Tertiary (LOW confidence — N/A this phase)
None. All claims trace to a verified file read or tool execution against the working tree.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — entire stack already in codebase; verified by Read
- Architecture: HIGH — every component reference verified by file read; insertion points exact line numbers
- Pitfalls: HIGH — six pitfalls, five of them grounded in source code reads (vault global fallback at CredentialService:65-77; console noise at CredentialService:59-91; settings race at SettingsService:94-99; csproj-cleanup at AIB.csproj:17; gitignore at .gitignore:11). Pitfall 6 (tray ghost) is general WPF/NotifyIcon convention, MEDIUM strict but mitigated by Phase 1 precedent.
- Open questions: MEDIUM — Q1 (AiProvider="OpenAI") flags a real internal-consistency gap that planner must decide; Q3 (ApiUrl on save) has a clear recommended default

**Research date:** 2026-05-30
**Valid until:** 2026-06-29 (30 days — stable; only the OpenAI SDK changelog could move; D-12 manual rotation only sensitive to OpenAI console UI changes which don't affect code)
