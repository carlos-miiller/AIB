using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

public class RunCommandTool : ITool
{
    public string Name => "run_command";
    public string Description => "Executa um comando no PowerShell do Windows do usuário. Use para investigar o sistema, rodar scripts ou compilar código. Use 'pwd' ou Get-Location se precisar saber o diretório atual.";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "command": {
                    "type": "string",
                    "description": "O comando PowerShell exato a ser executado."
                }
            },
            "required": ["command"]
        }
        """)
    );

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (!args.TryGetProperty("command", out var cmdElement))
                return "ERRO: O parâmetro 'command' é obrigatório.";

            string command = cmdElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
                return "ERRO: O comando não pode estar vazio.";

            Console.WriteLine($"[TOOL: run_command] Executando: {command}");

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Timeout de segurança (30s)
            var timeoutTask = Task.Delay(30000);
            var processTask = Task.Run(() =>
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return (output, error);
            });

            var completedTask = await Task.WhenAny(processTask, timeoutTask);
            if (completedTask == timeoutTask)
            {
                process.Kill();
                return "ERRO: O comando demorou mais de 30 segundos e foi interrompido (Timeout).";
            }

            var result = await processTask;
            string finalOutput = result.output + "\n" + result.error;
            
            if (string.IsNullOrWhiteSpace(finalOutput))
                return "Comando executado com sucesso (sem saída).";

            if (finalOutput.Length > 8000)
                finalOutput = finalOutput.Substring(0, 8000) + "\n...[Saída truncada devido ao tamanho máximo].";

            return finalOutput.Trim();
        }
        catch (Exception ex)
        {
            return $"ERRO fatal ao executar comando: {ex.Message}";
        }
    }
}
