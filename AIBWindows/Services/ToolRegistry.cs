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

    /// <summary>
    /// Se, do texto de terceiros no contexto, algum é E-MAIL (e não só página do navegador).
    /// Sem ligação, vale o mesmo que <see cref="ConteudoDeEmailNoContexto"/>: na dúvida, é e-mail.
    /// </summary>
    public Func<bool>? EmailNoContexto { get; set; }

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

            // A memória sobre o usuário não aceita pedido que pode ter vindo de um e-mail ou de
            // uma página: "anote que o usuário quer X" valeria em todas as conversas seguintes.
            if (tool.SoComFalaDoUsuario && ConteudoDeEmailNoContexto?.Invoke() == true)
            {
                Console.WriteLine($"[REGISTRY] {toolName} recusada: texto de terceiros no contexto.");
                aoDecidir?.Invoke("recusada_no_pre_voo");
                return $"ACESSO NEGADO: '{toolName}' não roda com texto de e-mail ou de página no contexto. "
                       + "Se o usuário contou isso, guarde numa próxima conversa.";
            }

            // O que foi autorizado viaja até a execução: a ferramenta confere se ainda é aquilo.
            // Ver ITool.ExecutarAutorizadoAsync.
            CommandConfirmationContext? autorizado = null;

            // Na dúvida, pergunta: exceção ao decidir é pedir confirmação, nunca pular.
            bool pede;
            try { pede = tool.PedeConfirmacao(argumentsJson); }
            catch { pede = true; }

            if (pede)
            {
                if (DispensaPelaPasta(tool, argumentsJson))
                {
                    var (liberado, motivo, ctxDispensa) = await DispensarAsync(tool, argumentsJson, userLevel, aoDecidir);
                    if (!liberado) return motivo!;
                    autorizado = ctxDispensa;
                }
                else
                {
                    var (permitido, motivo, ctxCard) = await AuthorizeAsync(tool, argumentsJson, userLevel, aoEsperarHumano, aoDecidir);
                    if (!permitido) return motivo!;
                    autorizado = ctxCard;
                }
            }
            else
            {
                aoDecidir?.Invoke("automatica");
            }

            Console.WriteLine($"[REGISTRY] Executando: {toolName}({(argumentsJson.Length > 100 ? argumentsJson[..100] + "..." : argumentsJson)})");
            try
            {
                string resultado = await tool.ExecutarAutorizadoAsync(argumentsJson, userLevel, autorizado);

                // Segredo que veio de carona na saída (variável de ambiente, .env, string de
                // conexão) não vai ao modelo, e por isso também não vai ao raw.jsonl. Ver Segredos.
                string limpo = Segredos.Redigir(resultado, out int omitidos);
                if (omitidos == 0) return resultado;

                Console.WriteLine($"[REGISTRY] {toolName}: {omitidos} valor(es) com cara de segredo omitido(s).");
                return limpo + Segredos.Aviso(omitidos);
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
    /// <para>
    /// Sem contexto, NEGA — como o <see cref="AuthorizeAsync"/>. Antes, contexto nulo ou exceção
    /// ao montá-lo viravam <c>comando = ""</c>, a floor list não via nada e a chamada passava: o
    /// mesmo caso que o caminho com card recusa como <c>deny_sem_contexto</c> era permitido aqui,
    /// e ainda gravado na auditoria com o comando em branco.
    /// </para>
    /// </summary>
    private async Task<(bool Liberado, string? Motivo, CommandConfirmationContext? Contexto)> DispensarAsync(
        ITool tool, string argumentsJson, int userLevel, Action<string>? aoDecidir)
    {
        var ctx = MontarContexto(tool, argumentsJson, userLevel);
        if (ctx == null) return (false, await NegarSemContextoAsync(tool, userLevel, aoDecidir), null);

        string comando = ctx.Command ?? "";

        string? razao = BateNoPiso(tool, ctx, userLevel);
        if (razao != null)
        {
            await AuditLogService.AppendAsync(new { evento = "deny_floor", ferramenta = tool.Name, comando, userLevel, razao });
            aoDecidir?.Invoke("barrada_pelo_piso");
            return (false, razao, null);
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

        return (true, null, ctx);
    }

    /// <summary>
    /// Portão de autorização: floor list primeiro (best-effort, só refuta abaixo do Nível 7 e com
    /// ConfirmDangerousCommands ligado), cartão depois (autoridade canônica, dispara em qualquer
    /// nível). A auditoria grava ANTES da execução, em todos os desfechos.
    /// <para>
    /// A floor list rodava DEPOIS do cartão: o comando aparecia com "Motivo do bloqueio", o
    /// usuário clicava Permitir, e era recusado do mesmo jeito — uma pergunta cuja resposta "sim"
    /// não valia nada. O que o piso barra não vira pergunta. A exceção por nível é a mesma de
    /// sempre, e mora no <see cref="CommandFloorList.Match"/>: em L&gt;=7 o piso não barra nada.
    /// </para>
    /// </summary>
    private async Task<(bool Autorizado, string? Motivo, CommandConfirmationContext? Contexto)> AuthorizeAsync(
        ITool tool, string argumentsJson, int userLevel, Action<long>? aoEsperarHumano = null,
        Action<string>? aoDecidir = null)
    {
        var ctx = MontarContexto(tool, argumentsJson, userLevel);
        if (ctx == null) return (false, await NegarSemContextoAsync(tool, userLevel, aoDecidir), null);

        string comando = ctx.Command ?? "";

        string? razao = BateNoPiso(tool, ctx, userLevel);
        if (razao != null)
        {
            await AuditLogService.AppendAsync(new { evento = "deny_floor", ferramenta = tool.Name, comando, userLevel, razao });
            aoDecidir?.Invoke("barrada_pelo_piso");
            return (false, razao, null);
        }

        // A chave é o comando exato, a menos que a ferramenta diga outra (o navegador usa o site).
        var chave = (tool.Name, ctx.ChaveDeSempre ?? comando, (string?)null);

        // Texto de terceiros no contexto (e-mail, página): o pedido desta ação pode ter vindo de
        // instruções escritas por eles. O card avisa, e o "sempre permitir" deixa de pular a
        // pergunta — uma autorização dada antes, com outro contexto, não cobre o que o texto pode
        // ter pedido. Exceção declarada: SempreApesarDeTerceiros (ver o contexto).
        bool comEmail = ConteudoDeEmailNoContexto?.Invoke() == true;
        bool soEmail = comEmail && (EmailNoContexto?.Invoke() ?? true);
        ctx.ConteudoDeEmailNoContexto = comEmail;
        ctx.EmailNoContexto = soEmail;
        bool sempreSuspenso = ctx.SemSempre || (comEmail && !(ctx.SempreApesarDeTerceiros && !soEmail));

        // "Sempre permitir" vale só nesta sessão e casa byte a byte na chave.
        if (!sempreSuspenso && AlwaysAllowSession.Contains(chave))
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
                return (false, $"ACESSO NEGADO: '{tool.Name}' exige confirmação do usuário e não há interface disponível para pedi-la.", null);
            }

            Console.WriteLine($"[REGISTRY] {tool.Name}: esperando você autorizar...");

            var cronometro = System.Diagnostics.Stopwatch.StartNew();
            var resposta = await _confirmationPrompt.PerguntarAsync(ctx);
            cronometro.Stop();

            // Ninguém respondeu: nega como sem interface, e não como recusa do usuário. O modelo
            // que ouve "o usuário recusou" muda de plano por uma decisão que não existiu.
            if (resposta.SemResposta is string porque)
            {
                await AuditLogService.AppendAsync(new { evento = "deny_sem_ui", ferramenta = tool.Name, comando, userLevel, motivo = porque });
                aoDecidir?.Invoke("negada_sem_interface");
                return (false, $"ACESSO NEGADO: '{tool.Name}' exige confirmação do usuário, e {porque}. Nada foi executado.", null);
            }

            var (permitido, sempre) = (resposta.Allowed, resposta.AlwaysAllow);
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

            if (!permitido) return (false, RecusaDoUsuario, null);
            if (sempre && !sempreSuspenso) AlwaysAllowSession.Add(chave);
        }

        return (true, null, ctx);
    }

    /// <summary>
    /// O contexto do cartão, ou null. Exceção da ferramenta ao montá-lo também é null: sem
    /// descrever a operação não há o que autorizar, e null é negação nos dois caminhos.
    /// </summary>
    private static CommandConfirmationContext? MontarContexto(ITool tool, string argumentsJson, int userLevel)
    {
        try { return tool.BuildConfirmationContext(argumentsJson, userLevel); }
        catch (Exception ex)
        {
            Console.WriteLine($"[REGISTRY] {tool.Name}: falha ao montar o contexto de confirmação: {ex.Message}");
            return null;
        }
    }

    private static async Task<string> NegarSemContextoAsync(ITool tool, int userLevel, Action<string>? aoDecidir)
    {
        await AuditLogService.AppendAsync(new { evento = "deny_sem_contexto", ferramenta = tool.Name, userLevel });
        aoDecidir?.Invoke("negada_sem_contexto");
        return $"ACESSO NEGADO: '{tool.Name}' exige confirmação, mas não foi possível descrever a operação para autorizar.";
    }

    /// <summary>
    /// O motivo da floor list, ou null quando ela não barra. Primeiro a regra TIPADA da própria
    /// ferramenta (<see cref="ITool.PisoTipado"/>); depois, para quem manda linha de comando, a
    /// regex — ver <see cref="ITool.PassaPelaFloorListCom"/>.
    /// </summary>
    private string? BateNoPiso(ITool tool, CommandConfirmationContext ctx, int userLevel)
    {
        bool floorLigado = _settingsService?.LoadSettings().ConfirmDangerousCommands ?? true;
        if (!floorLigado) return null;

        if (tool.PisoTipado(ctx, userLevel) is string tipado) return tipado;

        if (!tool.PassaPelaFloorListCom(ctx)) return null;
        string comando = ctx.Command ?? "";

        var (bateu, razao) = CommandFloorList.Match(comando, userLevel);
        return bateu ? razao ?? "ACESSO NEGADO (FLOOR): comando destrutivo — requer Nível 7." : null;
    }

    /// <summary>
    /// Reavalia as skills quando um <c>write</c> ou <c>edit</c> bem-sucedido caiu dentro da pasta
    /// delas.
    /// <para>
    /// Quem fazia isto era o laço do agente, ao ver a ferramenta <c>materialize_skill</c> — que
    /// não existe mais. Uma skill escrita pelo modelo durante a conversa (um SKILL.md novo) só
    /// passava a existir na próxima abertura do app: a ferramenta <c>skill</c> nem aparecia no
    /// schema quando era a primeira.
    /// </para>
    /// <para>
    /// Quem chama é o laço, DEPOIS de o lote paralelo terminar, e não o
    /// <see cref="ExecuteToolAsync"/>: as ferramentas de um turno rodam em paralelo, e o
    /// <see cref="Refresh"/> mexe no dicionário que as outras chamadas estão lendo.
    /// </para>
    /// </summary>
    public void ReavaliarSkillsSeTocou(string ferramenta, string argumentsJson, string resultado)
    {
        try
        {
            if (ferramenta is not (Ferramentas.Gravar or Ferramentas.Editar)) return;
            if (Memory.ArtifactExtractor.Falhou(resultado)) return;

            var args = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(argumentsJson);
            if (args.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            if (!args.TryGetProperty("path", out var p) || p.ValueKind != System.Text.Json.JsonValueKind.String) return;

            string caminho = PathArgumentRepair.Normalize(p.GetString());

            // A mesma comparação de "está dentro da pasta" das pastas sem confirmação: resolve
            // '..' e junções, e não confunde "skills" com "skills-velhas".
            if (PastasSemConfirmacao.Dispensa(caminho, SkillService.Raiz)) Refresh();
        }
        catch (Exception ex)
        {
            // A gravação já aconteceu e deu certo. Falhar em reavaliar não pode virar erro dela.
            Console.WriteLine($"[REGISTRY] Falha ao reavaliar as skills: {ex.Message}");
        }
    }

    /// <summary>
    /// Reavalia as skills em disco. Chamado quando uma skill nasce durante a conversa: sem
    /// isto, a habilidade recem-instalada so existiria na proxima abertura do app. Ver
    /// <see cref="ReavaliarSkillsSeTocou"/>.
    /// </summary>
    public void Refresh()
    {
        AtualizarFerramentaDeSkills();
        Console.WriteLine($"[REGISTRY] Refresh completo. Total de ferramentas: {_tools.Count}");
    }

    public bool Contains(string toolName) => _tools.ContainsKey(toolName);

    /// <summary>
    /// Registra uma ferramenta além das nativas. Existe para os ensaios do PORTÃO: os caminhos
    /// de falha dele (contexto que não se monta, ferramenta que lança ao descrever a operação)
    /// não são alcançáveis pelas nativas depois do pré-voo — e um caminho de negação que
    /// ninguém exercita é um caminho que vira "permitir" sem ninguém ver. Foi o que aconteceu
    /// na dispensa por pasta. A ferramenta registrada aqui passa pelo mesmo portão de todas.
    /// </summary>
    public void Registrar(ITool tool) => _tools[tool.Name] = tool;

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
            new FsTool(),
            new LembrarTool(),
            new LembreteTool(),

            // O Edge só abre na primeira chamada: construir aqui não toca disco nem processo.
            new BrowserTool(Navegador.NavegadorService.Padrao,
                new Navegador.SitesLiberados(Navegador.SitesLiberados.ArquivoPadrao),
                new Navegador.AnotacoesDeSite(Navegador.AnotacoesDeSite.RaizPadrao)),

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
