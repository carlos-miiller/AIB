using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services;

/// <summary>
/// Encapsula um script local (Python/PowerShell) como uma ferramenta de primeiro nível
/// do LLM, eliminando o aninhamento JSON do antigo sistema run_skill().
/// O LLM chama a skill diretamente pelo nome, com parâmetros limpos.
/// </summary>
public class DynamicSkillTool : ITool
{
    private readonly SkillMetadata _metadata;

    public DynamicSkillTool(SkillMetadata metadata)
    {
        _metadata = metadata;
    }

    public string Name => SanitizeName(_metadata.Name);

    public string Description => string.IsNullOrWhiteSpace(_metadata.Description)
        ? $"Executa a skill local '{_metadata.Name}'."
        : _metadata.Description;

    public int RequiredLevel => 9;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name,
        Description,
        BinaryData.FromString(BuildSchema()));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            // Extrai argumentos do JSON fornecido pelo LLM
            string args = ParseArgs(argumentsJson);

            // Verifica se o script ainda existe
            if (!File.Exists(_metadata.ScriptFile))
                return $"ERRO: Script da skill '{_metadata.Name}' não encontrado em: {_metadata.ScriptFile}";

            string command = _metadata.Interpreter.ToLower() switch
            {
                "python"     => $"py \"{_metadata.ScriptFile}\" {args}",
                "powershell" => $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{_metadata.ScriptFile}\" {args}",
                "cmd"        => $"cmd.exe /c \"{_metadata.ScriptFile}\" {args}",
                _            => throw new Exception($"Interpretador '{_metadata.Interpreter}' não suportado.")
            };

            Console.WriteLine($"[SKILL] Executando: {command}");
            return await CommandService.ExecuteAsync(command, Path.GetDirectoryName(_metadata.ScriptFile));
        }
        catch (Exception ex)
        {
            return $"ERRO na skill '{_metadata.Name}': {ex.Message}";
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Converte o nome da skill para snake_case limpo e compatível com nomes de função.
    /// Garante que não haja espaços, hífens ou caracteres inválidos.
    /// </summary>
    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown_skill";
        return name.ToLower()
                   .Replace(" ", "_")
                   .Replace("-", "_")
                   .Replace(".", "_");
    }

    /// <summary>
    /// Constrói um schema JSON simples e genérico para a skill.
    /// O LLM enviará um objeto "arguments" com qualquer string de parâmetros.
    /// </summary>
    private static string BuildSchema() =>
        """
        {
          "type": "object",
          "properties": {
            "arguments": {
              "type": "string",
              "description": "Argumentos de linha de comando para o script (pode ser string vazia se nenhum argumento for necessário)."
            }
          },
          "required": []
        }
        """;

    /// <summary>
    /// Extrai a propriedade "arguments" do JSON, ou retorna a string bruta se o parse falhar.
    /// </summary>
    private static string ParseArgs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("arguments", out var val))
            {
                string? argsStr = val.GetString();
                return string.IsNullOrWhiteSpace(argsStr) ? string.Empty : argsStr.Replace("\n", " ").Replace("\r", "");
            }
        }
        catch { /* ignora, retorna vazio */ }
        return string.Empty;
    }
}
