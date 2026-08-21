using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

public class RunCommandTool : ITool
{
    public string Name => "run_command";
    public string Description => "Executa um comando no PowerShell do Windows do usuário. Use para investigar o sistema, rodar scripts ou compilar código. Use 'pwd' ou Get-Location se precisar saber o diretório atual.";
    public int RequiredLevel => 2;

    public bool RequiresConfirmation => true;

    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (!args.TryGetProperty("command", out var cmd)) return null;

            string comando = cmd.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(comando)) return null;

            var (bateu, razao) = CommandFloorList.Match(comando, userLevel);

            return new CommandConfirmationContext
            {
                Tool = Name,
                Command = comando,
                Level = userLevel,
                Cwd = Environment.CurrentDirectory,
                DenylistHit = bateu,
                DenylistReason = razao ?? ""
            };
        }
        catch (JsonException)
        {
            // JSON ilegível não vira autorização: sem descrever a operação, não há o que aprovar.
            return null;
        }
    }

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

            // -EncodedCommand (Base64 UTF-16LE) elimina o problema de quoting inteiro. O escape
            // anterior era command.Replace("\"", "\\\""), e a barra invertida não é o caractere
            // de escape do PowerShell (é a crase) — qualquer comando terminado em separador de
            // caminho do Windows era corrompido antes de rodar.
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Os dois pipes são lidos EM PARALELO. Ler stdout até o fim e só depois stderr
            // trava assim que o filho enche o buffer do stderr (~4KB): ele bloqueia escrevendo,
            // nós bloqueamos lendo o outro pipe, e o usuário via a mentira "demorou mais de
            // 30 segundos" num comando que nunca teve chance de terminar.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
            }

            if (timedOut)
            {
                // entireProcessTree: matar só o powershell.exe deixava netos rodando sem
                // supervisão e a task leitora pendurada nos handles do processo.
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(); } catch { }
                await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(2000));
                return "ERRO: O comando demorou mais de 30 segundos e foi interrompido (Timeout).";
            }

            string finalOutput = await stdoutTask + "\n" + await stderrTask;
            
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
