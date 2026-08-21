using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

public class ReadFileTool : ITool
{
    public string Name => "read_file";
    public string Description => "Lê o conteúdo de um arquivo de texto. Use caminhos absolutos preferencialmente.";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "O caminho completo e absoluto do arquivo a ser lido."
                }
            },
            "required": ["path"]
        }
        """)
    );

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (!args.TryGetProperty("path", out var pathElement))
                return "ERRO: O parâmetro 'path' é obrigatório.";

            // Mesmo reparo do write_file: caractere de controle é ilegal em caminho do Windows,
            // então sua presença só pode vir de um escape JSON mal emitido pelo modelo.
            string path = PathArgumentRepair.Normalize(pathElement.GetString());
            if (string.IsNullOrWhiteSpace(path))
                return "ERRO: O caminho do arquivo não pode estar vazio.";

            if (!File.Exists(path))
                return $"ERRO: Arquivo não encontrado em '{path}'.";

            Console.WriteLine($"[TOOL: read_file] Lendo: {path}");
            
            // Usar StreamReader para ler de forma assíncrona
            using var reader = new StreamReader(path);
            string content = await reader.ReadToEndAsync();

            if (content.Length > 12000)
            {
                return content.Substring(0, 12000) + "\n...[Arquivo muito grande, conteúdo truncado no final].";
            }

            return content;
        }
        catch (Exception ex)
        {
            return $"ERRO ao ler arquivo: {ex.Message}";
        }
    }
}
