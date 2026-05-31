---
phase: 02-key-rotation-env-hardening
plan: 04b
type: execute
wave: 4
depends_on:
  - 02-04a
files_modified:
  - Regras de Identidade/SEGURANCA.MD
  - README.md
  - .planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md
  - .planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-2026-MM-DD.png
autonomous: false
requirements:
  - SEC-03
must_haves:
  truths:
    - "Regras de Identidade/SEGURANCA.MD section 2 names the DPAPI vault as the canonical OpenAI key store; describes FirstRunWindow as the entry path; uses present-tense shipped behavior (NFR-03)"
    - "README.md key-setup bullet replaced with FirstRunWindow walkthrough; references the DPAPI vault path"
    - ".planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md exists with Phase-1-style numbered scenarios (S1..Sn) translating the VALIDATION.md per-task table into pre/step/expected/observed fields; all four audit outcome literals (firstrun_saved, firstrun_invalid_key, firstrun_cancelled, migration_clear_apikey) appear in the file"
    - "D-12 manual rotation completed: owner rotated the live OpenAI service-account key in OpenAI console; screenshot saved at .planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-2026-MM-DD.png showing OLD revoked + NEW last4 + date (key value redacted)"
  artifacts:
    - path: "Regras de Identidade/SEGURANCA.MD"
      provides: "Section 2 updated to describe DPAPI vault as canonical OpenAI key store + FirstRunWindow as entry path (NFR-03 present-tense shipped behavior)"
      contains: "DPAPI"
    - path: "README.md"
      provides: "Key-setup bullet replaced with FirstRunWindow + DPAPI vault walkthrough"
      contains: "FirstRunWindow"
    - path: ".planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md"
      provides: "Numbered UAT scenarios (S1..Sn) mirroring Phase 1 VERIFICATION.md format with pre/step/expected/observed fields"
      contains: "S1"
    - path: ".planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-2026-MM-DD.png"
      provides: "D-12 screenshot evidence — OLD service-account key revoked + NEW key last4 visible + date timestamp (full key value redacted)"
      contains: "(binary PNG)"
  key_links:
    - from: "Regras de Identidade/SEGURANCA.MD section 2"
      to: "AIBWindows/Services/CredentialService.cs + FirstRunWindow"
      via: "doc prose names the vault path and the FirstRunWindow entry"
      pattern: "DPAPI"
    - from: ".planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md"
      to: ".planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-2026-MM-DD.png"
      via: "VERIFICATION scenario references the screenshot file path as the proof artifact for D-12"
      pattern: "evidence/openai-console-rotation"
---

<objective>
Docs + UAT scenarios + D-12 rotation evidence slice of the original plan 04 (split per checker Issue 4 — original plan was over budget at 4 tasks + 6 files + 1 binary). This plan ships the wrapping that closes the phase: SEGURANCA.MD is rewritten to describe shipped behavior (NFR-03), README.md directs new contributors to FirstRunWindow, 02-VERIFICATION.md translates VALIDATION.md per-task rows into Phase-1-style numbered scenarios, and the owner performs the D-12 manual rotation in the OpenAI console with screenshot evidence committed.

Purpose: The code wires are inert without the rotation (the leaked key remains valid upstream) and without the doc updates (users do not discover the new flow). This plan is the gate between code-is-correct and phase-ships.

Output: 2 doc edits + 1 new doc + 1 new binary file (D-12 screenshot). Wave 4 — depends on 02-04a (SettingsWindow polish must ship before doc claims about "Configurada (cofre DPAPI)" friendly label are true; D-12 rotation requires end-to-end FirstRunWindow + Settings flow to work).

This plan is autonomous=false because Task 3 (D-12 rotation) is a checkpoint:human-action — owner must log into the OpenAI console, rotate the key, redact the screenshot, and save it. There is no CLI/API for "revoke service-account key + capture screenshot + save to evidence/" that Claude can autonomously drive.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/phases/02-key-rotation-env-hardening/02-CONTEXT.md
@.planning/phases/02-key-rotation-env-hardening/02-RESEARCH.md
@.planning/phases/02-key-rotation-env-hardening/02-PATTERNS.md
@.planning/phases/02-key-rotation-env-hardening/02-UI-SPEC.md
@.planning/phases/02-key-rotation-env-hardening/02-VALIDATION.md
@.planning/phases/02-key-rotation-env-hardening/02-01-PLAN.md
@.planning/phases/02-key-rotation-env-hardening/02-02-PLAN.md
@.planning/phases/02-key-rotation-env-hardening/02-03-PLAN.md
@.planning/phases/02-key-rotation-env-hardening/02-04a-PLAN.md
@.planning/phases/01_modal-and-level9/VERIFICATION.md
@Regras de Identidade/SEGURANCA.MD
@README.md
</context>

<tasks>

<task type="auto" tdd="false">
  <name>Task 1: Update SEGURANCA.MD section 2 + README.md key-setup section to describe shipped DPAPI vault + FirstRunWindow flow (NFR-03)</name>
  <files>Regras de Identidade/SEGURANCA.MD, README.md</files>
  <read_first>
    - Regras de Identidade/SEGURANCA.MD (full file, current state — locate section 2 around line 11-14 with the DPAPI prose)
    - README.md (full file, current state — locate the Requisitos Globais bullet around line 30 mentioning .env)
    - .planning/phases/02-key-rotation-env-hardening/02-PATTERNS.md SEGURANCA.MD section (lines 504-516 — concrete addition template) + README.md section (lines 520-528 — replacement pattern)
    - .planning/phases/02-key-rotation-env-hardening/02-RESEARCH.md Architectural Responsibility Map (DPAPI vault canonical) + Standard Stack (final state)
  </read_first>
  <action>
    Two doc edits, both in pt-BR (matching the existing doc language).

    Edit A — Regras de Identidade/SEGURANCA.MD: Locate section 2 (the section about CredentialService / DPAPI). The existing prose at line 14 generically mentions DPAPI vault. Extend it with an EXPLICIT statement that the OpenAI API key lives in the DPAPI vault and nowhere else, and that the entry path is FirstRunWindow on first hotkey after install. Suggested addition (planner discretion on exact prose, subject to NFR-03 present-tense shipped behavior — no aspirational "deve"/"deverá"/"será"/"must"/"will"/"should" for the OpenAI key path):

    - A new bullet or sentence under section 2 stating in present tense: "A chave da OpenAI é armazenada exclusivamente no cofre DPAPI (~/.AIB/credentials/openai.bin), nunca em .env ou em texto plano no profile.dat. A entrada inicial acontece via FirstRunWindow, aberta automaticamente no primeiro atalho global (Ctrl+Shift+Space) após a instalação ou após uma rotação de chave."

    - Also remove or update any aspirational language elsewhere in section 2 that uses "deve"/"deverá" (future/imperative) if it describes the OpenAI key path — rewrite as present-tense statement of fact.

    DO NOT modify any other section of SEGURANCA.MD (section 1, section 3+ are out of scope for Phase 2).

    Edit B — README.md: Locate the existing Requisitos Globais bullet that says "Arquivo .env configurado na raiz com as chaves de API necessárias (caso não use Ollama)." (currently around line 30). REPLACE that bullet with a new one describing the FirstRunWindow walkthrough. Suggested replacement (planner discretion on exact prose):

    - "Chave da OpenAI (caso use OpenAI): será solicitada via FirstRunWindow no primeiro atalho global (Ctrl+Shift+Space) após a instalação. A chave é armazenada com criptografia DPAPI no cofre local (~/.AIB/credentials/openai.bin); não existe arquivo .env no fluxo Windows."

    Keep the surrounding bullet list intact. Do NOT modify other README sections.
  </action>
  <verify>
    <automated>powershell -Command "$ok = $true; $seg = 'C:\Users\Carlo\CPAPS\AIB\Regras de Identidade\SEGURANCA.MD'; $rd = 'C:\Users\Carlo\CPAPS\AIB\README.md'; if (!(Select-String -Path $seg -Pattern 'FirstRunWindow' -Quiet)) { Write-Error 'SEGURANCA missing FirstRunWindow ref'; $ok = $false }; if (!(Select-String -Path $seg -Pattern 'DPAPI' -Quiet)) { Write-Error 'SEGURANCA missing DPAPI ref'; $ok = $false }; if (!(Select-String -Path $rd -Pattern 'FirstRunWindow' -Quiet)) { Write-Error 'README missing FirstRunWindow ref'; $ok = $false }; if (Select-String -Path $rd -Pattern 'Arquivo .env configurado na raiz' -Quiet) { Write-Error 'README still has old .env bullet'; $ok = $false }; if ($ok) { Write-Output 'OK' } else { exit 1 }"</automated>
  </verify>
  <acceptance_criteria>
    - Select-String -Path "Regras de Identidade/SEGURANCA.MD" -Pattern "FirstRunWindow" returns at least 1 match
    - Select-String -Path "Regras de Identidade/SEGURANCA.MD" -Pattern "DPAPI" returns at least 1 match in or near section 2
    - Select-String -Path README.md -Pattern "FirstRunWindow" returns at least 1 match
    - Select-String -Path README.md -Pattern "Arquivo .env configurado na raiz" returns 0 matches (old bullet removed)
    - Select-String -Path README.md -Pattern "DPAPI" returns at least 1 match
    - The new prose in the edited section of SEGURANCA.MD does not use aspirational language (no "deverá"/"deve"/"será" describing the OpenAI key path — verify manually during writing; pre-existing matches in OTHER sections of SEGURANCA.MD are out of scope for this phase)
  </acceptance_criteria>
  <done>SEGURANCA.MD section 2 names the DPAPI vault as the canonical OpenAI key store and FirstRunWindow as the entry path (NFR-03 present-tense shipped behavior); README.md key-setup bullet is replaced with the FirstRunWindow + DPAPI walkthrough.</done>
</task>

<task type="auto" tdd="false">
  <name>Task 2: Write 02-VERIFICATION.md — Phase-1-style numbered UAT scenarios translated from VALIDATION.md per-task table</name>
  <files>.planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md</files>
  <read_first>
    - .planning/phases/01_modal-and-level9/VERIFICATION.md (template — Phase 1 UAT format with numbered S1..S8 scenarios, each with pre/step/expected/observed fields)
    - .planning/phases/02-key-rotation-env-hardening/02-VALIDATION.md (the 15-row per-task table; each row becomes an S-scenario)
    - .planning/phases/02-key-rotation-env-hardening/02-RESEARCH.md Phase Requirements to Test Map (lines 644-660 — the same scenarios in narrative form)
    - .planning/phases/02-key-rotation-env-hardening/02-CONTEXT.md (D-01..D-12 decisions — each maps to at least 1 verification scenario)
  </read_first>
  <action>
    Create C:\Users\Carlo\CPAPS\AIB\.planning\phases\02-key-rotation-env-hardening\02-VERIFICATION.md modelled on .planning/phases/01_modal-and-level9/VERIFICATION.md.

    Frontmatter mirroring the Phase 1 file (yaml block with phase, status=draft, created date, requirements list [SEC-03], etc.). Body structure: a short header explaining what UAT covers, then numbered scenarios S1..Sn, each with these four fields (matching Phase 1 format exactly): Pre (preconditions — initial state), Step (numbered ordered list of user actions including exact PowerShell commands or UI clicks), Expected (observable outcome — UI state, file existence, audit-log line, etc. with EXACT string matches), Observed (left blank with "(pending tester)" for the tester to fill in).

    Scenarios MUST cover every row in VALIDATION.md per-task table (15 rows). Recommended scenario list (one S-scenario per VALIDATION row; planner may merge tightly related rows into single scenarios when steps overlap; final count must be at least 13):

    - S1 (SEC-03.A1): three .env files deleted from disk — verify via Test-Path on each absolute path
    - S2 (SEC-03.A2 + D-12): rotation evidence file exists at evidence/openai-console-rotation-*.png and visually shows OLD revoked + NEW last4
    - S3 (SEC-03.A3): fresh vault → hotkey opens FirstRunWindow (not ChatWindow). Step: delete ~/.AIB/credentials/openai.bin, set settings.AiProvider=OpenAI + settings.ApiKey=use-vault, launch app, press Ctrl+Shift+Space. Expected: FirstRunWindow appears.
    - S4 (SEC-03.A4 + D-05): placeholder key rejected via regex. Step: in FirstRunWindow OpenAI branch paste sk-PLACEHOLDER, click Salvar. Expected: ErrorLabel visible with copy "Chave inválida — deve começar com sk-"; audit log last line contains outcome:firstrun_invalid_key.
    - S5 (D-01 + D-08 + D-03): Ollama-branch user never sees FirstRunWindow. Step: set settings.AiProvider=Ollama, launch app, press hotkey. Expected: ChatWindow opens directly; no FirstRunWindow.
    - S6 (D-04): four cancel paths each emit audit + clean shutdown. Step: open FirstRunWindow then press Esc; relaunch + press window-X; relaunch + click Cancelar; relaunch + Alt+F4. Expected: each path writes audit line with outcome:firstrun_cancelled (emitted by App.ShowFirstRunWindow per Pitfall 6, not by the window itself); tray icon disappears within ~30s of hover refresh.
    - S7 (D-05): regex positive case — accept real-shape key. Step: paste a 20+ char key matching ^sk-[a-zA-Z0-9_-]{20,}$, click Salvar. Expected: sentinel write happens; window closes; ChatWindow opens.
    - S8 (D-06): vault write succeeds and sentinel persists. Step: after S7, inspect filesystem. Expected: Test-Path ~/.AIB/credentials/openai.bin returns True; LoadSettings().ApiKey is "use-vault"; audit log last line contains outcome:firstrun_saved with key_last4 field.
    - S9 (D-06 read): post-save chat request succeeds via vault-resolved key. Step: send a chat message after S8. Expected: OpenAI response returns; no audit entry on this read path (silent).
    - S10 (D-09): Ollama branch with Ollama running. Step: ensure Ollama serve is up, open FirstRunWindow Ollama branch. Expected: dropdown populates with installed models; Save mirrors settings.ModelName == settings.ShadowModelName == picked value.
    - S11 (D-09 fail): Ollama stopped. Step: stop Ollama, open FirstRunWindow Ollama branch. Expected: OllamaErrorBlock visible with "Ollama não detectado em localhost:11434"; Tentar novamente button visible; Usar qwen2.5:7b mesmo assim fallback link visible.
    - S12 (D-10): DotNetEnv removed and build clean. Step: run Select-String "DotNetEnv" AIBWindows/AIB.csproj then dotnet build AIBWindows/AIB.csproj. Expected: zero matches; 0 errors.
    - S13 (D-11): existing-user migration is once and idempotent. Step: pre-encrypt settings.ApiKey=sk-svcacct-test into profile.dat, launch app. Expected: settings.ApiKey is now "use-vault"; audit log has outcome:migration_clear_apikey with previous_key_present=true. Re-launch — Expected: no new migration line in audit log.
    - S14 (NFR-04): profile.dat schema unchanged. Step: load a Phase-1-era profile.dat with the Phase-2 build. Expected: no deserialization error; settings load with C# defaults for any missing field.
    - S15 (Settings polish): SettingsWindow KeyTextBox shows friendly label per sentinel; Alterar chave button reopens FirstRunWindow without shutting down on cancel. Step: open SettingsWindow, inspect KeyTextBox; click Alterar chave; press Esc in FirstRunWindow. Expected: KeyTextBox label matches one of the three friendly strings; SettingsWindow stays open after Esc (no Shutdown).

    Each scenario's Step field includes the exact PowerShell commands or UI actions a tester would perform. Audit-log inspection uses Get-Content ~/.AIB/logs/audit.log -Tail 1 or similar. Vault inspection uses Test-Path ~/.AIB/credentials/openai.bin. Expected field includes the exact string match expected.

    Status field at the top of file: status=draft (pending tester sign-off). Add a footer linking to .planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-2026-MM-DD.png as the D-12 proof artifact (referenced by S2).

    Total scenario count: 15 (matches the VALIDATION.md per-task table row count). If the planner merges adjacent rows into a single scenario, document the merge in that scenario's header; final count must be at least 13.

    **CRITICAL — Audit literal coverage (per checker Issue 7):** the file body MUST contain each of the four audit outcome literal strings at least once: `firstrun_saved`, `firstrun_invalid_key`, `firstrun_cancelled`, `migration_clear_apikey`. These map to specific scenarios above (S4/S8 → firstrun_invalid_key/firstrun_saved, S6 → firstrun_cancelled, S13 → migration_clear_apikey). The verify command asserts presence of all four.
  </action>
  <verify>
    <automated>powershell -Command "$p = 'C:\Users\Carlo\CPAPS\AIB\.planning\phases\02-key-rotation-env-hardening\02-VERIFICATION.md'; if (!(Test-Path $p)) { Write-Error 'missing'; exit 1 }; $c = Get-Content -Raw $p; $n = ([regex]::Matches($c, '(?m)^##\s+S\d+')).Count; if ($n -lt 13) { Write-Error \"only $n scenarios — need >=13\"; exit 1 }; if (!($c -match 'evidence/openai-console-rotation')) { Write-Error 'missing D-12 ref'; exit 1 }; foreach ($lit in 'firstrun_saved','firstrun_invalid_key','firstrun_cancelled','migration_clear_apikey') { if ($c -notmatch $lit) { Write-Error \"missing audit literal: $lit\"; exit 1 } }; Write-Output \"OK: $n scenarios + 4 audit literals\""</automated>
  </verify>
  <acceptance_criteria>
    - Test-Path .planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md returns True
    - File contains yaml frontmatter with phase=02-key-rotation-env-hardening, status=draft, requirements list containing SEC-03
    - File contains at least 13 numbered scenario headers matching the regex pattern ^##\s+S\d+ (target 15; allow merges down to 13)
    - File contains each of these four audit outcome literals at least once (verified by the verify command's literal-loop assertion per checker Issue 7): `firstrun_saved`, `firstrun_invalid_key`, `firstrun_cancelled`, `migration_clear_apikey`
    - File contains each of these D-XX references at least once: D-01, D-03, D-04, D-05, D-06, D-09, D-10, D-11, D-12
    - File contains the literal path string evidence/openai-console-rotation (D-12 evidence reference in S2)
    - Each scenario has four labelled sections (Pre, Step, Expected, Observed) — verified by Select-String counts: Pre at least 13, Step at least 13, Expected at least 13, Observed at least 13
  </acceptance_criteria>
  <done>02-VERIFICATION.md exists with at least 13 numbered Phase-1-style scenarios covering every VALIDATION.md row; each scenario references the relevant D-XX decision; D-12 evidence path is named in S2; all four audit outcome literals appear in the body (verified by the loop in the verify command per checker Issue 7).</done>
</task>

<task type="checkpoint:human-action" gate="blocking">
  <name>Task 3: D-12 manual rotation — owner rotates the live OpenAI service-account key in console and commits redacted screenshot</name>
  <what-built>
    Plans 01-03 deleted the live key from all three on-disk .env files, wired the vault sentinel into OpenAIService, scaffolded FirstRunWindow as the new entry path, and the evidence/.gitkeep directory marker is in place (plan 01 Task 2). Plan 04a polished SettingsWindow. The CODE side of "no live key on disk" is complete.

    However, the leaked sk-svcacct-... key is still VALID upstream in the OpenAI console until the owner explicitly rotates it. Until rotation, anyone who had filesystem access between the original commit and the plan 01 deletion (including any backup, any cloud-sync, any USB stick, any git-stash that included the .env files) can still use the key. The leak window closes only when the upstream key is revoked.

    D-11 explicitly forbids auto-migrating the leaked key into the vault — the user must rotate FIRST and paste the NEW key into FirstRunWindow. This checkpoint is the trigger to perform that rotation.
  </what-built>
  <how-to-verify>
    Owner performs these steps manually (no Claude automation possible — the OpenAI dashboard has no public CLI/API for service-account-key revoke + screenshot):

    1. Log into the OpenAI dashboard at platform.openai.com using the owner's credentials.
    2. Navigate to: API keys → Service accounts → locate the sk-svcacct-... key currently in use by AIB.
    3. Click "Revoke" on the OLD key. Confirm revocation. The key transitions to "Revoked" status with a timestamp.
    4. Click "Create new service account key" (or equivalent UI affordance). Generate the new sk-svcacct-... key. Copy the NEW key value to clipboard (do NOT save it to any file other than the DPAPI vault via FirstRunWindow).
    5. Take a screenshot of the OpenAI console that shows BOTH:
       - The OLD key visibly marked as "Revoked" with its revocation timestamp
       - The NEW key with only the last 4 characters visible (the OpenAI console naturally truncates display of newly-created keys to last4 in the list view — this is expected; do NOT screenshot the one-time-reveal modal showing the full new key)
       - The current date is visible in the screenshot (browser address bar timestamp, system clock, or page header — any source)
    6. Crop or redact the screenshot to remove any other sensitive data (other key fingerprints, billing info, account email if owner prefers) using your image editor of choice. The screenshot must NOT contain the full new key value at any pixel.
    7. Save the cropped/redacted screenshot to the absolute path:
       C:\Users\Carlo\CPAPS\AIB\.planning\phases\02-key-rotation-env-hardening\evidence\openai-console-rotation-YYYY-MM-DD.png
       where YYYY-MM-DD is the actual rotation date (e.g. openai-console-rotation-2026-05-31.png).
    8. Open AIB (or relaunch if running) → press Ctrl+Shift+Space → FirstRunWindow opens (because the vault is empty / sentinel triggers detector per D-03) → pick the OpenAI radio → paste the NEW key into KeyTextBox → click Salvar. ChatWindow opens. Send a test chat message to confirm the new key is live and the OpenAIService vault read path works end-to-end.
    9. Type "rotated" in this prompt to confirm completion, OR describe any issues encountered.

    Acceptance: after this checkpoint, the OLD leaked key is dead (any attempt to use it returns 401 from OpenAI), the NEW key lives ONLY in the DPAPI vault (~/.AIB/credentials/openai.bin), the screenshot evidence is committed to the phase evidence/ folder, and an end-to-end chat round-trip has confirmed the wiring.
  </how-to-verify>
  <resume-signal>Type "rotated" once the screenshot is saved at evidence/openai-console-rotation-YYYY-MM-DD.png AND a test chat message succeeded after FirstRunWindow Save with the new key. Or describe issues (e.g. "OpenAI console UI changed — revoke moved to settings sub-page") and request guidance.</resume-signal>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| OpenAI console (browser) ↔ owner | Manual rotation crosses the human into the upstream issuer's web UI; no Claude automation. |
| New key ↔ clipboard ↔ FirstRunWindow KeyTextBox | The freshly-issued key transits clipboard briefly during paste into FirstRunWindow. No persistence outside the vault. |
| evidence/*.png ↔ git history | The screenshot becomes part of the repo's git history forever; redaction MUST be irreversible (no metadata, no full-key pixels). |
| docs ↔ shipped behavior | NFR-03 forbids drift between code and prose; doc edits in Task 1 are the audit trail that makes the new flow discoverable. |

## STRIDE Threat Register

| Threat ID | Category | Component | Severity | Disposition | Mitigation Plan |
|-----------|----------|-----------|----------|-------------|-----------------|
| T-02-13 | Spoofing / Authentication (ASVS V2) | OLD leaked sk-svcacct-... key continuing to work upstream | high | mitigate | Task 3 owner-driven rotation in OpenAI console revokes the OLD key. After Task 3, any holder of the leaked key receives 401. This is the actual SEC-03 close — without it, the code changes only relocate the trust path, not the leaked secret. |
| T-02-14 | Information Disclosure (ASVS V8) | D-12 screenshot containing the full NEW key | high | mitigate | Task 3 how-to-verify step 5+6+7: screenshot must show last4 only of NEW key (OpenAI console naturally truncates in list view); owner crops/redacts before save; do NOT screenshot the one-time-reveal modal. Acceptance: file size <500KB (sanity), filename matches pattern, no pixel review by Claude (cannot inspect binary). |
| T-02-17 | Misconfiguration (NFR-03) | Doc drift between shipped behavior and SEGURANCA.MD/README.md prose | medium | mitigate | Task 1 rewrites the OpenAI-key prose in both docs as present-tense statements of fact (no "deve"/"deverá"/"será" for the OpenAI key path). Acceptance check: README old bullet absent; new prose includes FirstRunWindow + DPAPI mentions. |
| T-02-18 | UAT gap | No reproducible test scenarios for the new flow → manual UAT becomes ad-hoc | medium | mitigate | Task 2 creates 02-VERIFICATION.md with at least 13 numbered scenarios mirroring Phase 1 format; each scenario maps to a D-XX decision; verify command enforces scenario count + D-12 ref + all four audit outcome literals (firstrun_saved/firstrun_invalid_key/firstrun_cancelled/migration_clear_apikey) per checker Issue 7. |
| T-02-SC | Tampering / Supply Chain | npm/pip/cargo installs in this plan | low | accept | No package installs — only doc edits + 1 markdown file + 1 binary file (owner-supplied screenshot). RESEARCH Package Legitimacy Audit confirms N/A for the entire phase. |
</threat_model>

<verification>
- SEGURANCA.MD section 2 contains FirstRunWindow + DPAPI mentions.
- README.md old .env-bullet absent; new FirstRunWindow + DPAPI bullet present.
- 02-VERIFICATION.md exists with at least 13 numbered scenarios; D-12 evidence path referenced; all four audit outcome literals present (firstrun_saved, firstrun_invalid_key, firstrun_cancelled, migration_clear_apikey).
- evidence/openai-console-rotation-YYYY-MM-DD.png exists after Task 3 owner sign-off (file size 0 < x < 5MB; PNG header magic bytes).
- Test chat message after FirstRunWindow Save with NEW key returns a normal LLM response (manual verification step 8 of Task 3).
</verification>

<success_criteria>
- SEGURANCA.MD section 2 + README.md key-setup describe the shipped DPAPI vault + FirstRunWindow flow in present tense (NFR-03).
- 02-VERIFICATION.md provides at least 13 Phase-1-style numbered scenarios covering every VALIDATION.md per-task row, and contains all four audit outcome literal strings (per checker Issue 7).
- The OLD leaked OpenAI service-account key is REVOKED in the upstream console (Task 3 owner action).
- The NEW key lives ONLY in the DPAPI vault; FirstRunWindow + OpenAIService end-to-end smoke test (chat message) passes.
- Redacted screenshot evidence committed to evidence/openai-console-rotation-YYYY-MM-DD.png.
</success_criteria>

<output>
Create .planning/phases/02-key-rotation-env-hardening/02-04b-SUMMARY.md when done, listing: SEGURANCA.MD + README.md doc-edit deltas, 02-VERIFICATION.md scenario count + audit-literal coverage report, evidence/*.png filename + size + date, end-to-end chat test result from Task 3 step 8, any deviations from the plan that the owner observed during rotation.
</output>
