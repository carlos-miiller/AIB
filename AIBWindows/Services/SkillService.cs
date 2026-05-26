using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

            string result = await CommandService.ExecuteAsync($"cmd /c call npx -y skills add {installArg} --yes", workPath, 900000);
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
