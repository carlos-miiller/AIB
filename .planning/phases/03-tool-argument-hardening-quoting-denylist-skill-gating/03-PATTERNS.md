# Phase 3: tool-argument-hardening (quoting + denylist + skill gating) — Pattern Map

**Mapped:** 2026-05-31
**Files analyzed:** 9 (1 new, 7 modified, 1 deleted)
**Analogs found:** 9 / 9 — every file has a strong analog inside Phase 1's shipped code

---

## Reading guide

Phase 3 is overwhelmingly a **reuse + extension** phase, not a greenfield one. Every new behavior has a Phase 1 analog already in the tree:

- The modal-hop pattern (`_modalLock` → `Dispatcher.InvokeAsync` → `ShowDialog`) is verbatim in `NativeTools.cs:360-375` — Phase 3 lifts it into a single static helper so `ExecuteSkillTool` and `MaterializeSkillTool` reuse it instead of duplicating it.
- The audit-log entry shape (`BuildEntry`) is verbatim in `NativeTools.cs:451-460` — Phase 3 adds two optional fields (`content_hash`, plus the new `allow_then_floor_deny` outcome string) without breaking the JSONL schema.
- The `CommandConfirmationContext` POCO with `{ get; init; }` is already in place; Phase 3 only adds five more init-only properties.
- `CommandService.ExecuteAsync` already contains the canonical "read pump + timeout-kill + 50KB truncation" block; Phase 3 extracts it into a private helper so the new `ExecuteWithArgListAsync` reuses it byte-for-byte.

The single piece of net-new behavior with no codebase analog is the floor-list normalize-then-regex pipeline (`CommandFloorList.Match`). It is small enough (~70 LoC) to be its own file per CODIGO_LIMPO.MD "one service per system integration".

---

## File Classification

| File | New / Modified / Deleted | Role | Data Flow | Closest Analog | Match Quality |
|------|--------------------------|------|-----------|----------------|---------------|
| `AIBWindows/Services/CommandService.cs` | **MODIFY** | static service wrapping process spawn | request-response (process I/O) | itself — `ExecuteAsync` lines 10-72 already contains the full pump | self-reference (extract + add sibling) |
| `AIBWindows/Services/CommandFloorList.cs` | **NEW** | static helper / pure transform | sync request-response (string → match-or-null) | `NativeTools.cs:311-319` `ContainsWord` + `:413-448` `ApplyDenylist` (both DELETED) | partial — replaces, doesn't mirror |
| `AIBWindows/Services/SkillService.cs` | **MODIFY** | static service wrapping skill exec + install | request-response | itself — `RunSkillAsync` lines 186-205 + `InstallFromOnlineAsync` lines 147-177 | self-reference (rewrite specific blocks) |
| `AIBWindows/Services/DynamicSkillTool.cs` | **DELETE** | dead `ITool` (zero `ToolRegistry` registration) | n/a | n/a — verified absent from `ToolRegistry.cs:65-81` | n/a |
| `AIBWindows/Services/NativeTools.cs` (`RunCommandTool`) | **MODIFY** | `ITool`, request-response with UI hop | modal hop → floor-gate → exec | itself — `RunCommandTool.ExecuteAsync:322-407` is the canonical Phase 1 modal flow | self-reference (insert floor steps) |
| `AIBWindows/Services/NativeTools.cs` (`ExecuteSkillTool`) | **MODIFY** | `ITool`, request-response with UI hop | modal hop → exec | `RunCommandTool.ExecuteAsync:322-407` (Phase 1) | role+flow exact — same modal-hop shape |
| `AIBWindows/Services/NativeTools.cs` (`MaterializeSkillTool`) | **MODIFY** | `ITool`, request-response with UI hop | modal hop → exec | `RunCommandTool.ExecuteAsync:322-407` (Phase 1) | role+flow exact — same modal-hop shape |
| `AIBWindows/Services/CommandConfirmationContext.cs` | **MODIFY** | immutable POCO / data carrier | in-memory ctor → consumed | itself — 4 existing `{ get; init; }` properties | self-reference (add 5 more) |
| `AIBWindows/Services/AlwaysAllowSession.cs` | **MODIFY** | static singleton, in-memory `HashSet<>` | add/contains/clear, lock-guarded | itself — re-key the existing `HashSet<string>` to a tuple | self-reference (signature change) |
| `AIBWindows/Views/CommandConfirmationWindow.xaml` | **MODIFY** | WPF Window XAML | declarative layout | itself — Phase 1 added 3 rows; Phase 3 adds 2 more | self-reference (extend Grid) |
| `AIBWindows/Views/CommandConfirmationWindow.xaml.cs` | **MODIFY** | WPF Window code-behind + new static helper | ctor populates fields; new `ShowAsync` helper | `RunCommandTool.ExecuteAsync:360-375` (modal-hop body to lift) | role+flow exact — lifting Phase 1 code |
| `AIBWindows/Services/AuditLogService.cs` | **NO STRUCTURAL CHANGE** | static append-only writer | JSONL append per call | itself — anonymous-object schema absorbs new field | self-reference (caller-side only) |
| `Regras de Identidade/SEGURANCA.MD` | **MODIFY** | governance doc | static markdown | Phase 2's NFR-03 rewrite (commit `f4904d0..` family) | role match — present-tense pattern |

---

## Pattern Assignments

### 1) `AIBWindows/Services/CommandService.cs` — extract `RunProcessAsync` helper + add `ExecuteWithArgListAsync` (D-05)

**Analog:** itself — existing `ExecuteAsync(string command, string? workDir, int timeoutMs)` lines 10-72. The body is already the canonical "pump stdout/stderr → wait with timeout → kill on timeout → truncate at 50KB → return string" pattern.

**Existing imports** (`CommandService.cs:1-4`):
```csharp
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
```

**Existing pump block to extract** (`CommandService.cs:26-66`, the "tail" after `ProcessStartInfo` construction):
```csharp
using var process = new Process { StartInfo = startInfo };
var output = new StringBuilder();
var error = new StringBuilder();

process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

process.Start();
process.BeginOutputReadLine();
process.BeginErrorReadLine();

var finished = await Task.Run(() => process.WaitForExit(timeoutMs));

if (finished)
{
    // Espera um pouco mais para garantir que todo o texto saiu dos buffers
    process.WaitForExit();
}
else
{
    try { process.Kill(true); } catch { }
    return $"[TIMEOUT] O comando demorou mais de {timeoutMs/1000}s.\nOutput parcial:\n{output}";
}

string result = output.ToString();
string err = error.ToString();

if (!string.IsNullOrWhiteSpace(err))
{
    result += $"\n[ERRO]:\n{err}";
}

if (string.IsNullOrWhiteSpace(result)) return "[Comando executado, mas não retornou saída]";

// Trunca se for muito grande
if (result.Length > 50000)
{
    result = result.Substring(0, 50000) + "\n\n[AVISO: Saída muito longa, truncada...]";
}

return result;
```

**How to apply:**

1. Move lines 26-66 verbatim into a new private helper `RunProcessAsync(Process process, int timeoutMs)`. The `Process` is passed in already-constructed (caller built the `ProcessStartInfo`). The helper does NOT instantiate `Process` and does NOT call `using` on it — caller owns the `using`.

   **Important deviation from the literal block:** the existing block uses `using var process = new Process { ... }` inside `ExecuteAsync`. When extracting, the **caller** must keep `using var process = new Process { StartInfo = psi };` and the helper accepts `Process` as a parameter. This keeps the `IDisposable` boundary at the caller (matches `ExecuteAsync` today, which constructs + disposes locally).

2. Refactor `ExecuteAsync(string command, ...)` to:
   - Build `ProcessStartInfo` with `FileName = "cmd.exe"`, `Arguments = $"/c {command}"` (UNCHANGED — SEC-04 carve-out per CONTEXT.md D-05).
   - `using var process = new Process { StartInfo = startInfo };`
   - `return await RunProcessAsync(process, timeoutMs);`
   - Wrap in the existing `try/catch (Exception ex) { return $"ERRO ao executar comando: {ex.Message}"; }` (CommandService.cs:68-71).

3. Add new sibling method `ExecuteWithArgListAsync(string fileName, IEnumerable<string> args, string? workDir = null, int timeoutMs = 20000)`:
   ```csharp
   public static async Task<string> ExecuteWithArgListAsync(
       string fileName,
       IEnumerable<string> args,
       string? workDir = null,
       int timeoutMs = 20000)
   {
       try
       {
           var psi = new ProcessStartInfo
           {
               FileName = fileName,
               RedirectStandardOutput = true,
               RedirectStandardError = true,
               UseShellExecute = false,             // REQUIRED for ArgumentList
               CreateNoWindow = true,
               StandardOutputEncoding = Encoding.UTF8,
               WorkingDirectory = string.IsNullOrWhiteSpace(workDir)
                   ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                   : workDir
           };
           foreach (var arg in args) psi.ArgumentList.Add(arg);

           using var process = new Process { StartInfo = psi };
           return await RunProcessAsync(process, timeoutMs);
       }
       catch (Exception ex)
       {
           return $"ERRO ao executar comando: {ex.Message}";
       }
   }
   ```

**Goal: both methods stay under ~50 lines each** (CONTEXT.md Claude's Discretion). Verified by line-count: extracted helper is ~30 lines; `ExecuteAsync` shrinks to ~25 lines; `ExecuteWithArgListAsync` is ~25 lines.

---

### 2) `AIBWindows/Services/CommandFloorList.cs` — NEW (D-02, D-03)

**Analog:** none in the codebase — this is the one piece of net-new behavior. The closest reference is `NativeTools.cs:311-319` (`ContainsWord` helper — DELETED) and `NativeTools.cs:413-448` (`ApplyDenylist` — DELETED). Use the deleted code's **shape** (static class, regex with `\b`, PT-BR reason strings prefixed) but with the new normalize-then-match pipeline.

**Imports template** (file-scoped namespace per CONVENTIONS.md; matches `LevelService.cs`, `DirectoryService.cs`, `ShadowHistoryService.cs`):
```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AIB.Services;
```

**Shape to write** (copy verbatim from RESEARCH.md §"Pattern 2", adapted to project conventions):
```csharp
/// <summary>
/// Floor list de comandos destrutivos para <c>run_command</c>. Roda APÓS o
/// modal (D-04) e refuta apenas quando <c>userLevel &lt; 7</c> e
/// <c>ConfirmDangerousCommands == ON</c>. Em L&gt;=7 ou com a flag OFF,
/// o modal é a autoridade única (D-01, herda Phase 1 D5/D8).
///
/// Pipeline (D-03):
///   1. Lowercase.
///   2. Colapsa concat de strings PowerShell: <c>"x" + "y"</c> -> <c>xy</c>.
///   3. Expande aliases destrutivos: <c>mv</c> -> <c>Move-Item</c>, etc.
///   4. <c>-EncodedCommand</c> -> recusa imediata (não-decodável aqui).
///   5. Regex com <c>\b</c> word boundaries; first match wins.
///
/// Documentado em SEGURANCA.MD como BEST-EFFORT — o modal é o gate real.
/// </summary>
public static class CommandFloorList
{
    private static readonly (Regex Pattern, string Category, string Reason)[] _entries = new[]
    {
        // Recursive deletion
        (new Regex(@"\brm\s+(-rf|-r\s+-f|-f\s+-r|-r)\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "recursive-delete",
         "ACESSO NEGADO (FLOOR): deleção recursiva (rm -r/-rf) — requer Nível 7."),
        (new Regex(@"\bdel\s+/s\b|\bdel\s+/f\s+/s\b|\brmdir\s+/s\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "recursive-delete",
         "ACESSO NEGADO (FLOOR): deleção recursiva (del /s, rmdir /s) — requer Nível 7."),
        (new Regex(@"\bremove-item\b.*\b-recurse\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "recursive-delete",
         "ACESSO NEGADO (FLOOR): deleção recursiva (Remove-Item -Recurse) — requer Nível 7."),
        // Format / partition
        (new Regex(@"\bformat\b|\bdiskpart\b|\bwmic\s+logicaldisk\b|\bcipher\s+/w\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "format",
         "ACESSO NEGADO (FLOOR): formatação/partição de disco — requer Nível 7."),
        // Shutdown / logoff
        (new Regex(@"\bshutdown\b|\brestart-computer\b|\bstop-computer\b|\blogoff\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "shutdown",
         "ACESSO NEGADO (FLOOR): desligamento/reboot/logoff — requer Nível 7."),
        // Registry destructive
        (new Regex(@"\breg\s+delete\b|\bremove-itemproperty\b.*\b-path\s+hk|\bremove-item\b.*\b-path\s+hk",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "registry-destructive",
         "ACESSO NEGADO (FLOOR): operação destrutiva no registro do Windows — requer Nível 7."),
    };

    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mv"]  = "move-item",
        ["ri"]  = "remove-item",
        ["ni"]  = "new-item",
        ["sc"]  = "set-content",
        ["ac"]  = "add-content",
        ["gci"] = "get-childitem",
    };

    private static readonly Regex _concatPattern =
        new(@"[""']\s*\+\s*[""']", RegexOptions.Compiled);
    private static readonly Regex _aliasPattern =
        new(@"\b(mv|ri|ni|sc|ac|gci)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _encodedCmdPattern =
        new(@"-encodedcommand\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Retorna <c>(Hit: true, Reason)</c> se o comando bate em uma entrada do floor.
    /// Em <c>userLevel &gt;= 7</c> sempre retorna <c>(false, null)</c> (D-01: floor inativo).
    /// </summary>
    public static (bool Hit, string? Reason) Match(string command, int userLevel)
    {
        if (userLevel >= 7) return (false, null);

        // Step 1: lowercase
        string normalized = command.ToLowerInvariant();

        // Step 2: collapse PowerShell quote-concat ("Remove" + "-Item" -> "Remove-Item")
        normalized = _concatPattern.Replace(normalized, "");

        // Step 3: expand destructive aliases
        normalized = _aliasPattern.Replace(normalized, m => _aliases[m.Value]);

        // Step 4: -EncodedCommand outright refuse at L<7
        if (_encodedCmdPattern.IsMatch(normalized))
            return (true, "ACESSO NEGADO (FLOOR): powershell -EncodedCommand não é avaliável — requer Nível 7.");

        // Step 5: floor regex match (first wins)
        foreach (var (pattern, _, reason) in _entries)
            if (pattern.IsMatch(normalized)) return (true, reason);

        return (false, null);
    }
}
```

**Copy-from-codebase notes:**
- File-scoped namespace `namespace AIB.Services;` — matches `LevelService.cs`, `DirectoryService.cs`, `AlwaysAllowSession.cs`, `CommandConfirmationContext.cs`. Do NOT use block-namespace.
- Reason prefix `"ACESSO NEGADO (FLOOR):"` mirrors the existing `"ACESSO NEGADO (SANDBOX):"` family in `NativeTools.cs:417, 426, 430, 437, 444` — substitute `SANDBOX → FLOOR` per CONTEXT.md Claude's Discretion.
- `Regex` with `RegexOptions.Compiled` is consistent with hot-path matchers; the existing `ContainsWord` did NOT use `Compiled`, but the new floor list runs on every `run_command` so compilation is worth the one-time JIT cost.

---

### 3) `AIBWindows/Services/SkillService.cs` — `InterpreterMap` + `RunSkillAsync` rewrite + `InstallFromOnlineAsync` hardening (D-06, D-07, D-11)

**Analog:** itself — `SkillService.cs:186-205` (`RunSkillAsync`) and `:147-177` (`InstallFromOnlineAsync`).

**Existing imports** (`SkillService.cs:1-6`):
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
```

**Add:** `using System.Text.RegularExpressions;` (new — for D-11 regex check).

**Existing switch expression to DELETE** (`SkillService.cs:194-205`):
```csharp
string scriptPath = skill.ScriptFile;
string safeArgs = arguments.Replace("\n", " ").Replace("\r", "");
string command = skill.Interpreter.ToLower() switch
{
    "python" => $"py \"{scriptPath}\" {safeArgs}",
    "powershell" => $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {safeArgs}",
    "cmd" => $"cmd.exe /c \"{scriptPath}\" {safeArgs}",
    _ => throw new Exception("Interpretador não suportado.")
};

return await CommandService.ExecuteAsync(command, Path.GetDirectoryName(scriptPath));
```

**Replace with InterpreterMap + ExecuteWithArgListAsync** (per D-06, D-07):
```csharp
private static readonly Dictionary<string, (string FileName, string[] Switches)> InterpreterMap
    = new(StringComparer.OrdinalIgnoreCase)
{
    ["python"]     = ("py.exe",         Array.Empty<string>()),
    ["powershell"] = ("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" }),
    ["cmd"]        = ("cmd.exe",        new[] { "/c" }),
};

public static async Task<string> RunSkillAsync(string name, string arguments)
{
    var skill = ListLocalSkills().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (skill == null) return $"Erro: Skill '{name}' não encontrada.";

    if (skill.Interpreter == "markdown")
        return $"INSTRUÇÕES DA SKILL '{name}':\n\n{File.ReadAllText(skill.ScriptFile)}\n\nSugestão: Use 'materialize_skill' para criar um script para esta skill.";

    if (!InterpreterMap.TryGetValue(skill.Interpreter, out var pair))
        return $"Erro: interpretador '{skill.Interpreter}' não suportado.";

    // D-06: argumentsString reaches the script as a SINGLE argv element.
    // ArgumentList preserves literal bytes — no shell interpretation.
    // The \n/\r/\0 strip is defense-in-depth against weird argv display in logs,
    // NOT a security control (ArgumentList already neutralizes shell metachars).
    string safeArgs = (arguments ?? "").Replace("\n", " ").Replace("\r", "").Replace("\0", "");

    var args = new List<string>(pair.Switches) { skill.ScriptFile, safeArgs };
    return await CommandService.ExecuteWithArgListAsync(
        pair.FileName, args, Path.GetDirectoryName(skill.ScriptFile));
}
```

**Existing `InstallFromOnlineAsync` block to harden** (`SkillService.cs:147-177`):
```csharp
public static async Task<string> InstallFromOnlineAsync(string url)
{
    string workPath = Path.Combine(Path.GetTempPath(), "AIB_Skills_Work");
    try
    {
        EnsureDir();
        if (!Directory.Exists(workPath)) Directory.CreateDirectory(workPath);
        string installArg = url;
        if (url.Contains("skills.sh/"))
        {
            var urlParts = url.Split("skills.sh/", StringSplitOptions.RemoveEmptyEntries)[1]
                              .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (urlParts.Length >= 3) installArg = $"{urlParts[0]}/{urlParts[1]}@{urlParts[2]}";
        }

        string result = await CommandService.ExecuteAsync(
            $"cmd /c call npx -y skills add {installArg} --yes", workPath, 900000);
        // ... existing copy-from-.agents/skills block ...
    }
    catch (Exception ex) { return $"Erro: {ex.Message}"; }
}
```

**Replace with regex check + ArgumentList + `npx.cmd` fallback** (per D-11 + Pitfall #2):
```csharp
/// <summary>
/// Instala uma skill a partir de owner/repo[@version] via npx.
///
/// IMPORTANTE: este método NÃO tem callers no Phase 3 — é dead code defensivo.
/// Callers futuros DEVEM gatear esta chamada atrás de:
///   1. RequiredLevel &gt;= 7 no ITool exposto;
///   2. Hop pelo CommandConfirmationWindow.ShowAsync(ctx) (D-08).
/// O regex abaixo é a defesa load-bearing contra path-traversal / shell-injection
/// no installArg até o npx.cmd shim (cmd.exe re-parsa metacaracteres apesar do
/// ArgumentList — BatBadBut CVE-2024-1874 family).
/// </summary>
public static async Task<string> InstallFromOnlineAsync(string url)
{
    string workPath = Path.Combine(Path.GetTempPath(), "AIB_Skills_Work");
    try
    {
        EnsureDir();
        if (!Directory.Exists(workPath)) Directory.CreateDirectory(workPath);
        string installArg = url;
        if (url.Contains("skills.sh/"))
        {
            var urlParts = url.Split("skills.sh/", StringSplitOptions.RemoveEmptyEntries)[1]
                              .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (urlParts.Length >= 3) installArg = $"{urlParts[0]}/{urlParts[1]}@{urlParts[2]}";
        }

        // D-11: regex BEFORE any shell-out — load-bearing defense vs the npx.cmd shim
        // re-parsing cmd metacharacters (ArgumentList does NOT escape & | ^ < > %).
        if (!Regex.IsMatch(installArg, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$"))
            return "Erro: formato de skill inválido (esperado: owner/repo[@version]).";

        // D-11: ArgumentList path. npx is a cmd-shim on Windows; CreateProcess does NOT
        // honor PATHEXT under UseShellExecute=false, so try npx.cmd first, fall back to npx.
        var npxArgs = new[] { "-y", "skills", "add", installArg, "--yes" };
        string result;
        try
        {
            result = await CommandService.ExecuteWithArgListAsync(
                "npx.cmd", npxArgs, workPath, 900_000);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2) // ERROR_FILE_NOT_FOUND
        {
            result = await CommandService.ExecuteWithArgListAsync(
                "npx", npxArgs, workPath, 900_000);
        }

        string agentsSkillsPath = Path.Combine(workPath, ".agents", "skills");
        if (Directory.Exists(agentsSkillsPath))
        {
            foreach (var skillDir in Directory.GetDirectories(agentsSkillsPath))
            {
                string skillName = Path.GetFileName(skillDir);
                string targetPath = Path.Combine(SkillsDir, skillName);
                if (Directory.Exists(targetPath)) Directory.Delete(targetPath, true);
                CopyDirectory(skillDir, targetPath);
            }
            return $"SUCESSO: A habilidade '{installArg}' foi instalada.";
        }
        return $"FALHA na instalação: {result}";
    }
    catch (Exception ex) { return $"Erro: {ex.Message}"; }
}
```

**Note on the catch:** `ExecuteWithArgListAsync` wraps its `Process.Start` in `try/catch (Exception ex)` and returns a string starting with `"ERRO ao executar comando:"` — so the `Win32Exception` will NOT propagate out as a thrown exception today. **Planner action:** decide whether to (a) tighten `ExecuteWithArgListAsync` to rethrow `Win32Exception` with `NativeErrorCode == 2` instead of swallowing it, or (b) inspect the returned string for `"ERRO ao executar comando: The system cannot find the file specified"` and fall back accordingly. Option (a) is cleaner and matches RESEARCH.md Pitfall #2 idiom; document the chosen path in PLAN.md.

---

### 4) `AIBWindows/Services/DynamicSkillTool.cs` — DELETE (D-12)

**Analog:** n/a — outright deletion.

**Verification before delete:**
- `ToolRegistry.cs:65-81` registers 12 tools; `DynamicSkillTool` is **absent** from the list (verified — see "Registered tools" block above).
- `ToolRegistry.cs:9-12` comment explicitly says skills are not registered individually anymore (lazy-loaded via `execute_skill`).
- csproj is SDK-style with implicit `<Compile Include="**/*.cs"/>` (verified via `AIB.csproj` — no `<Compile Remove>` or explicit include for this file).

**Action:** `git rm AIBWindows/Services/DynamicSkillTool.cs`. Then `dotnet build AIBWindows/AIB.csproj` must report 0 errors / 0 new warnings.

**Post-delete grep check:** `grep -rn "DynamicSkillTool" AIBWindows/` must return zero matches.

---

### 5) `AIBWindows/Services/NativeTools.cs` — `RunCommandTool.ExecuteAsync` floor-list insertion (D-04)

**Analog:** itself — current `RunCommandTool.ExecuteAsync` lines 322-407 is the canonical Phase 1 modal flow.

**Current Phase 1 modal-hop block** (`NativeTools.cs:322-407` — abbreviated; copy structure verbatim):
```csharp
public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
{
    // [1] Parse + validate
    string command = ToolArgParser.Get(argumentsJson, "command");
    if (string.IsNullOrWhiteSpace(command)) return "ERRO: 'command' é obrigatório.";

    string cwd = DirectoryService.DataDir;

    // [3] Build context
    var ctx = new CommandConfirmationContext
    {
        Tool = "run_command",
        Command = command,
        Level = userLevel,
        Cwd = cwd
    };

    // [4] AlwaysAllow fast path
    if (AlwaysAllowSession.Contains(command)) { /* audit + skip modal */ }
    else
    {
        // [5] No-UI guard
        if (System.Windows.Application.Current == null) { /* audit + return deny */ }

        // [6] Modal hop — _modalLock + Dispatcher.InvokeAsync (Pattern 3 of RESEARCH.md)
        await _modalLock.WaitAsync().ConfigureAwait(false);
        try
        {
            (allowed, alwaysAllow) = await System.Windows.Application.Current.Dispatcher.InvokeAsync<(bool, bool)>(() =>
            {
                var win = new CommandConfirmationWindow(ctx) { Owner = System.Windows.Application.Current.MainWindow };
                bool result = win.ShowDialog() == true;
                return (result && win.IsAllowed, win.AlwaysAllow);
            }).Task;
        }
        finally { _modalLock.Release(); }

        if (!allowed) { /* audit "deny" */ return "Comando recusado pelo usuário"; }
        if (alwaysAllow) { AlwaysAllowSession.Add(command); /* audit "always_allow" */ }
        else { /* audit "allow" */ }
    }

    // [7] Denylist gate — Phase 3 D-04 replaces ApplyDenylist with CommandFloorList
    var settings = new SettingsService().LoadSettings();
    if (userLevel < 9 && settings.ConfirmDangerousCommands)
    {
        string? deny = ApplyDenylist(command.ToLowerInvariant(), userLevel);   // OLD — DELETE
        if (deny != null) return deny;
    }

    return await CommandService.ExecuteAsync(command, cwd);
}
```

**How to apply (D-04):**

1. **Before** building the context (insert between line 329 `string cwd = ...` and line 332 `var ctx = new CommandConfirmationContext`), call `CommandFloorList.Match`:
   ```csharp
   // D-04: pre-compute floor verdict so the modal can render the AVISO banner.
   var (floorHit, floorReason) = CommandFloorList.Match(command, userLevel);
   ```

2. Populate the new `CommandConfirmationContext` fields:
   ```csharp
   var ctx = new CommandConfirmationContext
   {
       Tool = "run_command",
       Command = command,
       Level = userLevel,
       Cwd = cwd,
       DenylistHit = floorHit,                  // D-04 new
       DenylistReason = floorReason,            // D-04 new
       // ScriptBody / Interpreter / ContentHash stay null for run_command
   };
   ```

3. **Re-key AlwaysAllow** per D-10:
   ```csharp
   var allowKey = (ctx.Tool, ctx.Command, (string?)null);  // run_command has no ContentHash
   if (AlwaysAllowSession.Contains(allowKey))
   { ... }
   else { ... }
   // and: AlwaysAllowSession.Add(allowKey);
   ```

4. **DELETE the entire `ApplyDenylist` method** at `NativeTools.cs:413-448` AND the `ContainsWord` helper at `:311-319`.

5. **Replace the `if (userLevel < 9 && settings.ConfirmDangerousCommands)` gate** at `:399-403` with the floor-after-modal block per D-04:
   ```csharp
   // D-04: post-modal floor refuse at L<7 (the gate of D-01).
   //   - userLevel >= 7  → CommandFloorList.Match already returned (false, null) so floorHit is false here.
   //   - userLevel <  7  ∩ ConfirmDangerousCommands == OFF  → settings gate below short-circuits.
   //   - userLevel <  7  ∩ ConfirmDangerousCommands == ON   → if the floor hit, audit and refuse now.
   var settings = new SettingsService().LoadSettings();
   if (floorHit && settings.ConfirmDangerousCommands)
   {
       _ = AuditLogService.AppendAsync(BuildEntry(ctx, "allow_then_floor_deny", false));
       return floorReason!;
   }

   return await CommandService.ExecuteAsync(command, cwd);
   ```

6. **Extend `BuildEntry`** to carry the new `content_hash` field (default null for `run_command`):
   ```csharp
   private static object BuildEntry(CommandConfirmationContext ctx, string outcome, bool alwaysAllow) => new
   {
       ts = DateTime.UtcNow.ToString("o"),
       tool = ctx.Tool,
       cmd = ctx.Command,
       level = ctx.Level,
       cwd = ctx.Cwd,
       outcome,
       always_allow = alwaysAllow,
       content_hash = ctx.ContentHash,   // D-10 new — null for run_command, hex string for skills
   };
   ```
   `System.Text.Json.JsonSerializer` emits `"content_hash":null` for null values by default (Phase 1 D6 schema absorbs this without an explicit `JsonIgnoreCondition`).

7. **Lift `_modalLock` out of `RunCommandTool`** into `CommandConfirmationWindow.ShowAsync` (see file #8 below). After lifting, `RunCommandTool.ExecuteAsync` calls the helper instead of holding its own semaphore:
   ```csharp
   if (System.Windows.Application.Current == null)
   {
       _ = AuditLogService.AppendAsync(BuildEntry(ctx, "deny_no_ui", false));
       return "ACESSO NEGADO: interface de confirmação indisponível.";
   }

   var (allowed, alwaysAllow) = await CommandConfirmationWindow.ShowAsync(ctx);
   ```
   The `private static readonly SemaphoreSlim _modalLock` on line 288 is **deleted** from `RunCommandTool` (it moves to the helper).

---

### 6) `AIBWindows/Services/NativeTools.cs` — `ExecuteSkillTool.ExecuteAsync` modal hop (D-08, D-09, D-10)

**Analog:** `RunCommandTool.ExecuteAsync:322-407` — Phase 1 modal flow. Same shape; the only differences are:
- Read script body + compute SHA256 BEFORE the modal (D-09).
- Skip the modal entirely for `Interpreter == "markdown"` skills (D-09).
- `RequiredLevel` raised from 1 to 6 (D-11 / SEC-06).
- No floor-list check (skill content is fully visible to the user via ScriptBody preview).

**Current body to replace** (`NativeTools.cs:568-575`):
```csharp
public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
{
    string skillName = ToolArgParser.Get(argumentsJson, "skill_name");
    string args = ToolArgParser.Get(argumentsJson, "arguments");
    if (string.IsNullOrWhiteSpace(skillName)) return "ERRO: 'skill_name' é obrigatório.";

    return await SkillService.RunSkillAsync(skillName, args);
}
```

**Replace with full modal-hop block** (mirrors `RunCommandTool.ExecuteAsync:322-407` structure):
```csharp
public int RequiredLevel => 6;   // was 1 — SEC-06 / D-11

public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
{
    string skillName = ToolArgParser.Get(argumentsJson, "skill_name");
    string args = ToolArgParser.Get(argumentsJson, "arguments");
    if (string.IsNullOrWhiteSpace(skillName)) return "ERRO: 'skill_name' é obrigatório.";

    var skill = SkillService.ListLocalSkills()
        .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
    if (skill == null) return $"Erro: Skill '{skillName}' não encontrada.";

    // D-09: markdown-only skills don't execute a process — bypass the modal.
    if (skill.Interpreter == "markdown")
        return await SkillService.RunSkillAsync(skillName, args);

    // D-09: read body + SHA256 (cap 50KB)
    var (body, hash) = ReadAndHashSkillFile(skill.ScriptFile);

    var ctx = new CommandConfirmationContext
    {
        Tool = "execute_skill",
        Command = $"{skill.Interpreter} {skill.Name} {args}",
        Level = userLevel,
        Cwd = Path.GetDirectoryName(skill.ScriptFile) ?? "",
        ScriptBody = body,                  // D-08 new
        Interpreter = skill.Interpreter,    // D-08 new
        ContentHash = hash,                 // D-08 new
        // DenylistHit / DenylistReason stay default-false/null — skills don't consult the floor.
    };

    var allowKey = (ctx.Tool, ctx.Command, ctx.ContentHash);

    // AlwaysAllow fast path — D-10 tuple key with ContentHash
    if (AlwaysAllowSession.Contains(allowKey))
    {
        _ = AuditLogService.AppendAsync(BuildEntry(ctx, "always_allow", true));
    }
    else
    {
        if (System.Windows.Application.Current == null)
        {
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "deny_no_ui", false));
            return "ACESSO NEGADO: interface de confirmação indisponível.";
        }

        var (allowed, alwaysAllow) = await CommandConfirmationWindow.ShowAsync(ctx);

        if (!allowed)
        {
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "deny", false));
            return "Comando recusado pelo usuário";
        }
        if (alwaysAllow)
        {
            AlwaysAllowSession.Add(allowKey);
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "always_allow", true));
        }
        else
        {
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "allow", false));
        }
    }

    return await SkillService.RunSkillAsync(skillName, args);
}

// D-09 helper — read once + tail-truncate at 50KB + SHA256 of full bytes.
// SHA256 is over the FULL file bytes (not the truncated body) so silent edits
// past the 50KB cap still invalidate the AlwaysAllow tuple.
private static (string? Body, string? Hash) ReadAndHashSkillFile(string scriptPath)
{
    if (!System.IO.File.Exists(scriptPath)) return (null, null);
    var bytes = System.IO.File.ReadAllBytes(scriptPath);
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    var body = System.Text.Encoding.UTF8.GetString(bytes);
    const int Cap = 50_000;
    if (body.Length > Cap) body = body.Substring(0, Cap) + "\n\n[... SCRIPT TRUNCATED]";
    return (body, hash);
}
```

**Placement of `BuildEntry`:** since Phase 1 placed it on `RunCommandTool` as a private static method (`NativeTools.cs:451-460`), the cleanest fix is to **promote it** to a top-level internal static helper inside the namespace OR copy it as a private static on each skill tool. RESEARCH.md doesn't lock this; planner discretion. Recommended: extract to `internal static class ConfirmationAuditEntry { public static object Build(...) => new { ... }; }` so all three tools share it without duplication.

---

### 7) `AIBWindows/Services/NativeTools.cs` — `MaterializeSkillTool.ExecuteAsync` modal hop (D-08, D-10)

**Analog:** `ExecuteSkillTool.ExecuteAsync` (Phase 3 modified — file #6 above). Same modal-hop block, with two differences:
- `ScriptBody = content` (the LLM-supplied `script_content` argument; no disk read because the file doesn't exist yet — CONTEXT.md `canonical_refs` block).
- `ContentHash = SHA256(UTF8 bytes of content)` — same algorithm as `ExecuteSkillTool`, applied to the LLM's content directly.
- `RequiredLevel` raised from 5 to 8 (D-11 / SEC-06).

**Current body to replace** (`NativeTools.cs:602-611`):
```csharp
public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
{
    string name = ToolArgParser.Get(argumentsJson, "skill_name");
    string content = ToolArgParser.Get(argumentsJson, "script_content");
    string interp = ToolArgParser.Get(argumentsJson, "interpreter");
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
        return "ERRO: 'skill_name' e 'script_content' são obrigatórios.";
    if (string.IsNullOrWhiteSpace(interp)) interp = "powershell";
    return await SkillService.MaterializeSkillAsync(name, content, interp);
}
```

**Replace with:**
```csharp
public int RequiredLevel => 8;   // was 5 — SEC-06 / D-11

public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
{
    string name    = ToolArgParser.Get(argumentsJson, "skill_name");
    string content = ToolArgParser.Get(argumentsJson, "script_content");
    string interp  = ToolArgParser.Get(argumentsJson, "interpreter");
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
        return "ERRO: 'skill_name' e 'script_content' são obrigatórios.";
    if (string.IsNullOrWhiteSpace(interp)) interp = "powershell";

    // D-08: hash the LLM-supplied body directly (no disk read — file doesn't exist yet).
    string ext = interp == "python" ? "py" : "ps1";
    string destPath = Path.Combine(DirectoryService.DataDir, "skills", name, $"{name}.{ext}");
    string contentHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    // D-08 cap preview at 50KB.
    string body = content;
    const int Cap = 50_000;
    if (body.Length > Cap) body = body.Substring(0, Cap) + "\n\n[... SCRIPT TRUNCATED]";

    var ctx = new CommandConfirmationContext
    {
        Tool = "materialize_skill",
        Command = $"{interp} -> {destPath}",   // shows destination path so the user sees where it lands
        Level = userLevel,
        Cwd = Path.GetDirectoryName(destPath) ?? "",
        ScriptBody = body,
        Interpreter = interp,
        ContentHash = contentHash,
    };

    var allowKey = (ctx.Tool, ctx.Command, ctx.ContentHash);

    if (AlwaysAllowSession.Contains(allowKey))
    {
        _ = AuditLogService.AppendAsync(BuildEntry(ctx, "always_allow", true));
    }
    else
    {
        if (System.Windows.Application.Current == null)
        {
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "deny_no_ui", false));
            return "ACESSO NEGADO: interface de confirmação indisponível.";
        }

        var (allowed, alwaysAllow) = await CommandConfirmationWindow.ShowAsync(ctx);

        if (!allowed)
        {
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "deny", false));
            return "Comando recusado pelo usuário";
        }
        if (alwaysAllow)
        {
            AlwaysAllowSession.Add(allowKey);
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "always_allow", true));
        }
        else
        {
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "allow", false));
        }
    }

    return await SkillService.MaterializeSkillAsync(name, content, interp);
}
```

**Note:** the `Command` field on the context is what shows in the modal under the existing "A AIB solicitou..." label. Putting the destination path here surfaces the write-target visibly (closes RESEARCH.md Open Question #1).

---

### 8) `AIBWindows/Services/CommandConfirmationContext.cs` — add 5 new init-only properties (D-04, D-08)

**Analog:** itself — the 4 existing `{ get; init; }` properties (`CommandConfirmationContext.cs:13-16`).

**Current file:**
```csharp
namespace AIB.Services;

public class CommandConfirmationContext
{
    public string Tool { get; init; } = "";
    public string Command { get; init; } = "";
    public int Level { get; init; } = 0;
    public string Cwd { get; init; } = "";
}
```

**Add 5 new init-only properties** (D-04 + D-08, all nullable/default-false so existing call sites keep working):
```csharp
namespace AIB.Services;

public class CommandConfirmationContext
{
    public string Tool { get; init; } = "";
    public string Command { get; init; } = "";
    public int Level { get; init; } = 0;
    public string Cwd { get; init; } = "";

    // D-04: floor-list precheck verdict (run_command only).
    public bool DenylistHit { get; init; } = false;
    public string? DenylistReason { get; init; }

    // D-08: skill-tool preview fields. Null for run_command.
    public string? ScriptBody { get; init; }
    public string? Interpreter { get; init; }
    public string? ContentHash { get; init; }   // SHA256 hex string; also keys AlwaysAllow (D-10)
}
```

**Convention notes (copy from PATTERNS.md Phase 1):**
- File-scoped namespace, no `using` line needed.
- `{ get; init; }` for every property (consistent with the existing 4).
- Default-empty / default-false / nullable so existing `RunCommandTool` ctor block (`NativeTools.cs:332-338`) compiles without explicitly setting the new fields. The compiler emits `null` for unset reference-type init props and `false` for `bool`.
- XML `///` summaries on each new property documenting which decision they implement (D-04 / D-08) — matches the existing class-level summary style.

---

### 9) `AIBWindows/Services/AlwaysAllowSession.cs` — re-key `HashSet<string>` → `HashSet<(string, string, string?)>` (D-10)

**Analog:** itself — current public API at `AlwaysAllowSession.cs:15-33`.

**Current file:**
```csharp
public static class AlwaysAllowSession
{
    private static readonly HashSet<string> _allowed = new();

    public static bool Contains(string command) { lock (_allowed) return _allowed.Contains(command); }
    public static void Add(string command)      { lock (_allowed) _allowed.Add(command); }
    public static void Clear()                  { lock (_allowed) _allowed.Clear(); }
}
```

**Replace with tuple-keyed API** (D-10 — `(Tool, Cmd, ContentHash?)`):
```csharp
using System.Collections.Generic;

namespace AIB.Services;

/// <summary>
/// Allowlist em memória de aprovações "Sempre permitir" desta sessão.
///
/// Chave: tupla <c>(Tool, Cmd, ContentHash?)</c> (D-10 Phase 3).
///   - <c>Tool</c>: nome da ferramenta (run_command / execute_skill / materialize_skill).
///   - <c>Cmd</c>: texto do comando (run_command) ou
///                 <c>"{interpreter} {skill_name} {args}"</c> (execute_skill) ou
///                 <c>"{interpreter} -> {destPath}"</c> (materialize_skill).
///   - <c>ContentHash</c>: <c>null</c> para run_command; SHA256 hex do body para skills.
///     Mudança de body em disco → tupla nova → modal re-dispara.
///
/// Sessão única — sem persistência (Phase 1 D2 preservado). Lifetime = processo.
/// </summary>
public static class AlwaysAllowSession
{
    private static readonly HashSet<(string Tool, string Cmd, string? ContentHash)> _allowed = new();

    public static bool Contains((string Tool, string Cmd, string? ContentHash) key)
    {
        lock (_allowed) return _allowed.Contains(key);
    }

    public static void Add((string Tool, string Cmd, string? ContentHash) key)
    {
        lock (_allowed) _allowed.Add(key);
    }

    public static void Clear()
    {
        lock (_allowed) _allowed.Clear();
    }
}
```

**Call site updates** (all in `NativeTools.cs`):
- `RunCommandTool` lines 342 + 385: pass `(ctx.Tool, ctx.Command, (string?)null)` instead of `command`.
- `ExecuteSkillTool` (new modal block, file #6 above): pass `(ctx.Tool, ctx.Command, ctx.ContentHash)`.
- `MaterializeSkillTool` (new modal block, file #7 above): same as `ExecuteSkillTool`.

**Note:** `ValueTuple<string, string, string?>` has built-in `Equals`/`GetHashCode` deep on each field — `HashSet<>` works correctly with no custom `IEqualityComparer`. Default ordinal string comparison matches Phase 1 D2's "exact match" requirement.

---

### 10) `AIBWindows/Views/CommandConfirmationWindow.xaml` — add ScriptBody row + amber DenylistHit banner (D-04, D-08)

**Analog:** itself — current XAML at `CommandConfirmationWindow.xaml`. Phase 1 already structured it as a `Grid` with `RowDefinitions` (lines 29-33) and uses the `WarningAccent` LinearGradientBrush (lines 18-22) for the ⚠ glyph + Permitir button.

**Pattern to extend** — current `StackPanel Grid.Row="1"` content block (lines 40-55):
```xml
<StackPanel Grid.Row="1">
    <TextBlock x:Name="ToolText" Text="Tool: run_command" Foreground="#AAA" Margin="0,0,0,4" FontSize="12"/>
    <TextBlock x:Name="LevelText" Text="Nível: 1/9" Foreground="#AAA" Margin="0,0,0,8" FontSize="12"/>

    <TextBlock Text="A AIB solicitou a execução do seguinte comando:" Foreground="#AAA" Margin="0,0,0,5"/>
    <Border Background="#1AFFFFFF" Padding="10" CornerRadius="6" BorderBrush="#2A2A30" BorderThickness="1">
        <TextBlock x:Name="CommandText" Text="git status" Foreground="White" TextWrapping="Wrap" FontFamily="Consolas" FontSize="13"/>
    </Border>

    <TextBlock Text="CWD:" Foreground="#AAA" Margin="0,10,0,4" FontSize="12"/>
    <Border Background="#1AFFFFFF" Padding="6" CornerRadius="4" BorderBrush="#2A2A30" BorderThickness="1">
        <TextBlock x:Name="CwdText" Text="" Foreground="White" TextWrapping="Wrap" FontFamily="Consolas" FontSize="12"/>
    </Border>

    <CheckBox x:Name="AlwaysAllowCheckBox" Content="Sempre permitir este comando/ação" Foreground="#888" Margin="0,15,0,0" FontSize="12"/>
</StackPanel>
```

**Add two new blocks inside the same `StackPanel Grid.Row="1"`:**

1. **Amber AVISO banner** (D-04, between the `CommandText` Border and the `CWD:` label):
   ```xml
   <Border x:Name="DenylistBanner"
           Background="{StaticResource WarningAccent}"
           Padding="8" CornerRadius="6"
           Margin="0,10,0,0"
           Visibility="Collapsed">
       <TextBlock x:Name="DenylistText"
                  Foreground="#1A1A1A" FontWeight="Bold" FontSize="12"
                  TextWrapping="Wrap"/>
   </Border>
   ```
   - Reuses the existing `WarningAccent` LinearGradientBrush (lines 18-22) — no new palette entries per VISUAL.MD.
   - Visibility defaults to `Collapsed`; the code-behind ctor sets it to `Visible` when `ctx.DenylistHit == true`.

2. **ScriptBody scroll-viewer row** (D-08, after the `CwdText` Border, BEFORE the `AlwaysAllowCheckBox`):
   ```xml
   <TextBlock x:Name="ScriptBodyLabel"
              Text="Conteúdo do script:" Foreground="#AAA" Margin="0,10,0,4" FontSize="12"
              Visibility="Collapsed"/>
   <ScrollViewer x:Name="ScriptBodyScroller"
                 MaxHeight="200"
                 VerticalScrollBarVisibility="Auto"
                 HorizontalScrollBarVisibility="Auto"
                 Visibility="Collapsed">
       <Border Background="#1AFFFFFF" Padding="8" CornerRadius="4"
               BorderBrush="#2A2A30" BorderThickness="1">
           <TextBox x:Name="ScriptBodyText"
                    FontFamily="Consolas" FontSize="12"
                    Foreground="White" Background="Transparent"
                    BorderThickness="0"
                    IsReadOnly="True"
                    AcceptsReturn="True"
                    TextWrapping="NoWrap"/>
       </Border>
   </ScrollViewer>
   ```
   - `MaxHeight=200` per D-08.
   - Read-only `TextBox` (not `TextBlock`) so the user can select + copy the text — matches typical script-preview UX. `IsReadOnly="True"` keeps it visually a label.
   - `FontFamily="Consolas" FontSize="12"` matches the `CommandText` font choice (line 46).
   - Visibility defaults to `Collapsed`; code-behind sets to `Visible` when `ctx.ScriptBody != null`.

3. **Header label flip** (D-08): the `TextBlock Text="CONFIRMAÇÃO DE COMANDO"` at line 37 needs an `x:Name` so the code-behind can rewrite it to `"CONFIRMAÇÃO DE SCRIPT ({Interpreter})"` when ScriptBody is present:
   ```xml
   <TextBlock x:Name="HeaderText"
              Text="CONFIRMAÇÃO DE COMANDO" FontSize="16" FontWeight="Bold"
              Foreground="White" VerticalAlignment="Center"/>
   ```

4. **Window height** — per CONTEXT.md Claude's Discretion: change `Height="380"` to `MinHeight="380" SizeToContent="Height"` on the `<Window>` root so the window auto-sizes when ScriptBody is present but stays 380 minimum when not. **Width stays 450** (no change).

---

### 11) `AIBWindows/Views/CommandConfirmationWindow.xaml.cs` — extend ctor + add static `ShowAsync` helper (D-04, D-08)

**Analog (ctor):** itself — current ctor at `CommandConfirmationWindow.xaml.cs:12-19`.
**Analog (helper body):** `NativeTools.cs:288, 362-375` — the `_modalLock` semaphore + the `Dispatcher.InvokeAsync` body inside `RunCommandTool.ExecuteAsync` are lifted verbatim into the new helper.

**Current ctor:**
```csharp
public CommandConfirmationWindow(CommandConfirmationContext ctx)
{
    InitializeComponent();
    CommandText.Text = ctx.Command;
    ToolText.Text = $"Tool: {ctx.Tool}";
    LevelText.Text = $"Nível: {ctx.Level}/9";
    CwdText.Text = ctx.Cwd;
}
```

**Extend with D-04 + D-08 handlers + the new `ShowAsync` static helper:**
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AIB.Services;

namespace AIB.Views;

public partial class CommandConfirmationWindow : Window
{
    // D-08: hoisted from RunCommandTool — single semaphore across run_command +
    // execute_skill + materialize_skill so the ReAct loop can dispatch tools in
    // parallel without two ShowDialog calls racing for the UI thread.
    private static readonly SemaphoreSlim _modalLock = new(1, 1);

    public bool IsAllowed { get; private set; } = false;
    public bool AlwaysAllow { get; private set; } = false;

    public CommandConfirmationWindow(CommandConfirmationContext ctx)
    {
        InitializeComponent();
        CommandText.Text = ctx.Command;
        ToolText.Text = $"Tool: {ctx.Tool}";
        LevelText.Text = $"Nível: {ctx.Level}/9";
        CwdText.Text = ctx.Cwd;

        // D-04: amber AVISO banner under CommandText.
        if (ctx.DenylistHit && !string.IsNullOrEmpty(ctx.DenylistReason))
        {
            DenylistText.Text = $"AVISO: este comando será recusado pelo floor list após aprovação (nível {ctx.Level}). Razão: {ctx.DenylistReason}";
            DenylistBanner.Visibility = Visibility.Visible;
        }

        // D-08: script body preview + header label flip.
        if (!string.IsNullOrEmpty(ctx.ScriptBody))
        {
            ScriptBodyText.Text = ctx.ScriptBody;
            ScriptBodyLabel.Visibility = Visibility.Visible;
            ScriptBodyScroller.Visibility = Visibility.Visible;
            HeaderText.Text = $"CONFIRMAÇÃO DE SCRIPT ({ctx.Interpreter ?? "?"})";
        }
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        IsAllowed = true;
        AlwaysAllow = AlwaysAllowCheckBox.IsChecked ?? false;
        DialogResult = true;
        Close();
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        IsAllowed = false;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Helper compartilhado (D-08) que faz o hop para a UI thread, mostra o modal,
    /// e retorna a decisão do usuário. Usado por RunCommandTool, ExecuteSkillTool
    /// e MaterializeSkillTool. Reentrancy-guarded pelo <c>_modalLock</c>.
    ///
    /// O caller é responsável pelo No-UI guard (<c>Application.Current == null</c>)
    /// e pela atualização do <see cref="AlwaysAllowSession"/> + audit log.
    /// </summary>
    public static async Task<(bool Allowed, bool AlwaysAllow)> ShowAsync(CommandConfirmationContext ctx)
    {
        // No-UI guard — caller should also check this BEFORE calling for cleaner
        // audit semantics (caller emits "deny_no_ui" with full context).
        if (Application.Current == null) return (false, false);

        await _modalLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Application.Current.Dispatcher.InvokeAsync<(bool, bool)>(() =>
            {
                var win = new CommandConfirmationWindow(ctx) { Owner = Application.Current.MainWindow };
                bool result = win.ShowDialog() == true;
                return (result && win.IsAllowed, win.AlwaysAllow);
            }).Task;
        }
        finally
        {
            _modalLock.Release();
        }
    }
}
```

**Critical correctness note (RESEARCH.md Pitfall #6):** keep `_modalLock.WaitAsync().ConfigureAwait(false)`. Do **not** change to `ConfigureAwait(true)`. The await capture would re-enter the UI thread synchronously and deadlock when the tool is called from a non-UI synchronization context.

---

### 12) `AIBWindows/Services/AuditLogService.cs` — NO STRUCTURAL CHANGE; caller-side only (D-10)

**Analog:** itself — `AuditLogService.AppendAsync(object entry)` at `AuditLogService.cs:34-61`.

**Why no structural change:** the method already accepts `object` and serializes via `JsonSerializer.Serialize(entry)`. The `BuildEntry` helper that constructs the anonymous object lives on `RunCommandTool` (`NativeTools.cs:451-460`). To add `content_hash`, modify `BuildEntry` (see file #5, step 6 above), not `AuditLogService`.

The output JSONL line shape becomes (run_command example):
```json
{"ts":"2026-05-31T14:32:00.000Z","tool":"run_command","cmd":"dir C:\\","level":2,"cwd":"C:\\Users\\Carlo\\.AIB","outcome":"allow","always_allow":false,"content_hash":null}
```

And for skill tools:
```json
{"ts":"2026-05-31T14:33:11.245Z","tool":"execute_skill","cmd":"python consultar_cep 01001-000","level":6,"cwd":"C:\\Users\\Carlo\\.AIB\\.default_skills\\consultar_cep","outcome":"always_allow","always_allow":true,"content_hash":"A1B2C3...64hex"}
```

The new `allow_then_floor_deny` outcome (D-04) just slots into the existing `outcome` field — no schema migration needed; existing audit log readers see it as an opaque string.

---

### 13) `Regras de Identidade/SEGURANCA.MD` — rewrite denylist + skill + quoting paragraphs (NFR-03)

**Analog:** Phase 2's NFR-03 rewrite cadence — present-tense documentation that describes the SHIPPED behavior (not historical promises). Phase 2 set the pattern by rewriting the API-key paragraphs after key-rotation/env-hardening landed.

**Sections to rewrite:**
1. **Denylist paragraph** → describe the hybrid floor:
   - Modal is the unconditional first gate at every level.
   - Floor list runs ONLY at `userLevel < 7` and `ConfirmDangerousCommands == ON`.
   - Floor list is best-effort; the canonical defense is the modal showing the command + (for skills) the script body.
   - Four categories enumerated (recursive deletion, format/partition, shutdown, registry destructive ops).
   - `-EncodedCommand` outright refused at L<7.

2. **Skill execution paragraph** → describe:
   - `execute_skill` requires Level 6; `materialize_skill` requires Level 8.
   - Both route through `CommandConfirmationWindow` showing the script body (50KB cap).
   - AlwaysAllow is keyed by `(Tool, Cmd, ContentHash?)`; silent disk edits invalidate the cache.
   - Markdown-only skills skip the modal (no process execution).

3. **Argument quoting paragraph** → describe:
   - All tool-internal exec uses `ProcessStartInfo.ArgumentList` (no shell metacharacters).
   - `run_command` keeps `cmd.exe /c {raw_string}` post-modal because the user explicitly typed and approved the command (carve-out).
   - `InstallFromOnlineAsync` regex-validates `installArg` before any `npx.cmd` shell-out.

**Style guide:** present tense, declarative. No "we will", no "TODO". Match Phase 2's commit pattern (`docs(security): rewrite ... per Phase X`).

---

## Shared Patterns

### Pattern A: Modal-hop body (lifted from Phase 1 RunCommandTool)

**Source:** `NativeTools.cs:288, 362-375` — Phase 1's modal-hop body inside `RunCommandTool.ExecuteAsync`.
**Lifted into:** `CommandConfirmationWindow.ShowAsync` (static method on the window class).
**Applies to:** `RunCommandTool`, `ExecuteSkillTool`, `MaterializeSkillTool`.

**Shape (unchanged from Phase 1, just hoisted):**
```csharp
private static readonly SemaphoreSlim _modalLock = new(1, 1);

public static async Task<(bool Allowed, bool AlwaysAllow)> ShowAsync(CommandConfirmationContext ctx)
{
    if (Application.Current == null) return (false, false);
    await _modalLock.WaitAsync().ConfigureAwait(false);
    try
    {
        return await Application.Current.Dispatcher.InvokeAsync<(bool, bool)>(() =>
        {
            var win = new CommandConfirmationWindow(ctx) { Owner = Application.Current.MainWindow };
            bool result = win.ShowDialog() == true;
            return (result && win.IsAllowed, win.AlwaysAllow);
        }).Task;
    }
    finally
    {
        _modalLock.Release();
    }
}
```

**Why single semaphore across all three tools:** ReAct loop dispatches tools via `Task.WhenAll`. Per-tool semaphores would let `execute_skill` and `materialize_skill` open two modals simultaneously. One semaphore on the **window class** serializes all three.

---

### Pattern B: Audit-log entry construction

**Source:** `NativeTools.cs:451-460` — Phase 1 `BuildEntry` (currently a private method on `RunCommandTool`).
**Applies to:** All three modal-bearing tools after Phase 3 (`RunCommandTool`, `ExecuteSkillTool`, `MaterializeSkillTool`).

**Shape (Phase 3-extended):**
```csharp
private static object BuildEntry(CommandConfirmationContext ctx, string outcome, bool alwaysAllow) => new
{
    ts = DateTime.UtcNow.ToString("o"),
    tool = ctx.Tool,
    cmd = ctx.Command,
    level = ctx.Level,
    cwd = ctx.Cwd,
    outcome,
    always_allow = alwaysAllow,
    content_hash = ctx.ContentHash,   // D-10: null for run_command, hex for skills
};
```

**Recommended refactor (planner discretion):** promote to `internal static class ConfirmationAuditEntry { public static object Build(...) }` so all three tools reference it via one symbol. Alternative: copy verbatim into each tool's class — works but invites schema drift. Promotion is cleaner; matches CODIGO_LIMPO.MD "one helper per cross-cutting concern".

**Audit outcome enum (string constants):**
- `"allow"` (Phase 1) — user approved this turn only.
- `"always_allow"` (Phase 1) — user approved + checked Sempre permitir.
- `"deny"` (Phase 1) — user clicked Recusar.
- `"deny_no_ui"` (Phase 1) — `Application.Current == null` guard fired.
- `"allow_then_floor_deny"` (Phase 3 NEW per D-04) — user clicked Allow but floor list refused post-modal.

---

### Pattern C: Static singleton with lock-guarded `HashSet<>`

**Source:** `AlwaysAllowSession.cs:15-33` (Phase 1).
**Applies to:** the re-keyed `AlwaysAllowSession` (Phase 3 — file #9 above).

**Invariant preserved:** `lock (_allowed) { return _allowed.Contains(key); }` and `lock (_allowed) { _allowed.Add(key); }`. The lock object is the collection itself — matches Phase 1 D2 + the existing pattern.

---

### Pattern D: PT-BR error string prefix taxonomy

**Source:** `NativeTools.cs:325-353, 417-444` — Phase 1 + pre-Phase-1 used `"ACESSO NEGADO (SANDBOX):"` for denylist refusals and `"ERRO:"` for argument validation errors.
**Applies to:**
- `CommandFloorList` reason strings → `"ACESSO NEGADO (FLOOR):"` prefix (substitute `SANDBOX → FLOOR` per CONTEXT.md Claude's Discretion).
- `ExecuteSkillTool` / `MaterializeSkillTool` no-UI guards → `"ACESSO NEGADO: interface de confirmação indisponível."` (same string as `RunCommandTool` line 353).
- `SkillService.RunSkillAsync` interpreter-not-supported → `"Erro: interpretador '{x}' não suportado."` (matches existing wording style at `SkillService.cs:201`).
- `SkillService.InstallFromOnlineAsync` regex reject → `"Erro: formato de skill inválido (esperado: owner/repo[@version])."`.

**Do NOT introduce new prefix families.** All Phase 3 user-visible strings reuse `"ACESSO NEGADO ..."` / `"ACESSO NEGADO (FLOOR):"` / `"Erro: ..."` / `"ERRO: ..."` — no new taxonomy.

---

### Pattern E: Level-check denial format (REFERENCE ONLY — no change)

**Source:** `ToolRegistry.cs:36-38`:
```csharp
if (tool.RequiredLevel > userLevel)
    return $"ACESSO NEGADO: A ferramenta '{toolName}' exige Nível {tool.RequiredLevel}, mas o seu nível atual é {userLevel}.";
```

**Applies to:** `execute_skill` at L<6 and `materialize_skill` at L<8 — both get this exact string automatically because `ToolRegistry.ExecuteToolAsync` is the dispatcher and reads `tool.RequiredLevel`. The tool's `ExecuteAsync` is never invoked when level is insufficient. **No code change needed** — just bump `RequiredLevel` to 6 / 8.

**UAT expectation (SEC-06 acceptance):** at L1, `execute_skill` returns `"ACESSO NEGADO: A ferramenta 'execute_skill' exige Nível 6, mas o seu nível atual é 1."` — matches the requirement phrasing in CONTEXT.md.

---

### Pattern F: Fire-and-forget audit append

**Source:** `NativeTools.cs:344, 352, 379, 386, 390` — Phase 1 caller idiom.
**Applies to:** every audit point in all three modal-bearing tools.

**Shape:**
```csharp
_ = AuditLogService.AppendAsync(BuildEntry(ctx, "outcome", alwaysAllow));
```

The `_ =` discard suppresses the CS4014 "not awaited" warning. `AuditLogService.AppendAsync` catches all exceptions internally and logs to `[AUDIT]` console tag — never throws.

---

### Pattern G: File-scoped namespace + ImplicitUsings (project convention)

**Source:** `AIBWindows/AIB.csproj:7` — `<ImplicitUsings>enable</ImplicitUsings>`.
**Applies to:** every new or modified `.cs` file in Phase 3.

**Implication:** `using System;`, `using System.Threading;`, `using System.Collections.Generic;` are implicit and need NOT be explicit. The only `using` lines required are project-specific (e.g. `using AIB.Services;`) or those outside the global usings set (`using System.Text.RegularExpressions;` for the floor list, `using System.Security.Cryptography;` for SHA256 in `NativeTools.cs`).

**File-scoped namespace style:** `namespace AIB.Services;` at top (matches Phase 1's new files; CONVENTIONS.md flags `ReminderService.cs`, `ChatHistoryService.cs`, `ContextService.cs` as legacy block-namespace holdouts — Phase 3 sticks with file-scoped throughout).

---

## No Analog Found

| Item | Why no analog |
|------|---------------|
| `CommandFloorList.Match` normalize-then-regex pipeline (steps 1-5 of D-03) | Phase 1's `ContainsWord` does a single regex pass with `\b` boundaries but lacks lowercase / concat-strip / alias-expand pre-processing. This is the one genuinely new code pattern in Phase 3. No codebase analog; use RESEARCH.md §"Pattern 2" example as the canonical shape. |
| `npx.cmd` → `npx` two-step `Process.Start` fallback (Pitfall #2) | `Process.Start` is only invoked from `CommandService` in the codebase, and `cmd.exe` (which honors PATHEXT) is always the file name today. The `npx.cmd` shim convention is a Windows-specific quirk with no prior pattern in this project. |
| `SHA256.HashData(bytes)` for skill content-hash key (D-09) | No crypto / hashing code exists anywhere in `AIBWindows/Services/`. Use the BCL one-liner per RESEARCH.md Pattern 4. |
| `ProcessStartInfo.ArgumentList.Add(...)` (D-05) | The codebase has never used `ArgumentList`; every `Process.Start` to date passes `Arguments = "/c ..."`. RESEARCH.md verifies this is the BatBadBut-safe Microsoft-recommended path. |

---

## Metadata

**Analog search scope:** `AIBWindows/Services/`, `AIBWindows/Views/`, `AIBWindows/App.xaml.cs`, `.planning/phases/01_modal-and-level9/`.
**Files read for pattern extraction:**
- `AIBWindows/Services/CommandService.cs` (full)
- `AIBWindows/Services/SkillService.cs` (full)
- `AIBWindows/Services/NativeTools.cs` (RunCommandTool §282-461; ExecuteSkillTool §549-576; MaterializeSkillTool §582-612; ManageClipboardTool §618-660; imports §1-15)
- `AIBWindows/Services/CommandConfirmationContext.cs` (full)
- `AIBWindows/Services/AlwaysAllowSession.cs` (full)
- `AIBWindows/Services/AuditLogService.cs` (full)
- `AIBWindows/Services/ToolRegistry.cs` (full)
- `AIBWindows/Services/DynamicSkillTool.cs` (full — deletion target)
- `AIBWindows/Services/SettingsService.cs` (`ConfirmDangerousCommands` field §48)
- `AIBWindows/Views/CommandConfirmationWindow.xaml` (full)
- `AIBWindows/Views/CommandConfirmationWindow.xaml.cs` (full)
- `.planning/phases/01_modal-and-level9/PATTERNS.md` (full — analog cross-reference)
- `.planning/phases/03-.../03-CONTEXT.md` (full — locked decisions D-01..D-12)
- `.planning/phases/03-.../03-RESEARCH.md` (full — Architecture Patterns, Code Examples, Common Pitfalls, Validation Architecture, Security Domain, Project Constraints)

**Files NOT read (intentionally):** `OpenAIService.cs`, `MemoryService.cs`, `VoiceService.cs`, `OcrService.cs`, `ShadowAssistantService.cs`, `ChatWindow.xaml.cs`, `App.xaml.cs` — none touch the Phase 3 surface; Phase 1's PATTERNS.md already validated their irrelevance to the modal surface and Phase 3 strictly extends Phase 1.

**Pattern extraction date:** 2026-05-31
