using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIB.Services;

public static class CommandService
{
    private static readonly HashSet<string> SafePrefixes = new()
    {
        "dir", "ls", "git status", "git log", "git branch", "git diff",
        "dotnet --version", "python --version", "node --version",
        "whoami", "hostname", "ipconfig", "systeminfo", "type", "cat", "echo"
    };

    public static bool IsSafeCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        string lowerCmd = cmd.Trim().ToLower();
        return SafePrefixes.Any(prefix => lowerCmd.StartsWith(prefix));
    }

    public static async Task<string> ExecuteAsync(string command, string? workDir = null, int timeoutMs = 20000)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = string.IsNullOrWhiteSpace(workDir) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : workDir
            };

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
        }
        catch (Exception ex)
        {
            return $"ERRO ao executar comando: {ex.Message}";
        }
    }
}
