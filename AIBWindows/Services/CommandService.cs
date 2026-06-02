using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace AIB.Services;

public static class CommandService
{
    /// <summary>
    /// Executa um comando arbitrário através de cmd.exe /c. Carve-out SEC-04: a string
    /// é interpolada na linha de comando do cmd.exe (metacaracteres incluídos), pois o
    /// chamador é o run_command pós-modal — o humano já viu e aprovou o comando literal
    /// no CommandConfirmationWindow. NÃO usar para argv fornecido por LLM sem modal.
    /// </summary>
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
            return await RunProcessAsync(process, timeoutMs);
        }
        catch (Exception ex)
        {
            return $"ERRO ao executar comando: {ex.Message}";
        }
    }

    /// <summary>
    /// Executa um processo passando os argumentos via ProcessStartInfo.ArgumentList — cada
    /// elemento de <paramref name="args"/> chega ao processo filho como um argv literal
    /// (CommandLineToArgvW round-trip via PasteArguments). Sem cmd.exe na cadeia, portanto
    /// metacaracteres (";", "&amp;", "|", "$", etc.) não são interpretados. Usado por
    /// SkillService.RunSkillAsync e InstallFromOnlineAsync para argv vindo do LLM (D-05/D-06).
    /// </summary>
    public static async Task<string> ExecuteWithArgListAsync(string fileName, IEnumerable<string> args, string? workDir = null, int timeoutMs = 20000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = string.IsNullOrWhiteSpace(workDir) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : workDir
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

    /// <summary>
    /// Helper compartilhado: faz read-pump assíncrono dos stdout/stderr, aplica timeout
    /// com kill-tree, normaliza erros, vazio e trunca em 50KB. O chamador é dono do
    /// <see cref="Process"/> (using var) — este helper NÃO descarta o processo.
    /// </summary>
    private static async Task<string> RunProcessAsync(Process process, int timeoutMs)
    {
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
}
