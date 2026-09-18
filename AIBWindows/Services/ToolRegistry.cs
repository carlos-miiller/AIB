using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenAI.Chat;
using AIB.Services.Mail;
using AIB.Services.Tools;

namespace AIB.Services;

/// <summary>
/// Registro central das ferramentas nativas C# do agente AIB.
/// As skills dinâmicas não são mais registradas aqui individualmente para evitar overhead no LLM.
/// Em vez disso, o LLM usa a ferramenta 'skill' para chamá-las sob demanda (Lazy Loading).
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfirmationPrompt? _confirmationPrompt;
    private readonly SettingsService? _settingsService;

    /// <summary>
    /// O que o modelo recebe quando o usuário recusa no cartão. Constante porque quem lê o
    /// histórico depois (o extrator de artefatos) precisa reconhecer a recusa pelo texto exato.
    /// </summary>
    public const string RecusaDoUsuario = "Ação Rejeitada pelo Usuário.";

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

    /// <summary>
    /// A thread de e-mail à qual a conversa aberta está ligada (<c>conta|thr:id</c>), ou vazio.
    /// Quem liga é o <see cref="ConversationService"/>; o registry só pergunta.
    /// </summary>
    public Func<string>? ChaveDaConversaDeEmail { get; set; }

    /// <summary>
    /// Se há texto original de e-mail no contexto vivo da conversa. É o que faz o card de
    /// confirmação avisar — e o que suspende o "sempre permitir" enquanto durar.
    /// </summary>
    public Func<bool>? ConteudoDeEmailNoContexto { get; set; }

    private bool EmConversaDeEmail => !string.IsNullOrWhiteSpace(ChaveDaConversaDeEmail?.Invoke());

    /// <remarks>
    /// <c>mail_read</c> só é oferecida na conversa DE um e-mail. Fora dela não há o que ler, e o
    /// schema custaria tokens em toda requisição para uma ferramenta que só responderia erro.
    /// </remarks>
    public List<ChatTool> GetActiveTools(int userLevel)
        => _tools.Values
            .Where(t => t.RequiredLevel <= userLevel)
            .Where(t => t.Name != Ferramentas.LerEmail || EmConversaDeEmail)
            .Select(t => t.ChatToolDefinition)
            .ToList();

    public (List<ITool> Natives, List<ITool> Dynamics) GetCategorizedTools()
    {
        return (_tools.Values.ToList(), new List<ITool>());
    }

    /// <param name="aoEsperarHumano">
    /// Recebe os milissegundos que a ferramenta passou parada no cartão de confirmação, esperando o usuário
    /// decidir. Sem isto o tempo de gente vira tempo de máquina: um <c>Get-Content</c> trivial
    /// apareceu no log como "ok em 7299,6s" porque ninguém tinha clicado em autorizar por duas
    /// horas.
    /// </param>
    /// <param name="aoDecidir">
    /// Recebe COMO a chamada passou pelo portão, numa palavra: <c>automatica</c> (não pede
    /// confirmação), <c>pasta_dispensada</c>, <c>permitida</c>, <c>permitida_sempre</c>,
    /// <c>sempre_na_sessao</c>, <c>recusada</c>, <c>barrada_pelo_piso</c>, <c>negada_sem_contexto</c>,
    /// <c>negada_sem_interface</c>, <c>nivel_insuficiente</c>, <c>recusada_no_pre_voo</c> ou
    /// <c>ferramenta_desconhecida</c>. Vai para o raw.jsonl com o resultado: sem ela, um comando
    /// que você autorizou e um que nunca pediu confirmação ficam iguais no registro.
    /// </param>
    public async Task<string> ExecuteToolAsync(
        string toolName, string argumentsJson, int userLevel, Action<long>? aoEsperarHumano = null,
        Action<string>? aoDecidir = null)
    {
        if (_tools.TryGetValue(toolName, out var tool))
        {
            if (tool.RequiredLevel > userLevel)
            {
                aoDecidir?.Invoke("nivel_insuficiente");
                return $"ACESSO NEGADO: A ferramenta '{toolName}' exige Nível {tool.RequiredLevel}, mas o seu nível atual é {userLevel}.";
            }

            // ANTES do portão humano: chamada impossível não vira pergunta. Ver ITool.Validar.
            string? recusa = tool.Validar(argumentsJson);
            if (recusa != null)
            {
                Console.WriteLine($"[REGISTRY] {toolName} recusada no pré-voo.");
                aoDecidir?.Invoke("recusada_no_pre_voo");
                return recusa;
            }

            if (tool.RequiresConfirmation)
            {
                if (DispensaPelaPasta(tool, argumentsJson))
                {
                    var (liberado, recusaDoPiso) = await DispensarAsync(tool, argumentsJson, userLevel, aoDecidir);
                    if (!liberado) return recusaDoPiso!;
                }
                else
                {
                    var (autorizado, motivo) = await AuthorizeAsync(tool, argumentsJson, userLevel, aoEsperarHumano, aoDecidir);
                    if (!autorizado) return motivo!;
                }
            }
            else
            {
                aoDecidir?.Invoke("automatica");
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
        aoDecidir?.Invoke("ferramenta_desconhecida");
        return $"ERRO: Ferramenta '{toolName}' não encontrada no registry. Ferramentas disponíveis: {string.Join(", ", _tools.Keys)}.";
    }

    /// <summary>
    /// Se a chamada cai numa pasta que o usuário dispensou de confirmação.
    /// <para>
    /// Texto de e-mail no contexto ANULA a dispensa, pela mesma razão que anula o "sempre
    /// permitir": a ação pode ter sido pedida por quem escreveu o e-mail, e uma pasta marcada de
    /// confiança foi marcada contra os enganos do modelo, não contra um pedido de terceiro.
    /// </para>
    /// </summary>
    private bool DispensaPelaPasta(ITool tool, string argumentsJson)
    {
        if (ConteudoDeEmailNoContexto?.Invoke() == true) return false;

        try { return tool.DispensaConfirmacao(argumentsJson); }
        catch { return false; }
    }

    /// <summary>
    /// Deixa passar sem perguntar, mas nem por isso sem registrar: a auditoria grava TODA
    /// execução de ferramenta que pediria confirmação, inclusive as dispensadas. Uma gravação que
    /// não apareceu em card e não aparece no registro seria uma gravação invisível.
    /// <para>
    /// A floor list continua valendo. Dispensar o card é dispensar a PERGUNTA, não o piso: o que
    /// exige Nível 7 segue exigindo Nível 7 dentro da pasta de confiança.
    /// </para>
    /// </summary>
    private async Task<(bool Liberado, string? Motivo)> DispensarAsync(
        ITool tool, string argumentsJson, int userLevel, Action<string>? aoDecidir)
    {
        string comando = "";
        try { comando = tool.BuildConfirmationContext(argumentsJson, userLevel)?.Command ?? ""; }
        catch { }

        bool floorLigado = _settingsService?.LoadSettings().ConfirmDangerousCommands ?? true;
        if (floorLigado)
        {
            var (bateu, razao) = CommandFloorList.Match(comando, userLevel);
            if (bateu)
            {
                await AuditLogService.AppendAsync(new { evento = "deny_floor", ferramenta = tool.Name, comando, userLevel, razao });
                aoDecidir?.Invoke("barrada_pelo_piso");
                return (false, razao!);
            }
        }

        await AuditLogService.AppendAsync(new
        {
            evento = "allow_pasta_dispensada",
            ferramenta = tool.Name,
            comando,
            userLevel,
            pastas = PastasSemConfirmacao.Configuradas
        });

        Console.WriteLine($"[REGISTRY] {tool.Name}: sem confirmação — alvo em pasta dispensada.");
        aoDecidir?.Invoke("pasta_dispensada");

        return (true, null);
    }

    /// <summary>
    /// Portão de autorização, na ordem documentada em SEGURANCA.MD:
    /// cartão primeiro (autoridade canônica, dispara em qualquer nível), floor list depois
    /// (best-effort, só refuta abaixo do Nível 7 e com ConfirmDangerousCommands ligado).
    /// A auditoria grava ANTES da execução, em todos os desfechos.
    /// </summary>
    private async Task<(bool Autorizado, string? Motivo)> AuthorizeAsync(
        ITool tool, string argumentsJson, int userLevel, Action<long>? aoEsperarHumano = null,
        Action<string>? aoDecidir = null)
    {
        var ctx = tool.BuildConfirmationContext(argumentsJson, userLevel);
        if (ctx == null)
        {
            await AuditLogService.AppendAsync(new { evento = "deny_sem_contexto", ferramenta = tool.Name, userLevel });
            aoDecidir?.Invoke("negada_sem_contexto");
            return (false, $"ACESSO NEGADO: '{tool.Name}' exige confirmação, mas não foi possível descrever a operação para autorizar.");
        }

        string comando = ctx.Command ?? "";
        var chave = (tool.Name, comando, (string?)null);

        // Texto de e-mail no contexto: o pedido desta ação pode ter vindo de instruções escritas
        // por terceiros. O card avisa, e o "sempre permitir" deixa de pular a pergunta — uma
        // autorização dada antes, com outro contexto, não cobre o que o e-mail pode ter pedido.
        bool comEmail = ConteudoDeEmailNoContexto?.Invoke() == true;
        ctx.ConteudoDeEmailNoContexto = comEmail;

        // "Sempre permitir" vale só nesta sessão e casa byte a byte no comando exato.
        if (AlwaysAllowSession.Contains(chave) && !comEmail)
        {
            await AuditLogService.AppendAsync(new { evento = "allow_sessao", ferramenta = tool.Name, comando, userLevel });
            aoDecidir?.Invoke("sempre_na_sessao");
        }
        else
        {
            if (_confirmationPrompt == null)
            {
                await AuditLogService.AppendAsync(new { evento = "deny_sem_ui", ferramenta = tool.Name, comando, userLevel });
                aoDecidir?.Invoke("negada_sem_interface");
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
                semprePermitir = sempre,
                conteudoDeEmail = comEmail
            });

            aoDecidir?.Invoke(!permitido ? "recusada" : sempre ? "permitida_sempre" : "permitida");

            if (!permitido) return (false, RecusaDoUsuario);
            if (sempre) AlwaysAllowSession.Add(chave);
        }

        // Floor list roda DEPOIS do cartão: mesmo autorizado, categorias destrutivas exigem
        // Nível 7. Em L>=7 ou com a flag desligada, o cartão é a autoridade única.
        bool floorLigado = _settingsService?.LoadSettings().ConfirmDangerousCommands ?? true;
        if (floorLigado)
        {
            var (bateu, razao) = CommandFloorList.Match(comando, userLevel);
            if (bateu)
            {
                await AuditLogService.AppendAsync(new { evento = "deny_floor", ferramenta = tool.Name, comando, userLevel, razao });
                aoDecidir?.Invoke("barrada_pelo_piso");
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
            new EditFileTool(),
            new GlobTool(),
            new GrepTool(),
            new RunCommandTool(),
            new WriteFileTool(),

            // Registrada SEMPRE, e não só quando a triagem está ligada. Com ela fora, o modelo
            // não sabe que a pergunta tem resposta possível e chuta — e chutar sobre a caixa de
            // entrada de alguém é o pior desfecho. Desligada, ela responde exatamente isso.
            new ConsultarEmailsTool(_settingsService),

            // Registrada sempre, oferecida só na conversa de um e-mail (GetActiveTools). Tudo o
            // que ela precisa é lido NA CHAMADA: a conta pode ter sido desconectada, e a senha
            // trocada, desde que o app abriu.
            new LerEmailTool(
                () => ChaveDaConversaDeEmail?.Invoke() ?? "",
                () => _settingsService?.LoadSettings().MailAccounts ?? new List<MailAccountSettings>(),
                endereco => new MailVault().Ler(endereco),
                () => new MailKitMailService(
                    _settingsService?.LoadSettings().MailTimeoutSeconds ?? new UserAppSettings().MailTimeoutSeconds))
        };

        foreach (var tool in nativeTools)
        {
            _tools[tool.Name] = tool;
            Console.WriteLine($"[REGISTRY] Ferramenta nativa registrada: '{tool.Name}'");
        }

        AtualizarFerramentaDeSkills();
    }

    /// <summary>
    /// A skill so existe quando ha skill instalada.
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
            Console.WriteLine($"[REGISTRY] skill registrada ({quantas} habilidade(s) instalada(s)).");
        }
        else
        {
            _tools.Remove(skill.Name);
        }
    }
}
