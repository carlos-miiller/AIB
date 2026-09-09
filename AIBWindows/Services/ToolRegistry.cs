using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenAI.Chat;
using AIB.Services.Tools;

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
    private readonly IConfirmationPrompt? _confirmationPrompt;
    private readonly SettingsService? _settingsService;

    /// <param name="confirmationPrompt">
    /// Portão humano. Quando null, TODA ferramenta que exige confirmação é recusada — sem
    /// alguém para autorizar, o padrão seguro é não executar. É o que mantém honestos os
    /// cenários sem UI (testes, execução headless).
    /// </param>
    public ToolRegistry(
        IConfirmationPrompt? confirmationPrompt = null,
        SettingsService? settingsService = null)
    {
        _confirmationPrompt = confirmationPrompt;
        _settingsService = settingsService;
        RegisterNativeTools();
    }

    public List<ChatTool> GetActiveTools(int userLevel)
        => _tools.Values.Where(t => t.RequiredLevel <= userLevel).Select(t => t.ChatToolDefinition).ToList();

    public (List<ITool> Natives, List<ITool> Dynamics) GetCategorizedTools()
    {
        return (_tools.Values.ToList(), new List<ITool>());
    }

    /// <param name="aoEsperarHumano">
    /// Recebe os milissegundos que a ferramenta passou parada no modal, esperando o usuário
    /// decidir. Sem isto o tempo de gente vira tempo de máquina: um <c>Get-Content</c> trivial
    /// apareceu no log como "ok em 7299,6s" porque ninguém tinha clicado em autorizar por duas
    /// horas.
    /// </param>
    public async Task<string> ExecuteToolAsync(
        string toolName, string argumentsJson, int userLevel, Action<long>? aoEsperarHumano = null)
    {
        if (_tools.TryGetValue(toolName, out var tool))
        {
            if (tool.RequiredLevel > userLevel)
                return $"ACESSO NEGADO: A ferramenta '{toolName}' exige Nível {tool.RequiredLevel}, mas o seu nível atual é {userLevel}.";

            if (tool.RequiresConfirmation)
            {
                var (autorizado, motivo) = await AuthorizeAsync(tool, argumentsJson, userLevel, aoEsperarHumano);
                if (!autorizado) return motivo!;
            }

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
    /// Portão de autorização, na ordem documentada em SEGURANCA.MD:
    /// modal primeiro (autoridade canônica, dispara em qualquer nível), floor list depois
    /// (best-effort, só refuta abaixo do Nível 7 e com ConfirmDangerousCommands ligado).
    /// A auditoria grava ANTES da execução, em todos os desfechos.
    /// </summary>
    private async Task<(bool Autorizado, string? Motivo)> AuthorizeAsync(
        ITool tool, string argumentsJson, int userLevel, Action<long>? aoEsperarHumano = null)
    {
        var ctx = tool.BuildConfirmationContext(argumentsJson, userLevel);
        if (ctx == null)
        {
            await AuditLogService.AppendAsync(new { evento = "deny_sem_contexto", ferramenta = tool.Name, userLevel });
            return (false, $"ACESSO NEGADO: '{tool.Name}' exige confirmação, mas não foi possível descrever a operação para autorizar.");
        }

        string comando = ctx.Command ?? "";
        var chave = (tool.Name, comando, (string?)null);

        // "Sempre permitir" vale só nesta sessão e casa byte a byte no comando exato.
        if (AlwaysAllowSession.Contains(chave))
        {
            await AuditLogService.AppendAsync(new { evento = "allow_sessao", ferramenta = tool.Name, comando, userLevel });
        }
        else
        {
            if (_confirmationPrompt == null)
            {
                await AuditLogService.AppendAsync(new { evento = "deny_sem_ui", ferramenta = tool.Name, comando, userLevel });
                return (false, $"ACESSO NEGADO: '{tool.Name}' exige confirmação do usuário e não há interface disponível para pedi-la.");
            }

            Console.WriteLine($"[REGISTRY] {tool.Name}: esperando você autorizar...");

            var cronometro = System.Diagnostics.Stopwatch.StartNew();
            var (permitido, sempre) = await _confirmationPrompt.AskAsync(ctx);
            cronometro.Stop();

            aoEsperarHumano?.Invoke(cronometro.ElapsedMilliseconds);

            Console.WriteLine($"[REGISTRY] {tool.Name}: {(permitido ? "autorizado" : "recusado")} " +
                              $"depois de {cronometro.Elapsed.TotalSeconds:0.0}s de espera sua.");

            await AuditLogService.AppendAsync(new
            {
                evento = permitido ? "allow_usuario" : "deny_usuario",
                ferramenta = tool.Name,
                comando,
                userLevel,
                semprePermitir = sempre
            });

            if (!permitido) return (false, "Ação Rejeitada pelo Usuário.");
            if (sempre) AlwaysAllowSession.Add(chave);
        }

        // Floor list roda DEPOIS do modal: mesmo autorizado, categorias destrutivas exigem
        // Nível 7. Em L>=7 ou com a flag desligada, o modal é a autoridade única.
        bool floorLigado = _settingsService?.LoadSettings().ConfirmDangerousCommands ?? true;
        if (floorLigado)
        {
            var (bateu, razao) = CommandFloorList.Match(comando, userLevel);
            if (bateu)
            {
                await AuditLogService.AppendAsync(new { evento = "deny_floor", ferramenta = tool.Name, comando, userLevel, razao });
                return (false, razao!);
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Reavalia as skills em disco. Chamado quando uma skill nasce durante a conversa: sem
    /// isto, a habilidade recem-instalada so existiria na proxima abertura do app.
    /// </summary>
    public void Refresh()
    {
        AtualizarFerramentaDeSkills();
        Console.WriteLine($"[REGISTRY] Refresh completo. Total de ferramentas: {_tools.Count}");
    }

    public bool Contains(string toolName) => _tools.ContainsKey(toolName);

    private void RegisterNativeTools()
    {
        var nativeTools = new List<ITool>
        {
            new ReadFileTool(),
            new RunCommandTool(),
            new WriteFileTool(),

            // Registrada SEMPRE, e não só quando a triagem está ligada. Com ela fora, o modelo
            // não sabe que a pergunta tem resposta possível e chuta — e chutar sobre a caixa de
            // entrada de alguém é o pior desfecho. Desligada, ela responde exatamente isso.
            new ConsultarEmailsTool(_settingsService)
        };

        foreach (var tool in nativeTools)
        {
            _tools[tool.Name] = tool;
            _nativeToolNames.Add(tool.Name);
            Console.WriteLine($"[REGISTRY] Ferramenta nativa registrada: '{tool.Name}'");
        }

        AtualizarFerramentaDeSkills();
    }

    /// <summary>
    /// A execute_skill so existe quando ha skill instalada.
    /// <para>
    /// O schema de toda ferramenta registrada e reenviado ao modelo em CADA requisicao. Numa
    /// instalacao sem skills, deixa-la registrada seria pagar ~80 tokens por turno, para
    /// sempre, por uma ferramenta que so pode responder "nenhuma habilidade instalada".
    /// </para>
    /// <para>
    /// E o outro lado da mesma moeda do lazy loading: as skills nao viram ferramentas
    /// individuais, e a porta de entrada delas some quando nao ha nenhuma.
    /// </para>
    /// </summary>
    private void AtualizarFerramentaDeSkills()
    {
        var skill = new ExecuteSkillTool();
        int quantas = SkillService.GetSkillCount();

        if (quantas > 0)
        {
            _tools[skill.Name] = skill;
            _nativeToolNames.Add(skill.Name);
            Console.WriteLine($"[REGISTRY] execute_skill registrada ({quantas} habilidade(s) instalada(s)).");
        }
        else
        {
            _tools.Remove(skill.Name);
            _nativeToolNames.Remove(skill.Name);
        }
    }
}
