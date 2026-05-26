using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services;

/// <summary>
/// Registro central das ferramentas nativas C# do agente AIB.
/// As skills dinâmicas não são mais registradas aqui individualmente para evitar overhead no LLM.
/// Em vez disso, o LLM usa a ferramenta 'execute_skill' para chamá-las sob demanda (Lazy Loading).
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nativeToolNames = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry()
    {
        RegisterNativeTools();
    }

    public List<ChatTool> GetActiveTools(int userLevel)
        => _tools.Values.Where(t => t.RequiredLevel <= userLevel).Select(t => t.ChatToolDefinition).ToList();

    public (List<ITool> Natives, List<ITool> Dynamics) GetCategorizedTools()
    {
        return (_tools.Values.ToList(), new List<ITool>());
    }

    public async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, int userLevel)
    {
        if (_tools.TryGetValue(toolName, out var tool))
        {
            if (tool.RequiredLevel > userLevel)
                return $"ACESSO NEGADO: A ferramenta '{toolName}' exige Nível {tool.RequiredLevel}, mas o seu nível atual é {userLevel}.";

            Console.WriteLine($"[REGISTRY] Executando: {toolName}({(argumentsJson.Length > 100 ? argumentsJson[..100] + "..." : argumentsJson)})");
            try
            {
                return await tool.ExecuteAsync(argumentsJson, userLevel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[REGISTRY] ERRO em '{toolName}': {ex.Message}");
                return $"ERRO ao executar '{toolName}': {ex.Message}";
            }
        }

        Console.WriteLine($"[REGISTRY] AVISO: Ferramenta desconhecida '{toolName}'.");
        return $"ERRO: Ferramenta '{toolName}' não encontrada no registry. Ferramentas disponíveis: {string.Join(", ", _tools.Keys)}.";
    }

    public void Refresh()
    {
        // No Lazy Loading, não registramos skills dinâmicas no registry.
        Console.WriteLine($"[REGISTRY] Refresh completo. Total de ferramentas nativas: {_tools.Count}");
    }

    public bool Contains(string toolName) => _tools.ContainsKey(toolName);

    private void RegisterNativeTools()
    {
        var nativeTools = new List<ITool>
        {
            new ManageMemoryTool(),
            new ManageVaultTool(),
            new ReadFileTool(),
            new RunCommandTool(),
            new SearchWebTool(),
            new ReadScreenTool(),
            new MaterializeSkillTool(),
            new ManageClipboardTool(),
            new SetReminderTool(),
            new ExecuteSkillTool()
        };

        foreach (var tool in nativeTools)
        {
            _tools[tool.Name] = tool;
            _nativeToolNames.Add(tool.Name);
            Console.WriteLine($"[REGISTRY] Ferramenta nativa registrada: '{tool.Name}'");
        }
    }
}
