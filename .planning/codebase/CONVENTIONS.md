# Coding Conventions

**Analysis Date:** 2026-05-28

This repo holds two parallel implementations of the AIB assistant:

- `AIBWindows/` — production target. .NET 8 + WPF (C#), file-scoped namespaces, MVVM-light (no real ViewModels; code-behind owns UI).
- `AIBLinux/` — secondary/legacy. Python 3.10+ with PyQt6.

The project declares its own engineering rules in `Regras de Identidade/CODIGO_LIMPO.MD`, `Regras de Identidade/SEGURANCA.MD`, `Regras de Identidade/VISUAL.MD`, and `AIBWindows/funcionalities.md`. Those rules are the authoritative spec. Sections below capture both the rule and whether the code actually follows it.

## Declared Engineering Rules (must follow)

From `Regras de Identidade/CODIGO_LIMPO.MD`:

1. **No DI container.** `ChatWindow.xaml.cs` is the Composition Root. Services are `new`'d directly in the window constructor — no `IServiceCollection`. (`Regras de Identidade/CODIGO_LIMPO.MD:5-8`)
2. **One service per system integration.** Each external concern lives in its own class under `AIBWindows/Services/` (`WebSearchService.cs`, `VoiceService.cs`, `OcrService.cs`, ...). Code-behind handles UI only. (`Regras de Identidade/CODIGO_LIMPO.MD:10-15`)
3. **Mandatory `ITool` contract.** Every LLM-callable action implements `AIBWindows/Services/ITool.cs`. Tools never crash the app — they `try/catch` and return a Portuguese error string for the ReAct loop to read. (`Regras de Identidade/CODIGO_LIMPO.MD:17-21`)
4. **Async + UI Dispatcher discipline.** Network/disk/heavy work must be async (`await Task.Run` or async libraries). UI mutations from background threads must go through `Dispatcher.Invoke` / `Dispatcher.BeginInvoke`. (`Regras de Identidade/CODIGO_LIMPO.MD:23-26`)
5. **Namespace aliases for ambiguity.** Use `using WColor = System.Windows.Media.Color;` style aliases when `System.Drawing` and `System.Windows.Media` collide. Use fully-qualified `System.Windows.Clipboard` to avoid WinForms clash. (`Regras de Identidade/CODIGO_LIMPO.MD:28-31`, also `GRAVITY.MD:55-58`)

From `Regras de Identidade/SEGURANCA.MD`:

6. **No silent shell.** Any `run_command` / `windows_console_execution` tool invocation must be routed through the modal `CommandConfirmationWindow` and require a physical human click. Bypassing this is a critical architectural violation. (`Regras de Identidade/SEGURANCA.MD:5-9`)
7. **No plain-text secrets.** Credentials, tokens, and passwords are encrypted via Windows DPAPI through `CredentialService` (`%AppData%`/`%UserProfile%\.AIB\credentials\*.bin`). Never write secrets to LiteDB or text files. (`Regras de Identidade/SEGURANCA.MD:11-14`)
8. **`RequiredLevel` as access control.** Dangerous tools must declare a high `RequiredLevel`; `ToolRegistry` strips low-level users' tool definitions from the LLM payload entirely (air-gap against prompt injection). (`Regras de Identidade/SEGURANCA.MD:16-19`)
9. **Embeddings stay local.** All RAG vectorization runs offline via `SmartComponents.LocalEmbeddings` (bge-micro-v2 ONNX). Only the active short-term context goes to OpenAI/Ollama. (`Regras de Identidade/SEGURANCA.MD:21-25`)

## Naming Patterns

### C# (AIBWindows)

**Files:**
- One public type per file. Filename matches type. Service classes end with `Service` (`OpenAIService.cs`, `MemoryService.cs`, `CredentialService.cs`).
- Tool implementations are grouped in `AIBWindows/Services/NativeTools.cs` (`NativeTools.cs:71` `ManageMemoryTool`, `:115` `ManageVaultTool`, `:163` `ReadFileTool`, `:281` `RunCommandTool`, `:365` `SearchWebTool`, `:395` `ReadScreenTool`, `:447` `ExecuteSkillTool`, `:480` `MaterializeSkillTool`, ...). One large file rather than one-tool-per-file.
- WPF view pairs use `<Name>Window.xaml` + `<Name>Window.xaml.cs` (`Views/ChatWindow.xaml.cs:23`, `Views/SettingsWindow.xaml.cs:8`, `Views/CommandConfirmationWindow.xaml.cs`).

**Types:**
- PascalCase for classes, methods, properties, events: `class OpenAIService`, `StreamResponseAsync(...)`, `public string Name { get; }`, `public event Action<int,int>? OnTokenCountChanged;` (`Services/OpenAIService.cs:13,23,183`).
- Tool classes end with `Tool` (`ManageMemoryTool`, `ReadFileTool`, `GlobTool`, `GrepTool`, `ListDirTool` — registered in `Services/ToolRegistry.cs:63-89`).
- Interfaces prefixed with `I`: `ITool` (`Services/ITool.cs:11`).
- Static utility classes: `LevelService`, `MemoryService`, `CredentialService`, `DirectoryService`, `DefaultSkills`, `SkillService`.

**Members:**
- Private fields prefixed with underscore + camelCase: `_history`, `_client`, `_settingsService`, `_toolRegistry`, `_tokenizer`, `_generationCts` (`Services/OpenAIService.cs:14-21`).
- Constants in SCREAMING_SNAKE_CASE: `SYSTEM_PROMPT`, `SILENCE_THRESHOLD_MS`, `WAKE_WORD` (`Services/OpenAIService.cs:42`, `Services/VoiceService.cs:32,36`).
- Method-local variables in camelCase: `userLevel`, `maxLoops`, `fullResponse`.

**Namespaces:**
- Root namespace `AIB`. UI under `AIB.Views`, services under `AIB.Services`. File-scoped namespace style (`namespace AIB.Services;`) is the dominant pattern — see `Services/OpenAIService.cs:11`, `Services/MemoryService.cs:10`, `Services/ToolRegistry.cs:7`.

### Python (AIBLinux)

**Files / modules:** snake_case. `app/chat_window.py`, `app/openai_client.py`, `app/hotkey.py`, `app/screenshot.py`. Single entry point `AIBLinux/main.py`.

**Types:** PascalCase. `class ChatWindow`, `class HotkeyListener`, `class StreamWorker`, `class DimOverlay`, `class MessageBubble`, `class OpenAIClient` (`app/chat_window.py:32,56,120,168`, `app/openai_client.py:13`, `app/hotkey.py:6`).

**Functions / variables:** snake_case. Private helpers prefixed with `_`: `_setup_ui`, `_make_tray_icon`, `_stream_thread`, `_bubble_count` (`main.py:16,56,82`, `app/chat_window.py:174-186`).

**Constants:** SCREAMING_SNAKE_CASE: `SCREEN_WIDTH_RATIO`, `SCROLL_HEIGHT_RATIO`, `MARGIN_BOTTOM`, `INPUT_HEIGHT`, `SYSTEM_PROMPT`, `HOTKEY` (`app/chat_window.py:22-26`, `app/openai_client.py:7`, `app/hotkey.py:9`).

## Code Style

**Formatting / linting tools:**
- No `.editorconfig`, no `Directory.Build.props`, no Roslyn analyzers in `AIBWindows/AIB.csproj`. C# style is enforced manually.
- No `.flake8`, `ruff.toml`, `pyproject.toml`, or `setup.cfg`. Python is uninstrumented. Not detected.

**Indent / line endings:** 4 spaces in both C# and Python. CRLF line endings in C# files (Windows). LF in Python files.

**Nullable:** `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` are on (`AIBWindows/AIB.csproj:6-7`). Code uses `?` annotations: `private ChatClient? _client;`, `string? finishReason = null` (`Services/OpenAIService.cs:15,264`).

**Async style:**
- ReAct streaming returns `async IAsyncEnumerable<string>` (`Services/OpenAIService.cs:183`). UI consumes via `await foreach`.
- Background work uses `await Task.Run(...)` (9 occurrences across 5 files — `Services/NativeTools.cs:192`, `Services/CommandService.cs`, `Services/OcrService.cs`, `Services/WindowTextExtractor.cs`, `Views/ChatWindow.xaml.cs`). Matches rule #4.
- Async warmup spawned with `Task.Run(() => WarmupAndKeepAliveAsync())` from the `OpenAIService` constructor (`Services/OpenAIService.cs:63`).

## Import Organization

### C#
Standard `using` block at file top, sorted roughly: BCL → third-party → project. Aliases appear inside namespace blocks where they hide ambiguity:

```csharp
using System;
using System.Collections.Generic;
using OpenAI.Chat;
using AIB.Services;
// Aliases for System.Drawing vs System.Windows.Media
using WColor   = System.Windows.Media.Color;
using WBrushes = System.Windows.Media.Brushes;
using LinearGB = System.Windows.Media.LinearGradientBrush;
```
(`Views/ChatWindow.xaml.cs:1-19`)

This implements rule #5 / `CODIGO_LIMPO.MD:28-31`. Aliases consistently use `W` prefix for WPF (`WColor`, `WBrushes`, `WPoint`, `WTranslate`).

### Python
Order: stdlib → third-party → local `app.*`. Per-method late imports are tolerated for optional/heavy dependencies (e.g., `from PyQt6.QtGui import QPixmap` inside `_make_tray_icon` at `main.py:20`, `from PyQt6.QtCore import pyqtProperty` inside `DimOverlay` at `app/chat_window.py:83`). No isort config.

## Comments and Documentation

**XML doc comments on public C# surfaces:** `ITool` is fully documented (`Services/ITool.cs:6-34`), as are several non-trivial methods (`Services/VoiceService.cs:185-188`, `Views/ChatWindow.xaml.cs:297-300`). Most service classes have a top-level `///` summary; smaller helpers are bare.

**Heavy inline rationale in Portuguese.** Decisions are explained next to the code, not in external docs:
- `Services/OpenAIService.cs:80-90` — why `num_ctx=16384` after observed empty-response behavior with gemma4.
- `Services/OpenAIService.cs:200-203` — why `maxLoops = 18` after raising from 5.
- `Services/OpenAIService.cs:226-244` — full state-machine explanation for the 3-mode streaming sanitizer (Streaming / InsideThink / WaitingFinal) and Harmony channel handling.
- `Services/OpenAIService.cs:247-254` — carry-buffer rationale to avoid splitting `<think>` markers across chunks.

**Section banners** in long files use a fixed ASCII box style:
```csharp
// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: run_command — Executa comandos de terminal
// ─────────────────────────────────────────────────────────────────────────────
```
(`Services/NativeTools.cs:67-69, :111-113, :159-161, :277-279`, also `Views/ChatWindow.xaml.cs:205-207`).

**Python:** module docstrings on every file (`AIBLinux/main.py:1`, `app/chat_window.py:1`, `app/openai_client.py:1`, `app/hotkey.py:1`, `app/screenshot.py:1`). Sparse one-liner docstrings on functions; section banners use `# ──────────────────────────`. No PEP 257-grade per-function docstrings.

**Language:** comments and docstrings are in Portuguese (BR). User-facing strings from tools and UI also Portuguese (matches the system prompt at `Services/OpenAIService.cs:42-54` which forces PT-BR responses).

## Error Handling

**Tools never throw to the caller.** Per rule #3, every `ExecuteAsync(...)` catches and returns a Portuguese error string. Examples:

```csharp
// Services/NativeTools.cs:200-204
catch (Exception ex)
{
    return $"ERRO ao ler {ext}: {ex.Message}";
}
```

```csharp
// Services/CredentialService.cs:48-51
catch (Exception ex)
{
    return $"ERRO ao armazenar credencial: {ex.Message}";
}
```

`ToolRegistry.ExecuteToolAsync` adds a second safety net so a tool throwing still degrades to a string the LLM can read (`Services/ToolRegistry.cs:40-48`).

**Error strings follow conventions:**
- Tool errors begin with `ERRO:` or `ERRO ao <action>:` and end with the underlying message in PT-BR.
- Sandbox/permission rejections begin with `ACESSO NEGADO (SANDBOX):` or `ACESSO NEGADO:` followed by the rule that fired (`Services/NativeTools.cs:325, 335, 339, 346, 353`, `Services/ToolRegistry.cs:37`).
- Success messages use `SUCESSO:` prefix (`Services/SkillService.cs:142, 173, 238`).

**Empty `catch { }` blocks are tolerated for non-critical paths** (15 occurrences across 8 files). Mostly used in:
- `MemoryService.GetRecentMemories` swallows but logs (`Services/MemoryService.cs:152-156`).
- `SkillService.ListLocalSkills` swallows per-file parse failures so one bad skill doesn't break the list (`Services/SkillService.cs:84, 100`).
- `SettingsService.LoadSettings` falls back to plaintext-legacy on DPAPI failure (`Services/SettingsService.cs:65-78`).
- `OpenAIService.ResetHistory` swallows skill-list errors (`Services/OpenAIService.cs:173`).

This is intentional and aligned with the "don't crash the tray app" philosophy, but it's a real auditability gap — see `CONCERNS.md` (when written).

**WPF-specific exception trap.** Global startup is wrapped to show a user-friendly modal instead of an unhandled exception dialog (`App.xaml.cs:72-80`):
```csharp
catch (Exception ex)
{
    System.Windows.MessageBox.Show($"Erro crítico ao iniciar o AIB:\n\n{ex.Message}", ...);
    Current.Shutdown();
}
```

## Logging

**Framework:** plain `Console.WriteLine`. No `Microsoft.Extensions.Logging`, Serilog, or NLog dependency in `AIBWindows/AIB.csproj`. 49 occurrences across 12 files.

**Tag conventions:** structured prefixes in brackets so the dev console is greppable. 24 tagged log calls across the major services:
- `[REGISTRY]` — tool registration/execution (`Services/ToolRegistry.cs:39, 51, 58, 87`).
- `[DEBUG-COFRE]` — credential vault lookups (`Services/CredentialService.cs:59, 64, 73, 78, 88, 91`).
- `[STREAM-DBG]`, `[STREAM-END]` — ReAct stream introspection, gated by `VerboseConsoleLogging` setting (`Services/OpenAIService.cs:255-302`).
- `[WARMUP]`, `[WARMUP ERRO]` — Ollama keep-alive (`Services/OpenAIService.cs:78, 95, 101, 122, 134`).
- `[HOTKEY]` — global hotkey registration failures (`App.xaml.cs:69`).

`AIBWindows/funcionalities.md:88-89` also names `[DEBUG-IA]` and `[PERF]` as expected tags for "TTFT" diagnostics — those tags are not yet present in the source code; partial divergence from the declared spec.

**Python:** no logging framework either. `print(...)` only in `AIBLinux/verify_ollama.py:16-30`. No structured logging in `AIBLinux/app/`.

## Threading / UI Dispatcher

19 `Dispatcher.Invoke` / `Dispatcher.BeginInvoke` calls across 6 files. Background callbacks correctly marshal to the UI thread before touching XAML elements:

```csharp
// Views/ChatWindow.xaml.cs:112
private void HandleWarmupState(bool isWarmingUp)
{
    Dispatcher.BeginInvoke(() =>
    {
        InputBox.IsEnabled = !isWarmingUp;
        StatusBar.Visibility = isWarmingUp ? Visibility.Visible : Visibility.Collapsed;
        ...
    });
}
```

Matches rule #4 / `CODIGO_LIMPO.MD:23-26`.

## Composition Root Pattern (no DI)

`ChatWindow` is the Composition Root (rule #1). Constructor body manually wires services (`Views/ChatWindow.xaml.cs:39-104`):

```csharp
_settingsService = new SettingsService();
_openAIService   = new OpenAIService(_settingsService);
_openAIService.OnTokenCountChanged += UpdateTokenCounterUI;
_openAIService.OnWarmupStateChanged += HandleWarmupState;
_shadowService   = new ShadowAssistantService(_openAIService, _settingsService);
_voiceService    = new VoiceService();
```

`OpenAIService` then constructs `ToolRegistry` internally (`Services/OpenAIService.cs:59`), and `ToolRegistry` `new`s each `ITool` implementation in `RegisterNativeTools` (`Services/ToolRegistry.cs:63-89`). No service locator, no factory abstraction.

**Divergence:** `App.xaml.cs:36` also `new`s `ChatWindow()` — the *real* root is `App.OnStartup` (`App.xaml.cs:26-71`). `ChatWindow` is the *services* composition root only. `Regras de Identidade/CODIGO_LIMPO.MD:7` says "A classe `ChatWindow.xaml.cs` é o nosso Composition Root" — accurate for the agent stack, slightly imprecise about the overall app shell.

## Function / Method Design

- **Long methods exist where the state machine demands it.** `OpenAIService.StreamResponseAsync` (`Services/OpenAIService.cs:183` onward, ~400+ lines) is the canonical example: it implements ReAct loop + dual-mode tool execution + Harmony-channel sanitization + carry-buffer chunk handling in one continuous flow. Refactoring is explicitly avoided here to keep the streaming hot-path linear.
- **Tools are tiny.** Each `ITool` is one class with `Name`, `Description`, `RequiredLevel`, `ChatToolDefinition`, and a small `ExecuteAsync` (see `Services/NativeTools.cs:71-109` for the canonical 40-line shape).
- **Parameter parsing centralized.** All tools call the static `ToolArgParser.Get(json, key)` helper which tries strict `JsonDocument` parse → trimmed-braces re-parse → regex string-extraction fallback (`Services/NativeTools.cs:16-65`). This is the project's defensive answer to small LLMs that emit slightly malformed JSON.

## Module Design

**Exports / barrel files:** Not used. C# uses namespaces; each consumer adds a `using AIB.Services;` (no `Services.Tools` sub-namespace despite the `NativeTools.cs` grouping).

**Python:** `app/__init__.py` is empty (14 bytes) — namespace package only. No re-exports.

## Tool Schema Convention

Every `ITool` declares its OpenAI function schema as a raw JSON string via `BinaryData.FromString("""...""")`:

```csharp
public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
    Name, Description,
    BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "action": { "type": "string", "description": "'remember' para salvar..." },
        "key":    { "type": "string", "description": "..." }
      },
      "required": ["action", "key"]
    }
    """));
```
(`Services/NativeTools.cs:77-89`)

Field descriptions are in Portuguese to match the model's response language. `"required"` arrays must match the keys checked by `ExecuteAsync`.

## Sandbox / Permission Pattern

`RunCommandTool.ExecuteAsync` (`Services/NativeTools.cs:315-358`) layers four sandbox tiers based on `userLevel`, encoded as inline rule arrays:

```csharp
string[] sysDirs      = { "appdata", "windows", "program files", "programdata" };
string[] destructives = { "rm", "del", "erase", "remove-item", "ri", "out-file", ">", ">>", ... };
string[] netCmds      = { "curl", "wget", "invoke-webrequest", "iwr", "ping", "tracert", ... };
```

Matches whole words with a `\b`-equivalent helper `ContainsWord` (`:304-313`) so `firm` does not trigger `rm`. This implements rule #8 (gamification-as-firewall) — except that there's no UI confirmation modal in this path: a dangerous command at Level 8+ executes directly through `CommandService.ExecuteAsync`. **Divergence from `SEGURANCA.MD:6-8`**, which requires the `CommandConfirmationWindow` modal for every shell call. The `ConfirmDangerousCommands` setting exists (`Services/SettingsService.cs:36`) but is not consulted from `RunCommandTool`. See CONCERNS.md (concerns focus).

## Credential / Secret Handling

All credential storage goes through `Services/CredentialService.cs`:
- Files written to `%UserProfile%/.AIB/credentials/<system>.bin`.
- Encrypted via `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` (`Services/CredentialService.cs:42-44`).
- `manage_vault` tool requires `RequiredLevel = 7` (`Services/NativeTools.cs:119`).
- Settings file `profile.dat` is also DPAPI-encrypted with a plaintext-legacy fallback for migration (`Services/SettingsService.cs:60-79`).

Aligned with rule #7.

## Git / Branch / Commit Conventions

From `git log --oneline -30`:

**Branch model (observable):**
- `main` is the stable trunk.
- `feature/<topic>` for in-progress work: `feature/agent-upgrades`, `feature/shadow-assistant`, `feature/rpa-automation`.
- `claude/<slug>` branches indicate AI-assisted experimental branches (`claude/practical-liskov-67c90c`, `claude/youthful-wiles-52cb9f`, ...).

**Commit message style:** Conventional Commits-ish, lowercase, Portuguese subject after the type:
- `feat(agent): tuning do ReAct loop, parallel tools, sanitizacao de streaming`
- `feat(shadow): UIA, multi-monitor, sidebar, melhorias visuais e bugfixes`
- `feat(shadow): troca polling de tela inteira por dwell em janela hovered`
- `fix: corrige bugs do agente ReAct e refatora skills/tools`
- `docs: map existing codebase`
- `update llm local` (loose, pre-convention)
- `WIP: Shadow Assistant (shelved for hardware limitations)` (WIP convention)
- Older commits (`initial commit`, `first commit`, `update de live voice e refatoração...`) predate the convention.

**Scope tags observed:** `agent`, `shadow`. No git hooks, no commitlint config detected.

## Identified Divergences (declared rule vs. actual code)

| Rule | Source | Code state | Severity |
|------|--------|-----------|----------|
| Modal confirmation for every shell command | `SEGURANCA.MD:5-9` | `RunCommandTool` (`NativeTools.cs:315`) calls `CommandService.ExecuteAsync` directly when level passes; the `CommandConfirmationWindow` (`Views/CommandConfirmationWindow.xaml`) is not invoked from this path. | High |
| Logs use `[DEBUG-IA]` / `[PERF]` tags | `funcionalities.md:88-89` | Tags present in code: `[REGISTRY]`, `[STREAM-DBG]`, `[STREAM-END]`, `[WARMUP]`, `[DEBUG-COFRE]`, `[HOTKEY]`. `[DEBUG-IA]` and `[PERF]` not detected. | Low (cosmetic) |
| One service per integration (SRP) | `CODIGO_LIMPO.MD:10-15` | Mostly followed. `Services/NativeTools.cs` (44 KB, 13 tool classes) is a deliberate exception: it groups every tool to keep schemas adjacent and avoid 13 trivial files. | Low (intentional) |
| `ChatWindow.xaml.cs` is the Composition Root | `CODIGO_LIMPO.MD:5-8` | True for services. App-shell wiring (notify icon, hotkey, settings bootstrap) lives in `App.xaml.cs:26-71`. | Low (semantic) |
| File-scoped namespaces | dominant pattern | `Services/VoiceService.cs:11`, `Services/ContextService.cs`, `Services/ReminderService.cs`, `Services/ChatHistoryService.cs`, `Views/ContextSidebar.xaml.cs` still use block `namespace AIB.Services { ... }`. | Low (consistency) |
| Aliased namespaces for `System.Drawing` vs `System.Windows.Media` | `CODIGO_LIMPO.MD:28-31` | Followed in `Views/ChatWindow.xaml.cs:13-19`. | OK |
| Tools never crash | `CODIGO_LIMPO.MD:17-21` | Followed; `ToolRegistry` also wraps in `try/catch` as a backstop (`ToolRegistry.cs:40-48`). | OK |
| `Dispatcher` discipline | `CODIGO_LIMPO.MD:23-26` | Followed; 19 sites verified. | OK |
| DPAPI for secrets | `SEGURANCA.MD:11-14` | Followed by `CredentialService` and `SettingsService`. | OK |
| Local-only embeddings | `SEGURANCA.MD:21-25` | Followed via `SmartComponents.LocalEmbeddings` (`MemoryService.cs:7,24`). | OK |
| `RequiredLevel` gating | `SEGURANCA.MD:16-19` | Followed; `ToolRegistry.GetActiveTools` filters by level before sending to LLM (`ToolRegistry.cs:24-25`). | OK |

---

*Convention analysis: 2026-05-28*
