using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIB.Services;

public class SkillMetadata
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ScriptFile { get; set; } = ""; 
    public string Interpreter { get; set; } = "python"; 
    public List<string> Dependencies { get; set; } = new();
}

public static class SkillService
{
    private static string SkillsDir => Path.Combine(DirectoryService.DataDir, "skills");
    private static string DefaultSkillsDir => Path.Combine(DirectoryService.DataDir, ".default_skills");

    /// <summary>
    /// D-07: single source of truth for the interpreter dispatch table. Maps each
    /// supported interpreter alias to its executable + the leading switches that must
    /// precede the script path. Consumed by <see cref="RunSkillAsync"/>. Case-insensitive
    /// keys so the LLM may emit "Python", "PYTHON", "powershell", etc.
    /// </summary>
    private static readonly Dictionary<string, (string FileName, string[] Switches)> InterpreterMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"]     = ("py.exe", Array.Empty<string>()),
        ["powershell"] = ("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" }),
        ["cmd"]        = ("cmd.exe", new[] { "/c" })
    };

    public static void EnsureDir()
    {
        if (!Directory.Exists(SkillsDir))
            Directory.CreateDirectory(SkillsDir);

        if (!Directory.Exists(DefaultSkillsDir))
        {
            Directory.CreateDirectory(DefaultSkillsDir);
            foreach (var skill in DefaultSkills.Skills)
            {
                try
                {
                    string skillPath = Path.Combine(DefaultSkillsDir, skill.Name);
                    Directory.CreateDirectory(skillPath);
                    string scriptContent = DefaultSkills.GetScriptContent(skill.Name);
                    
                    File.WriteAllText(Path.Combine(skillPath, "skill.json"), JsonSerializer.Serialize(skill));
                    File.WriteAllText(Path.Combine(skillPath, skill.ScriptFile), scriptContent);
                }
                catch { }
            }
        }
    }

    public static List<SkillMetadata> ListLocalSkills()
    {
        EnsureDir();
        var skills = new List<SkillMetadata>();
        
        var dirsToScan = new List<string>();
        if (Directory.Exists(DefaultSkillsDir)) dirsToScan.AddRange(Directory.GetDirectories(DefaultSkillsDir));
        if (Directory.Exists(SkillsDir)) dirsToScan.AddRange(Directory.GetDirectories(SkillsDir));

        // Evita duplicatas se o usuário tiver uma skill com o mesmo nome que a default (sobrescreve com a do usuário)
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Ordem inversa: processa as do usuário primeiro, assim a do usuário tem precedência
        var allSubDirs = new List<string>();
        if (Directory.Exists(SkillsDir)) allSubDirs.AddRange(Directory.GetDirectories(SkillsDir));
        if (Directory.Exists(DefaultSkillsDir)) allSubDirs.AddRange(Directory.GetDirectories(DefaultSkillsDir));

        foreach (var dir in allSubDirs)
        {
            string skillName = Path.GetFileName(dir);
            if (seenNames.Contains(skillName)) continue; // Já carregou a versão prioritária

            string jsonPath = Path.Combine(dir, "skill.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    var json = File.ReadAllText(jsonPath);
                    var meta = JsonSerializer.Deserialize<SkillMetadata>(json);
                    if (meta != null) {
                        if (!Path.IsPathRooted(meta.ScriptFile)) meta.ScriptFile = Path.Combine(dir, meta.ScriptFile);
                        skills.Add(meta);
                        seenNames.Add(skillName);
                    }
                    continue;
                }
                catch { }
            }

            var markdownFiles = Directory.GetFiles(dir, "SKILL.md", SearchOption.AllDirectories);
            foreach (var file in markdownFiles)
            {
                try
                {
                    string content = File.ReadAllText(file);
                    var meta = ParseMarkdownSkill(content, file);
                    if (meta != null && !seenNames.Contains(meta.Name))
                    {
                        skills.Add(meta);
                        seenNames.Add(meta.Name);
                    }
                }
                catch { }
            }
        }
        return skills;
    }

    private static SkillMetadata? ParseMarkdownSkill(string content, string filePath)
    {
        var lines = content.Split('\n');
        if (lines.Length < 3 || !lines[0].Trim().Equals("---")) return null;

        var meta = new SkillMetadata { Interpreter = "markdown", ScriptFile = filePath };
        string skillDir = Path.GetDirectoryName(filePath) ?? "";

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Equals("---")) break;
            var parts = lines[i].Split(':', 2);
            if (parts.Length == 2)
            {
                string key = parts[0].Trim().ToLower();
                string value = parts[1].Trim();
                if (key == "name") meta.Name = value;
                else if (key == "description") meta.Description = value;
                else if (key == "interpreter") meta.Interpreter = value;
                else if (key == "script_file") meta.ScriptFile = Path.Combine(skillDir, value);
            }
        }
        return string.IsNullOrEmpty(meta.Name) ? null : meta;
    }

    public static async Task<string> InstallSkillAsync(string name, string description, string scriptContent, string interpreter = "python")
    {
        try
        {
            EnsureDir();
            string skillPath = Path.Combine(SkillsDir, name);
            if (!Directory.Exists(skillPath)) Directory.CreateDirectory(skillPath);

            var meta = new SkillMetadata { Name = name, Description = description, Interpreter = interpreter, ScriptFile = "main." + (interpreter == "python" ? "py" : "ps1") };
            await File.WriteAllTextAsync(Path.Combine(skillPath, "skill.json"), JsonSerializer.Serialize(meta));
            await File.WriteAllTextAsync(Path.Combine(skillPath, meta.ScriptFile), scriptContent);
            return $"SUCESSO: Skill '{name}' instalada.";
        }
        catch (Exception ex) { return $"Erro: {ex.Message}"; }
    }

    /// <summary>
    /// Instala uma skill a partir de owner/repo[@version] via npx.
    ///
    /// IMPORTANTE: este método NÃO tem callers no Phase 3 — é dead code defensivo.
    /// Callers futuros DEVEM gatear esta chamada atrás de:
    ///   1. RequiredLevel >= 7 no ITool exposto;
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
                var urlParts = url.Split("skills.sh/", StringSplitOptions.RemoveEmptyEntries)[1].Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (urlParts.Length >= 3) installArg = $"{urlParts[0]}/{urlParts[1]}@{urlParts[2]}";
            }

            // D-11: regex BEFORE any shell-out — load-bearing defense vs the npx.cmd shim
            // re-parsing cmd metacharacters (ArgumentList does NOT escape & | ^ < > %).
            if (!Regex.IsMatch(installArg, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$"))
                return "Erro: formato de skill inválido (esperado: owner/repo[@version]).";

            // D-11: ArgumentList path (no cmd /c call interpolation). npx no Windows é
            // um shim .cmd — CreateProcess não honra PATHEXT, então Process.Start("npx", ...)
            // retorna ERROR_FILE_NOT_FOUND. Tenta "npx.cmd" primeiro; se faltar, cai
            // para "npx" (npm < 7 e variantes que registram o nome curto no PATH).
            // Pitfall 2 (RESEARCH.md) — CommandService.ExecuteWithArgListAsync rethrows
            // Win32Exception com NativeErrorCode == 2 (locked Option A patch em
            // CommandService.cs) para permitir esta lógica de fallback.
            var npxArgs = new[] { "-y", "skills", "add", installArg, "--yes" };
            string result;
            try
            {
                result = await CommandService.ExecuteWithArgListAsync(
                    "npx.cmd", npxArgs, workPath, 900_000);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
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

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (FileInfo file in new DirectoryInfo(sourceDir).GetFiles()) file.CopyTo(Path.Combine(destinationDir, file.Name), true);
        foreach (DirectoryInfo subDir in new DirectoryInfo(sourceDir).GetDirectories()) CopyDirectory(subDir.FullName, Path.Combine(destinationDir, subDir.Name));
    }

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
        // The \n/\r/\0 strip is defense-in-depth against weird argv display
        // in logs, NOT a security control.
        string safeArgs = (arguments ?? "").Replace("\n", " ").Replace("\r", "").Replace("\0", "");

        var args = new List<string>(pair.Switches) { skill.ScriptFile, safeArgs };
        return await CommandService.ExecuteWithArgListAsync(
            pair.FileName, args, Path.GetDirectoryName(skill.ScriptFile));
    }

    public static async Task<string> MaterializeSkillAsync(string skillName, string scriptContent, string interpreter)
    {
        try
        {
            EnsureDir();
            string skillPath = Path.Combine(SkillsDir, skillName);
            if (!Directory.Exists(skillPath)) Directory.CreateDirectory(skillPath);

            string ext = interpreter == "python" ? "py" : "ps1";
            string scriptFileName = $"{skillName}.{ext}";
            string scriptFullPath = Path.Combine(skillPath, scriptFileName);

            await File.WriteAllTextAsync(scriptFullPath, scriptContent);

            string mdPath = Path.Combine(skillPath, "SKILL.md");
            if (File.Exists(mdPath))
            {
                string mdContent = await File.ReadAllTextAsync(mdPath);
                if (mdContent.StartsWith("---"))
                {
                    var endOfFrontmatter = mdContent.IndexOf("---", 3);
                    if (endOfFrontmatter > 0)
                    {
                        string header = mdContent.Substring(0, endOfFrontmatter);
                        string body = mdContent.Substring(endOfFrontmatter);
                        if (!header.Contains("interpreter:")) header += $"interpreter: {interpreter}\n";
                        if (!header.Contains("script_file:")) header += $"script_file: {scriptFileName}\n";
                        await File.WriteAllTextAsync(mdPath, header + "---" + body.Substring(3));
                    }
                }
            }
            return $"SUCESSO: A habilidade '{skillName}' foi materializada como um script {interpreter}.";
        }
        catch (Exception ex) { return $"Erro: {ex.Message}"; }
    }
}
