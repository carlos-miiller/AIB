using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services;

/// <summary>
/// Registro central e dinâmico de todas as ferramentas do agente AIB.
/// 
/// Responsabilidades:
/// - Pré-carrega todas as ferramentas nativas C# na inicialização.
/// - Varre a pasta de skills locais e registra cada script como DynamicSkillTool.
/// - Resolve conflitos de nomes: ferramentas nativas têm prioridade absoluta.
/// - Fornece a lista de ChatTool para o LLM e executa ferramentas por nome.
/// </summary>
public class ToolRegistry
{
    // Dicionário principal: nome_da_tool → instância ITool
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    // Conjunto de nomes nativos protegidos (não podem ser sobrescritos por skills dinâmicas)
    private readonly HashSet<string> _nativeToolNames = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry()
    {
        RegisterNativeTools();
        ScanAndRegisterDynamicSkills();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API Pública
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Retorna a lista de ChatTool para ser enviada ao LLM em cada chamada, filtrando pelo nível do usuário.
    /// </summary>
    public List<ChatTool> GetActiveTools(int userLevel)
        => _tools.Values.Where(t => t.RequiredLevel <= userLevel).Select(t => t.ChatToolDefinition).ToList();

    /// <summary>
    /// Retorna as ferramentas separadas em categorias para exibição na UI.
    /// </summary>
    public (List<ITool> Natives, List<ITool> Dynamics) GetCategorizedTools()
    {
        var natives = new List<ITool>();
        var dynamics = new List<ITool>();
        foreach (var kvp in _tools)
        {
            if (_nativeToolNames.Contains(kvp.Key)) natives.Add(kvp.Value);
            else dynamics.Add(kvp.Value);
        }
        return (natives, dynamics);
    }

    /// <summary>
    /// Executa uma ferramenta pelo nome com os argumentos fornecidos pelo LLM.
    /// Retorna uma mensagem de erro estruturada se a ferramenta não for encontrada.
    /// </summary>
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

    /// <summary>
    /// Re-escaneia a pasta de skills e atualiza o registry com novos scripts.
    /// Chamado após materializar uma nova skill para que ela fique disponível imediatamente.
    /// </summary>
    public void Refresh()
    {
        // Remove apenas as skills dinâmicas
        var dynamicKeys = _tools
            .Where(kv => !_nativeToolNames.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in dynamicKeys)
            _tools.Remove(key);

        ScanAndRegisterDynamicSkills();
        Console.WriteLine($"[REGISTRY] Refresh completo. Total de ferramentas: {_tools.Count}");
    }

    /// <summary>
    /// Verifica se uma ferramenta com o nome dado está registrada.
    /// </summary>
    public bool Contains(string toolName) => _tools.ContainsKey(toolName);

    // ─────────────────────────────────────────────────────────────────────────
    // Registro de Ferramentas Nativas
    // ─────────────────────────────────────────────────────────────────────────

    private void RegisterNativeTools()
    {
        var nativeTools = new List<ITool>
        {
            new RememberTool(),
            new RecallTool(),
            new StoreCredentialTool(),
            new RetrieveCredentialTool(),
            new ReadFileTool(),
            new RunCommandTool(),
            new SearchWebTool(),
            new OcrScreenTool(),
            new CaptureScreenTool(),
            new MaterializeSkillTool(),
            new ClipboardReadTool(),
            new ClipboardWriteTool(),
            new ActiveWindowTool(),
            new SetReminderTool(),
        };

        foreach (var tool in nativeTools)
        {
            _tools[tool.Name] = tool;
            _nativeToolNames.Add(tool.Name);
            Console.WriteLine($"[REGISTRY] Ferramenta nativa registrada: '{tool.Name}'");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Varredura e Registro de Skills Dinâmicas
    // ─────────────────────────────────────────────────────────────────────────

    private void ScanAndRegisterDynamicSkills()
    {
        var skills = SkillService.ListLocalSkills();
        int registered = 0;
        int skipped = 0;

        foreach (var skill in skills)
        {
            // Ignora skills do tipo Markdown (são apenas documentação, não executáveis)
            if (skill.Interpreter.Equals("markdown", StringComparison.OrdinalIgnoreCase))
                continue;

            var dynamicTool = new DynamicSkillTool(skill);

            // Proteção de nomes nativos: nunca sobrescreve ferramentas nativas
            if (_nativeToolNames.Contains(dynamicTool.Name))
            {
                Console.WriteLine($"[REGISTRY] CONFLITO: Skill '{skill.Name}' ignorada. Nome '{dynamicTool.Name}' é reservado para ferramenta nativa.");
                skipped++;
                continue;
            }

            _tools[dynamicTool.Name] = dynamicTool;
            Console.WriteLine($"[REGISTRY] Skill dinâmica registrada: '{dynamicTool.Name}' ({skill.Interpreter})");
            registered++;
        }

        Console.WriteLine($"[REGISTRY] Skills dinâmicas: {registered} registradas, {skipped} ignoradas (conflito de nome).");
    }
}
