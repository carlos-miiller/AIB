using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

public class WriteFileTool : ITool
{
    public string Name => "write_file";
    public string Description => "Cria ou sobrescreve um arquivo com o texto fornecido. Sempre use caminhos absolutos.";
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

            string path = pathElement.GetString() ?? string.Empty;
            string content = contentElement.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path))
                return "ERRO: O caminho não pode estar vazio.";

            Console.WriteLine($"[TOOL: write_file] Escrevendo em: {path}");

            // Garante que o diretório exista
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(path, content);
            return $"SUCESSO: Arquivo salvo corretamente em '{path}'.";
        }
        catch (Exception ex)
        {
            return $"ERRO ao escrever arquivo: {ex.Message}";
        }
    }
}
