using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

public class WriteFileTool : ITool
{
    public string Name => Ferramentas.Gravar;
    public string Description => "Cria ou sobrescreve um arquivo com o texto fornecido. Sempre use caminhos absolutos.";
    public int RequiredLevel => 2;

    public bool RequiresConfirmation => true;

    /// <summary>
    /// Gravar dentro de uma pasta dispensada não para no card. Fora dela, o card aparece como
    /// sempre. Ver <see cref="PastasSemConfirmacao"/>.
    /// </summary>
    public bool DispensaConfirmacao(string argumentsJson)
        => PastasSemConfirmacao.Dispensa(Caminho(argumentsJson));

    private static string? Caminho(string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (!args.TryGetProperty("path", out var pathEl)) return null;

            string caminho = PathArgumentRepair.Normalize(pathEl.GetString());
            return string.IsNullOrWhiteSpace(caminho) ? null : caminho;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// O cartão mostra o caminho ABSOLUTO já resolvido, e não o que o modelo escreveu: é a
    /// diferença entre autorizar "config.json" e autorizar a gravação real em
    /// %APPDATA%\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\config.json.
    /// Fora das pastas sem confirmação, o caminho resolvido é o que o usuário tem para decidir.
    /// </summary>
    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (!args.TryGetProperty("path", out var pathEl)) return null;

            string caminho = PathArgumentRepair.Normalize(pathEl.GetString());
            if (string.IsNullOrWhiteSpace(caminho)) return null;

            string resolvido;
            try { resolvido = Path.GetFullPath(caminho); }
            catch { return null; }

            bool existe = File.Exists(resolvido);
            string conteudo = args.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
            string previa = conteudo.Length > 400 ? conteudo[..400] + "\n...[prévia truncada]" : conteudo;

            return new CommandConfirmationContext
            {
                Tool = Name,
                Command = (existe ? "SOBRESCREVER " : "CRIAR ") + resolvido,
                Level = userLevel,
                Cwd = Environment.CurrentDirectory,
                ScriptBody = previa
            };
        }
        catch (JsonException)
        {
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
                "path": {
                    "type": "string",
                    "description": "O caminho completo e absoluto do arquivo a ser criado."
                },
                "content": {
                    "type": "string",
                    "description": "O conteúdo que será salvo no arquivo."
                }
            },
            "required": ["path", "content"]
        }
        """)
    );

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (!args.TryGetProperty("path", out var pathElement) || !args.TryGetProperty("content", out var contentElement))
                return "ERRO: Os parâmetros 'path' e 'content' são obrigatórios.";

            // Só o caminho passa pelo reparo. O conteúdo NUNCA: ali uma tabulação ou quebra
            // de linha de verdade é legítima, e reescrevê-la corromperia o arquivo.
            string path = PathArgumentRepair.Normalize(pathElement.GetString(), out bool pathRepaired);
            string content = contentElement.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path))
                return "ERRO: O caminho não pode estar vazio.";

            Console.WriteLine($"[TOOL: write] Escrevendo em: {path}");

            // Garante que o diretório exista
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(path, content);

            // Avisa o modelo do reparo para que ele corrija o escape na próxima chamada, em
            // vez de repetir o erro a cada arquivo.
            string aviso = pathRepaired
                ? " AVISO: o caminho recebido continha escapes JSON inválidos e foi corrigido. Em JSON, escreva a barra invertida duplicada (C:\\temp\\x.txt)."
                : "";

            return $"SUCESSO: Arquivo salvo corretamente em '{path}'.{aviso}";
        }
        catch (Exception ex)
        {
            return $"ERRO ao escrever arquivo: {ex.Message}";
        }
    }
}
