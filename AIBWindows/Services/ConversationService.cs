using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services.Agent;
using AIB.Services.Ai;
using AIB.Services.Memory;
using OpenAI.Chat;

namespace AIB.Services;

/// <summary>
/// Dona única do histórico da conversa. Toda leitura e toda escrita da lista acontecem
/// sob o mesmo lock, e o que sai daqui é sempre cópia — nenhum outro objeto do processo
/// segura a lista viva. Traduz os eventos do AgentLoop para o que a interface consome.
/// </summary>
public sealed class ConversationService : IMessageStore
{
    // System prompt curto de propósito: modelos pequenos seguem melhor poucas regras diretivas,
    // e cada linha aqui é paga em TODA requisição da sessão.
    //
    // Removido em 2026-08-21:
    //   - "chame manage_memory(action=recall)": a ferramenta não existe desde o refactor. O
    //     modelo obedecia e gastava uma iteração inteira para receber "não encontrada".
    //   - instrução para escrever <think>...</think>: qwen3.5 e demais modelos de raciocínio
    //     usam o campo separado message.thinking. Pedir a tag fazia o modelo emitir marcação
    //     redundante no canal de conteúdo.
    private const string SYSTEM_PROMPT =
        """
        Você é o AIB, agente local de IA no Windows do usuário.

        Operação: pense brevemente, execute as ferramentas necessárias, responda.

        Regras:
        - As ferramentas DEVEM ser chamadas pelo Function Calling nativo da API. NUNCA escreva a chamada como texto na resposta — texto não executa nada.
        - Nunca afirme ter feito algo que você não executou por ferramenta.
        - Uma etapa por vez: chame a ferramenta, espere o resultado, e só então siga para a próxima.
        - Se uma ferramenta falhar, leia o erro, corrija os argumentos e tente mais UMA vez.
        - Operações destrutivas passam por confirmação do usuário. Receber "Ação Rejeitada pelo Usuário." é normal: reconheça e proponha alternativa, sem repetir a mesma chamada.
        - Não peça permissão em texto — chame a ferramenta. Responda em Português (Brasil), conciso.
        - Formate a resposta: parágrafos curtos separados por linha em branco, lista com "- " ao enumerar, blocos ``` para código e saída de comando. Conciso não é tudo grudado num parágrafo só.
        """;

    private readonly List<ChatMessage> _history = new();

    /// <summary>Protege TODA leitura e escrita de <see cref="_history"/>. Sem exceção.</summary>
    private readonly object _gate = new();

    /// <summary>Serializa turnos: dois streams nunca intercalam escritas no histórico.</summary>
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    private readonly SettingsService _settingsService;
    private readonly ToolRegistry _toolRegistry;
    private readonly AgentLoop _agentLoop;
    private readonly TokenCounter _tokenCounter;
    private readonly IChatProviderFactory _providerFactory;
    private readonly WarmupService _warmupService;

    /// <summary>
    /// Contexto capturado na construção (a UI). Como a camada de serviço usa
    /// ConfigureAwait(false), os eventos podem nascer numa thread do pool; são devolvidos
    /// para cá antes de chegarem à interface. Nulo em testes e no modo CLI.
    /// </summary>
    private readonly SynchronizationContext? _uiContext;

    /// <summary>
    /// Raiz alternativa da memória em disco. Nula em produção (usa ~/.AIB/memory); o teste
    /// aponta para uma pasta temporária e nunca escreve na memória real do usuário.
    /// </summary>
    private readonly string? _memoryRootOverride;

    /// <summary>
    /// Registro cru da sessão. Trocado a cada ResetHistory: sessão nova, pasta nova.
    /// </summary>
    private SessionMemory _sessionMemory;

    /// <summary>
    /// Turnos já gravados nesta sessão. Contador PRÓPRIO, e não o índice devolvido pelo
    /// TurnSplitter: o Trim encolhe o histórico vivo e reindexaria os turnos a cada poda,
    /// fazendo o mesmo índice apontar para turnos diferentes ao longo da sessão.
    /// </summary>
    private int _turnsRecorded;

    /// <summary>
    /// Tokens dos turnos CRUS que ja foram engolidos por um capitulo. Cresce a cada
    /// compactacao e e a metade esquerda da conta: o que a conversa custaria se nada tivesse
    /// sido resumido.
    /// </summary>
    private int _tokensCompactados;

    /// <summary>
    /// Tokens da faixa narrativa da memoria — atos e capitulos soltos — como ela esta AGORA no
    /// prompt. E o que substituiu os turnos acima, e por isso sai da conta: sem descontar,
    /// a economia apareceria maior do que e.
    /// </summary>
    private int _tokensDeResumo;

    /// <summary>
    /// Nome da conversa, quando o modelo já o produziu. Enquanto for null, quem arquiva usa a
    /// heurística antiga — a primeira mensagem do usuário, cortada.
    /// </summary>
    public string? Title { get; private set; }

    /// <summary>Avisa a interface que a conversa ganhou nome. Dispara fora da thread de UI.</summary>
    public event Action<string>? OnTitleChanged;

    /// <summary>Já houve a retitulação do primeiro capítulo? Ela acontece uma vez só.</summary>
    private bool _tituloRevisado;

    /// <summary>
    /// Identidade da conversa VIVA no histórico arquivado.
    /// <para>
    /// Existe para o arquivamento poder acontecer a cada turno sem duplicar a entrada: a mesma
    /// conversa é regravada por cima de si mesma até terminar. Troca no
    /// <see cref="ResetHistory"/>, que é onde uma conversa acaba e outra começa.
    /// </para>
    /// </summary>
    private string _sessionId = Guid.NewGuid().ToString();

    /// <summary>
    /// Identidade da conversa que está aberta agora. Quem desenha o histórico usa isto para
    /// distinguir a conversa em andamento das arquivadas: ela aparece na lista, mas não é para
    /// ser recuperada nem excluída — está na tela.
    /// </summary>
    public string SessionId => _sessionId;

    /// <summary>
    /// O histórico arquivado mudou: entrada nova, conteúdo novo ou nome novo.
    /// <para>
    /// Dispara fora da thread de interface. Existe porque o painel lê o arquivo uma vez ao
    /// montar: sem aviso, a conversa em andamento só aparecia na lista quando o painel fosse
    /// reaberto, e o nome dado pelo modelo chegava um passo atrasado.
    /// </para>
    /// </summary>
    public event Action? OnHistoryChanged;

    /// <summary>
    /// A conversa inteira, para o histórico arquivado — separada do histórico VIVO de propósito.
    /// <para>
    /// O histórico vivo encolhe: o Trim corta o começo e a compactação troca turnos por
    /// capítulos. Arquivar a partir dele daria certo no primeiro turno e, depois da primeira
    /// compactação, substituiria a conversa gravada por um pedaço dela — perdendo justamente a
    /// parte antiga, que é a que o usuário não lembra e por isso vai procurar no histórico.
    /// </para>
    /// </summary>
    private readonly List<ChatMessage> _transcricao = new();

    /// <summary>
    /// Turno que estava aberto quando o registro passou por ele. Guardado para ser gravado
    /// assim que ficar claro que não vai fechar — isto é, quando outro turno começar.
    /// </summary>
    private Turn? _turnoPendente;

    /// <summary>Capitulos desta sessao. So a thread que segura o <see cref="_turnGate"/> mexe.</summary>
    private readonly MemoryLayer _memory = new();

    /// <summary>
    /// Fatos duráveis, na raiz da memória. Não é trocado no ResetHistory: capítulo e ato morrem
    /// com a sessão, fato é justamente o que sobra depois dela.
    /// </summary>
    private readonly FactStore _facts;

    /// <summary>
    /// CTS do turno em voo. Protegido por <see cref="_ctsGate"/>: o Stop vem da thread de
    /// UI enquanto o turno corre numa thread do pool, e cancelar/descartar sem lock deixava
    /// o Cancel cair num CTS já descartado.
    /// </summary>
    private CancellationTokenSource? _generationCts;

    private readonly object _ctsGate = new();

    public ConversationService(
        SettingsService settingsService,
        ToolRegistry toolRegistry,
        AgentLoop agentLoop,
        TokenCounter tokenCounter,
        IChatProviderFactory providerFactory,
        string? memoryRootOverride = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _agentLoop = agentLoop ?? throw new ArgumentNullException(nameof(agentLoop));
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));

        _uiContext = SynchronizationContext.Current;
        _memoryRootOverride = memoryRootOverride;
        _sessionMemory = NewSessionMemory();
        _facts = new FactStore(memoryRootOverride);

        _warmupService = new WarmupService(_settingsService, _toolRegistry, _providerFactory, _tokenCounter);
        _warmupService.OnWarmupStateChanged += state => RaiseWarmupState(state);
        // Ao SAIR do aquecimento o contador e refeito: o prefixo fixo ja esta montado e o
        // numero deixa de ser o da janela recem-aberta.
        _warmupService.OnWarmupStateChanged += aquecendo =>
        {
            if (!aquecendo)
                NotifyTokenCount(LevelService.GetLevel(_settingsService.LoadSettings().MessageCount));
        };

        // Popula o histórico inicial (System Prompt / SOUL) para já termos a métrica de
        // tokens. Nenhuma tarefa de fundo nasce daqui: o aquecimento é explícito.
        ResetHistory();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Eventos e superfície pública
    // ─────────────────────────────────────────────────────────────────────────

    public event Action<TokenReport>? OnTokenCountChanged;
    public event Action<bool>? OnWarmupStateChanged;

    public ToolRegistry Registry => _toolRegistry;

    public int CurrentTokenCount => CountTokens();

    /// <summary>Fotografia atual do custo de contexto, para a interface desenhar.</summary>
    public TokenReport CurrentTokenReport =>
        MontarRelatorio(LevelService.GetLevel(_settingsService.LoadSettings().MessageCount));

    public void CancelGeneration()
    {
        lock (_ctsGate)
        {
            try { _generationCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Cópia do histórico para persistência/diagnóstico. Somente leitura.</summary>
    public IReadOnlyList<ChatMessage> SnapshotHistory() => Snapshot();

    /// <summary>Salva a sessão atual e recria o system prompt (SOUL/skills/home dir).</summary>
    public void ResetHistory()
    {
        bool tinhaConversa;
        lock (_gate)
        {
            tinhaConversa = _transcricao.Count > 0;
            _history.Clear();
        }

        // IO de disco fora do lock: nada bloqueante segura o histórico.
        // A gravação final é por cima da mesma entrada que os turnos já vinham atualizando.
        if (tinhaConversa) ArquivarConversaViva();

        // Histórico zerado é sessão nova: pasta nova em memory/sessions e contagem de turnos
        // reiniciada. Continuar gravando na pasta anterior misturaria duas conversas num
        // raw.jsonl só, e o resumo de capítulo sairia costurando assuntos sem relação.
        if (tinhaConversa)
        {
            _sessionMemory = NewSessionMemory();
            _turnsRecorded = 0;
            _tokensCompactados = 0;
            _tokensDeResumo = 0;
            _tituloRevisado = false;
            Title = null;
            _sessionId = Guid.NewGuid().ToString();
            _turnoPendente = null;
            lock (_gate) { _transcricao.Clear(); }
            _memory.Clear();
        }

        string? prompt = BuildSystemPrompt();
        if (prompt == null) return;

        lock (_gate)
        {
            if (_history.Count == 0) _history.Add(ChatMessage.CreateSystemMessage(prompt));
        }

        // Os fatos duráveis atravessam sessões: eles têm de estar no prompt desde o PRIMEIRO
        // turno, e não só depois da primeira compactação. Capítulos e atos ainda não existem
        // aqui, então o bloco sai só com fatos — ou vazio, e nem chega a ser inserido.
        RefreshMemoryMessage(CurrentQuota(LevelService.GetLevel(_settingsService.LoadSettings().MessageCount)));
    }

    /// <summary>
    /// Injeta um chat recuperado da barra lateral. Substitui os dois History.Add() que a
    /// ChatWindow fazia direto na lista viva.
    /// </summary>
    public void AppendRecoveredContext(string title, string content)
    {
        lock (_gate)
        {
            _history.Add(ChatMessage.CreateUserMessage($"[CONTEXTO RECUPERADO DO CHAT: {title}]\n\n{content}"));
            _history.Add(ChatMessage.CreateAssistantMessage("Contexto compreendido. Em que posso ajudar com isso?"));
        }
    }

    /// <summary>
    /// Troca o histórico vivo pela conversa arquivada inteira — o "Abrir conversa" do painel.
    /// <para>
    /// Diferente do <see cref="AppendRecoveredContext"/>, que ANEXA a conversa antiga como um
    /// bloco de texto dentro da conversa corrente. Aqui a conversa antiga PASSA A SER a
    /// corrente: cada fala volta ao seu papel — usuário como usuário, agente como agente — e
    /// o modelo enxerga um diálogo, não um relatório sobre um diálogo.
    /// </para>
    /// <para>
    /// O <see cref="ResetHistory"/> antes disso não é detalhe: ele salva a conversa que estava
    /// aberta, abre pasta nova em memory/sessions e recria o system prompt. Sem ele, a conversa
    /// aberta ficaria emendada na anterior e as duas dividiriam o mesmo raw.jsonl.
    /// </para>
    /// </summary>
    /// <param name="memorySessionId">
    /// Pasta da conversa em <c>memory/sessions</c>. Quando ela existe, os capitulos e atos
    /// voltam com a conversa e os turnos que eles resumem NAO voltam crus.
    /// </param>
    /// <param name="sessionId">
    /// Id da entrada no historico. Continuar a conversa aberta tem de atualizar a MESMA linha
    /// da lista; sem isto ela seguiria com um Guid novo e a mesma conversa apareceria duas
    /// vezes no painel, uma parada no passado e outra crescendo.
    /// </param>
    public void LoadConversation(
        IReadOnlyList<ChatTurn> falas, string? memorySessionId = null, string? sessionId = null)
    {
        ResetHistory();

        if (!string.IsNullOrWhiteSpace(sessionId)) _sessionId = sessionId!;

        bool comMemoria = RestaurarMemoria(memorySessionId);

        lock (_gate)
        {
            foreach (var fala in falas)
            {
                var mensagem = fala.DoUsuario
                    ? ChatMessage.CreateUserMessage(fala.Texto)
                    : (ChatMessage)ChatMessage.CreateAssistantMessage(fala.Texto);

                // A transcrição sempre leva a conversa INTEIRA: ela é o que volta para o
                // arquivo, e arquivar só o que sobrou no contexto encolheria a conversa um
                // pouco a cada vez que ela fosse aberta.
                _transcricao.Add(mensagem);

                // O contexto vivo só recebe tudo quando não houve memória a restaurar. Com
                // memória, os turnos resumidos já estão representados pelos capítulos, e
                // acrescentá-los aqui mandaria a mesma conversa duas vezes ao modelo.
                if (!comMemoria) _history.Add(mensagem);
            }
        }

        if (comMemoria)
            RefreshMemoryMessage(CurrentQuota(
                LevelService.GetLevel(_settingsService.LoadSettings().MessageCount)));
    }

    /// <summary>
    /// Traz de volta os capitulos, atos e turnos crus de uma sessao arquivada. Devolve false
    /// quando nao ha pasta, quando ela esta vazia ou quando o id nao presta — e ai quem chama
    /// carrega a conversa do jeito antigo, toda crua.
    /// <para>
    /// Os turnos ja cobertos por capitulo NAO voltam ao contexto: eles viram tokens no total do
    /// contador, que e o unico lugar onde continuam pesando. Os demais voltam, mas so as falas
    /// de usuario e do agente. Chamada de ferramenta e resultado ficam de fora de proposito:
    /// um tool_calls sem o resultado correspondente quebra a requisicao seguinte, e remontar os
    /// pares a partir do disco e uma chance de erro sem ganho — os literais que importavam
    /// ficaram nos artefatos dos capitulos.
    /// </para>
    /// </summary>
    private bool RestaurarMemoria(string? memorySessionId)
    {
        if (string.IsNullOrWhiteSpace(memorySessionId)) return false;

        try
        {
            var memoria = new SessionMemory(memorySessionId!, _memoryRootOverride);
            var turnos = memoria.ReadTurns();
            var capitulos = memoria.ReadChapters();
            var atos = memoria.ReadActs();

            if (turnos.Count == 0 && capitulos.Count == 0) return false;

            _sessionMemory = memoria;
            _turnsRecorded = turnos.Count == 0 ? 0 : turnos[^1].Index + 1;

            _memory.Clear();
            _memory.AddRange(capitulos);
            foreach (var ato in atos) _memory.Add(ato);

            int ultimoCoberto = _memory.LastCoveredTurn;
            _tokensCompactados = 0;

            lock (_gate)
            {
                foreach (var turno in turnos)
                {
                    if (turno.Index <= ultimoCoberto)
                    {
                        // O turno inteiro, inclusive o miolo de ferramentas: e isso que a
                        // conversa custaria se o capitulo nao existisse.
                        foreach (var registro in turno.Messages)
                            _tokensCompactados += _tokenCounter.CountText(registro.Text ?? "");

                        continue;
                    }

                    foreach (var registro in turno.Messages)
                    {
                        if (string.IsNullOrWhiteSpace(registro.Text)) continue;

                        if (registro.Role == "user")
                            _history.Add(ChatMessage.CreateUserMessage(registro.Text));
                        else if (registro.Role == "assistant")
                            _history.Add(ChatMessage.CreateAssistantMessage(registro.Text));
                    }
                }
            }

            Console.WriteLine(
                $"[MEMORIA] Conversa reaberta em {memorySessionId}: {capitulos.Count} capitulo(s), " +
                $"{atos.Count} ato(s), {turnos.Count} turno(s) gravados.");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Nao foi possivel restaurar '{memorySessionId}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Aquecimento em background. Chamado pelo App, nunca por um construtor.</summary>
    public Task StartWarmupAsync() => _warmupService.RunAsync(SnapshotHistory(), CancellationToken.None);

    // ─────────────────────────────────────────────────────────────────────────
    // Memória de sessão (registro cru)
    // ─────────────────────────────────────────────────────────────────────────

    private SessionMemory NewSessionMemory() =>
        new(SessionMemory.SessionIdFrom(DateTime.Now), _memoryRootOverride);

    /// <summary>Pasta desta sessão em disco. Diagnóstico e teste.</summary>
    public string SessionMemoryDir => _sessionMemory.SessionDir;

    /// <summary>
    /// Grava em disco o último turno, se ele estiver fechado. Chamado ao fim de cada turno,
    /// ainda sob o portão — o turno seguinte não começa antes de este estar registrado.
    /// <para>
    /// Grava o ÚLTIMO turno e não todos: os anteriores já foram gravados, e o Trim pode ter
    /// comido o começo do histórico vivo. O que saiu do contexto continua em raw.jsonl.
    /// </para>
    /// </summary>
    private void RecordLastTurn()
    {
        try
        {
            var turnos = TurnSplitter.Split(Snapshot());
            if (turnos.Count == 0) return;

            var ultimo = turnos[^1];

            // Um turno que ficou aberto na chamada ANTERIOR não vai fechar mais: o usuário já
            // mandou outra mensagem por cima. Ele é gravado agora, antes do atual, para a ordem
            // se manter.
            //
            // Sem isto o turno era perdido para sempre, e em silêncio. Só o ÚLTIMO turno é
            // examinado a cada chamada, então um turno pulado nunca voltava a ser olhado — o
            // comentário antigo dizia "fica para a próxima", e não ficava. Medido: uma conversa
            // de dois turnos em que o primeiro terminou sem resposta do modelo foi para o disco
            // com um turno só, tanto no raw.jsonl quanto no histórico arquivado.
            if (_turnoPendente != null && !MesmoTurno(_turnoPendente, ultimo))
            {
                Gravar(_turnoPendente);
                _turnoPendente = null;
            }

            // Turno aberto (cancelado no meio, ou teto de iterações com ferramenta pendente):
            // gravá-lo agora deixaria em disco um tool_calls sem resultado, e ele ainda pode
            // fechar no próximo passo do mesmo turno. Fica guardado.
            if (!TurnSplitter.IsClosed(ultimo))
            {
                _turnoPendente = ultimo;
                return;
            }

            _turnoPendente = null;
            Gravar(ultimo);
        }
        catch (Exception ex)
        {
            // Registro é acréscimo. Nenhuma falha aqui pode escapar para o turno do usuário.
            Console.WriteLine($"[MEMORIA] Falha ao registrar o turno: {ex.Message}");
        }
    }

    /// <summary>
    /// Dois turnos são o mesmo quando abrem na MESMA mensagem de usuário.
    /// <para>
    /// Comparar as instâncias de <see cref="Turn"/> não serve: o <see cref="TurnSplitter"/>
    /// cria objetos novos a cada chamada. As mensagens, não — são as mesmas referências do
    /// histórico vivo.
    /// </para>
    /// </summary>
    private static bool MesmoTurno(Turn a, Turn b) =>
        a.Messages.Count > 0 && b.Messages.Count > 0 && ReferenceEquals(a.Messages[0], b.Messages[0]);

    /// <summary>Põe um turno no registro cru e na transcrição do histórico.</summary>
    private void Gravar(Turn turno)
    {
        if (_sessionMemory.AppendTurn(turno with { Index = _turnsRecorded }))
            _turnsRecorded++;

        // A mesma unidade que vai para o disco alimenta a transcrição do histórico. Só as
        // duas falas: o miolo de ferramentas do turno não é conversa.
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(turno.UserText))
                _transcricao.Add(ChatMessage.CreateUserMessage(turno.UserText));

            if (!string.IsNullOrWhiteSpace(turno.AssistantText))
                _transcricao.Add(ChatMessage.CreateAssistantMessage(turno.AssistantText));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Compactação (capítulos)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Turnos recentes que a compactação nunca toca: são o contexto imediato.</summary>
    private const int KeepRecentTurns = 2;

    /// <summary>
    /// Depois de compactar, a conversa viva deve cair para esta fração da cota. Compactar até
    /// só encostar no gatilho faria a compactação seguinte disparar quase junto — e cada
    /// compactação custa um prefill frio, porque reescreve o começo do prompt.
    /// </summary>
    private const double TargetAfterCompaction = 0.5;

    /// <summary>
    /// Teto da chamada de resumo. Independente do turno: o usuário já foi respondido.
    /// <para>
    /// Quatro minutos, e não dois. Medido no qwen3.5:4b em CPU, com o raciocínio desligado: o
    /// prefill de um capítulo grande chega a ~2 minutos e a geração é limitada a
    /// <see cref="Compactor.MaxSummaryTokens"/>. Os dois minutos anteriores eram chute e
    /// estouravam em toda tentativa.
    /// </para>
    /// </summary>
    private static readonly TimeSpan SummaryTimeout = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Teto de turnos por capítulo.
    /// <para>
    /// Sem ele, uma compactação que falha volta na tentativa seguinte com MAIS turnos — foi o
    /// que se viu contra o Ollama real: 5, 6, 7, 8, 9, 10, 11 turnos, cada tentativa mais cara
    /// que a anterior e todas estourando o tempo. Um teto fixo faz o custo do resumo parar de
    /// crescer, e o que sobrar vira o capítulo seguinte.
    /// </para>
    /// </summary>
    private const int MaxTurnsPerChapter = 8;

    /// <summary>
    /// Turnos de descanso depois de uma compactação que falhou.
    /// <para>
    /// A falha custa o <see cref="SummaryTimeout"/> inteiro, e ele é cobrado do usuário: a
    /// compactação segura o portão, então o turno SEGUINTE espera por ela. Sem descanso, um
    /// resumidor lento transforma toda mensagem daí em diante numa espera de quatro minutos.
    /// Melhor deixar a poda de emergência cuidar do contexto por alguns turnos.
    /// </para>
    /// </summary>
    private const int CompactionCooldownTurns = 3;

    /// <summary>Turnos que faltam para tentar compactar de novo. Só a thread do portão mexe.</summary>
    private int _compactionCooldown;

    /// <summary>Capítulos fechados nesta sessão. Diagnóstico e teste.</summary>
    public IReadOnlyList<Chapter> Chapters => _memory.Chapters;

    /// <summary>
    /// Compacta os turnos mais antigos num capítulo, se a conversa viva passou do gatilho.
    /// <para>
    /// Roda ao FIM do turno, ainda sob o portão: nunca no meio de uma cadeia de ferramentas.
    /// Nunca lança — compactação é melhoria, e a poda de emergência continua atrás como rede.
    /// </para>
    /// </summary>
    public async Task CompactIfNeededAsync(int userLevel, CancellationToken ct = default)
    {
        try
        {
            if (_compactionCooldown > 0)
            {
                _compactionCooldown--;
                return;
            }

            var quota = CurrentQuota(userLevel);
            if (quota.IsOff) return;

            int vivo = LiveTokens();
            int gatilho = MemoryBudget.CompactionThreshold(
                quota, _settingsService.LoadSettings().CompactionTrigger);
            if (vivo <= gatilho) return;

            var candidatos = SelectTurnsToCompact(quota, vivo);
            if (candidatos.Count == 0) return;

            Console.WriteLine($"[MEMORIA] Compactando {candidatos.Count} turno(s): vivo={vivo} > gatilho={gatilho}.");

            await FecharCapituloAsync(candidatos, quota, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _compactionCooldown = CompactionCooldownTurns;
            Console.WriteLine(
                $"[MEMORIA] Compactação cancelada (teto de {SummaryTimeout.TotalMinutes:F0} min). " +
                $"Os turnos seguem no contexto vivo; nova tentativa em {CompactionCooldownTurns} turno(s).");
        }
        catch (Exception ex)
        {
            _compactionCooldown = CompactionCooldownTurns;
            Console.WriteLine(
                $"[MEMORIA] Compactação falhou: {ex.Message}. " +
                $"Nova tentativa em {CompactionCooldownTurns} turno(s).");
        }
    }

    /// <summary>
    /// Fecha um capitulo sobre <paramref name="candidatos"/>: resume, tira os turnos do
    /// contexto, grava e reescreve o bloco de memoria. Lanca em falha de resumo — quem chama
    /// decide o que dizer ao usuario.
    /// </summary>
    private async Task<Chapter> FecharCapituloAsync(
        List<Turn> candidatos, MemoryQuota quota, CancellationToken ct)
    {
        var settings = _settingsService.LoadSettings();
        var compactor = new Compactor(_providerFactory.GetProvider(settings));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SummaryTimeout);

        var capitulo = await compactor
            .SummarizeAsync(_memory.NextChapterIndex, candidatos, timeout.Token)
            .ConfigureAwait(false);

        // Medido ANTES da remocao, e sobre as mensagens originais: e este o custo que o
        // capitulo acabou de tirar do prompt, e o unico numero que torna a economia
        // verificavel depois.
        _tokensCompactados += _tokenCounter.CountMessages(candidatos.SelectMany(t => t.Messages));

        // So remove DEPOIS que o capitulo existe. Remover antes e falhar o resumo perderia os
        // turnos das duas pontas: fora do contexto e sem substituto.
        RemoveOldestMessages(candidatos.Sum(t => t.Messages.Count));

        _memory.Add(capitulo);
        _sessionMemory.AppendChapter(capitulo);

        // Promocao antes de reescrever o bloco: se um ato nascer agora, ele ja entra no mesmo
        // prompt, e o prefixo e invalidado UMA vez em vez de duas.
        await PromoverAsync(compactor, ChaptersPerAct, ct).ConfigureAwait(false);

        // Mesma carona: o prefixo ja foi invalidado por esta compactacao, entao revisar o nome
        // da conversa agora nao custa cache nenhum.
        await RetitularPeloCapituloAsync(capitulo.Summary, ct).ConfigureAwait(false);

        RefreshMemoryMessage(quota);

        Console.WriteLine($"[MEMORIA] Capitulo {capitulo.Index} fechado ({capitulo.Artifacts.Count} artefato(s)). Vivo agora: {LiveTokens()} tokens.");

        return capitulo;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Compactação a pedido do usuário
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fecha um capitulo AGORA, com as mensagens que existem, sem esperar o gatilho de tokens.
    /// <para>
    /// Segura o mesmo portao do turno: compactar no meio de uma resposta mexeria no historico
    /// embaixo do laco. Devolve a frase que a interface mostra — inclusive a de recusa, porque
    /// um "nao deu" sem motivo e pior que nao ter o comando.
    /// </para>
    /// </summary>
    public async Task<string> ForcarCapituloAsync(int userLevel, CancellationToken ct = default)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var quota = CurrentQuota(userLevel);
            if (quota.IsOff)
                return "Nao ha cota de memoria neste nivel: o prompt fixo ja ocupa o orcamento inteiro.";

            var candidatos = SelectTurnsToCompact(quota, LiveTokens(), forcado: true);
            if (candidatos.Count == 0)
                return $"Nada a compactar: os {KeepRecentTurns} turnos mais recentes ficam sempre fora, "
                     + "e nao ha turno fechado antes deles.";

            var capitulo = await FecharCapituloAsync(candidatos, quota, ct).ConfigureAwait(false);

            NotifyTokenCount(userLevel);

            return $"Capitulo {capitulo.Index} fechado: {candidatos.Count} turno(s) viraram resumo, "
                 + $"com {capitulo.Artifacts.Count} artefato(s) preservados.";
        }
        catch (OperationCanceledException)
        {
            return $"O resumidor passou de {SummaryTimeout.TotalMinutes:F0} minutos e foi interrompido. "
                 + "As mensagens continuam no contexto.";
        }
        catch (Exception ex)
        {
            return $"A compactacao falhou: {ex.Message}. As mensagens continuam no contexto.";
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Fecha um ato AGORA sobre os capitulos soltos, sem esperar os
    /// <see cref="ChaptersPerAct"/> de praxe.
    /// <para>
    /// Exige dois capitulos no minimo. Um ato sobre um capitulo so e resumo de resumo sem
    /// ganho nenhum: trocaria o texto por outro mais pobre e ainda esconderia o original, que
    /// deixa de ser renderizado assim que um ato o cobre.
    /// </para>
    /// </summary>
    public async Task<string> ForcarAtoAsync(int userLevel, CancellationToken ct = default)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int soltos = _memory.UncoveredChapters.Count;
            if (soltos < 2)
                return soltos == 0
                    ? "Nao ha capitulo solto para promover. Feche um capitulo antes."
                    : "So ha um capitulo solto. Um ato sobre ele seria resumo de resumo, sem "
                    + "ganho e com perda: o capitulo original deixaria de ser mostrado.";

            var settings = _settingsService.LoadSettings();
            var compactor = new Compactor(_providerFactory.GetProvider(settings));

            var ato = await PromoverAsync(compactor, minimo: 2, ct).ConfigureAwait(false);
            if (ato == null) return "A promocao falhou. Os capitulos seguem soltos.";

            RefreshMemoryMessage(CurrentQuota(userLevel));
            NotifyTokenCount(userLevel);

            return $"Ato {ato.Index} fechado sobre os capitulos {ato.FirstChapter}-{ato.LastChapter}.";
        }
        catch (Exception ex)
        {
            return $"A promocao falhou: {ex.Message}. Os capitulos seguem soltos.";
        }
        finally
        {
            _turnGate.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Promoção (atos e fatos)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Capítulos soltos que fecham um ato. Quatro, e não dois: promover cedo demais custa um
    /// resumo de resumo por quase nada, e resumo de resumo é onde a informação some.
    /// </summary>
    private const int ChaptersPerAct = 4;

    /// <summary>Atos fechados nesta sessão. Diagnóstico e teste.</summary>
    public IReadOnlyList<Act> Acts => _memory.Acts;

    /// <summary>Fatos duráveis em disco. Diagnóstico e teste.</summary>
    public string FactsPath => _facts.FactsPath;

    /// <summary>
    /// Fecha um ato quando há capítulos soltos suficientes, e promove a fatos duráveis o que
    /// atravessou capítulos o bastante.
    /// <para>
    /// Nunca lança: o capítulo já está fechado e gravado, e uma falha aqui não pode desfazê-lo.
    /// </para>
    /// </summary>
    private async Task<Act?> PromoverAsync(Compactor compactor, int minimo, CancellationToken ct)
    {
        try
        {
            var soltos = _memory.UncoveredChapters;
            if (soltos.Count < minimo) return null;

            Console.WriteLine($"[MEMORIA] Promovendo {soltos.Count} capítulo(s) a ato.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SummaryTimeout);

            var ato = await compactor
                .PromoteAsync(_memory.NextActIndex, soltos, timeout.Token)
                .ConfigureAwait(false);

            _memory.Add(ato);
            _sessionMemory.AppendAct(ato);

            // Fatos saem de TODOS os capítulos da sessão, e não só dos deste ato: o que se
            // conta é quantos capítulos distintos um literal atravessou, e esse número não
            // reinicia quando um ato fecha.
            var candidatos = ArtifactDigest.Distill(_memory.Chapters);
            int promovidos = _facts.Promote(candidatos);

            Console.WriteLine(
                $"[MEMORIA] Ato {ato.Index} fechado (capítulos {ato.FirstChapter}–{ato.LastChapter}, " +
                $"{ato.Artifacts.Count} artefato(s)). Fatos novos: {promovidos}.");

            return ato;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[MEMORIA] Promoção cancelada. Os capítulos seguem soltos.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Promoção falhou: {ex.Message}");
            return null;
        }
    }

    /// <summary>Cota atual, descontado o prefixo fixo (SOUL + prompt base).</summary>
    private MemoryQuota CurrentQuota(int userLevel)
    {
        int prefixo;
        lock (_gate)
        {
            // Só a PRIMEIRA mensagem de sistema conta como prefixo fixo. A segunda é o próprio
            // bloco de memória, que já é pago pela cota de memória — contá-la aqui encolheria a
            // cota a cada capítulo, num aperto que se realimenta.
            prefixo = _history.Count > 0 && _history[0] is SystemChatMessage
                ? _tokenCounter.CountMessages(new[] { _history[0] })
                : 0;
        }

        return MemoryBudget.Compute(
            LevelService.GetMaxTokensForLevel(userLevel), prefixo,
            _settingsService.LoadSettings().MemoryFraction);
    }

    /// <summary>Tokens da conversa viva: tudo menos as mensagens de sistema do começo.</summary>
    private int LiveTokens()
    {
        lock (_gate)
        {
            int inicio = FirstRemovableIndex();
            return _tokenCounter.CountMessages(_history.Skip(inicio));
        }
    }

    /// <summary>
    /// Turnos mais antigos a compactar: os suficientes para a conversa viva cair à metade da
    /// cota. Nunca os <see cref="KeepRecentTurns"/> últimos, e nunca um turno aberto — um
    /// tool_calls sem resultado quebra a requisição seguinte.
    /// </summary>
    /// <param name="forcado">
    /// Ignora o alvo de tokens e leva os turnos disponiveis mesmo com a conversa folgada. E o
    /// caminho do comando do usuario: ele pediu um capitulo, nao perguntou se compensava.
    /// </param>
    private List<Turn> SelectTurnsToCompact(MemoryQuota quota, int vivo, bool forcado = false)
    {
        List<ChatMessage> vivos;
        lock (_gate) { vivos = _history.Skip(FirstRemovableIndex()).ToList(); }

        var turnos = TurnSplitter.Split(vivos);
        int disponiveis = turnos.Count - KeepRecentTurns;
        if (disponiveis <= 0) return new List<Turn>();

        int alvo = (int)(quota.Live * TargetAfterCompaction);
        var escolhidos = new List<Turn>();
        int restante = vivo;

        int teto = Math.Min(disponiveis, MaxTurnsPerChapter);

        for (int i = 0; i < teto && (forcado || restante > alvo); i++)
        {
            if (!TurnSplitter.IsClosed(turnos[i])) break;

            escolhidos.Add(turnos[i]);
            restante -= _tokenCounter.CountMessages(turnos[i].Messages);
        }

        return escolhidos;
    }

    /// <summary>Remove as <paramref name="count"/> mensagens mais antigas depois do prefixo de sistema.</summary>
    private void RemoveOldestMessages(int count)
    {
        lock (_gate)
        {
            int inicio = FirstRemovableIndex();
            int remover = Math.Min(count, _history.Count - inicio);
            if (remover > 0) _history.RemoveRange(inicio, remover);
        }
    }

    /// <summary>
    /// Reescreve o bloco de memória, mantido como uma SEGUNDA mensagem de sistema logo após a
    /// primeira.
    /// <para>
    /// Mensagem separada, e não texto acrescentado à primeira, por dois motivos. Reconstruir a
    /// primeira exigiria reler SOUL.MD e as skills do disco a cada capítulo, sob o portão — e
    /// uma falha de leitura passageira derrubaria a persona no meio da conversa. Além disso a
    /// primeira mensagem fica byte a byte idêntica, então o cache de prefixo a reaproveita
    /// inteira; só o que vem depois do bloco de memória é reavaliado.
    /// </para>
    /// </summary>
    private void RefreshMemoryMessage(MemoryQuota quota)
    {
        // Relido do disco a cada montagem: facts.md é do usuário, e ele pode tê-lo editado com
        // o app aberto. Custa uma leitura de arquivo pequeno, e só acontece quando um capítulo
        // ou ato nasce.
        _memory.SetFacts(_facts.ReadFacts());

        string narrativa = _memory.RenderNarrative(quota, _tokenCounter);
        string bloco = _memory.Render(quota, _tokenCounter);

        // Os arquivos anexados viajam na MESMA mensagem da memória, e não numa terceira.
        //
        // Não é economia de linhas: o prefixo do prompt é lido pelo cache do Ollama de cima
        // para baixo, e cada bloco novo é mais uma coisa que pode mudar de tamanho e invalidar
        // tudo que vem depois. Uma mensagem só mantém a ordem estável — prompt de sistema,
        // depois o que muda devagar, depois a conversa viva.
        //
        // Vai DEPOIS da memória de propósito: fatos, atos e capítulos mudam quando um capítulo
        // nasce; a lista de anexos muda quando o usuário clica no "+", que é bem mais
        // frequente. O que muda mais fica mais perto do fim.
        string anexados = ContextService.RenderizarAnexados();
        if (anexados.Length > 0)
            bloco = bloco.Length > 0 ? bloco + "\n\n" + anexados : anexados;

        lock (_gate)
        {
            bool temBase = _history.Count > 0 && _history[0] is SystemChatMessage;

            // O custo do resumo so e contabilizado quando o bloco de fato ENTRA no prompt.
            //
            // Com "SendSystemPrompt" desligado nao ha onde ancorar a memoria e ela nunca e
            // enviada — mas o numero continuava sendo descontado do total do contador, que
            // entao ficava MENOR que o contexto e batia na guarda de sanidade. O efeito era a
            // economia sumir da tela para sempre, em toda conversa desses usuarios, sem
            // nenhum sinal de que algo estava errado.
            bool entrou = temBase && !string.IsNullOrEmpty(bloco);
            _tokensDeResumo = entrou && narrativa.Length > 0
                ? _tokenCounter.CountText(narrativa)
                : 0;

            if (!temBase) return; // sem prompt de sistema não há onde ancorar o bloco

            bool temMemoria = _history.Count > 1 && _history[1] is SystemChatMessage;

            if (string.IsNullOrEmpty(bloco))
            {
                if (temMemoria) _history.RemoveAt(1);
                return;
            }

            var mensagem = ChatMessage.CreateSystemMessage(bloco);
            if (temMemoria) _history[1] = mensagem;
            else _history.Insert(1, mensagem);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Turno do usuário
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Turno completo do usuário. Traduz os eventos do AgentLoop: texto de canal final vira
    /// chunk devolvido ao chamador, o resto vai para o callback técnico e para os eventos.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamItem> StreamResponseAsync(
        string userMessage,
        Action<string>? onTechnicalContent = null)
    {
        var settings = _settingsService.LoadSettings();
        int userLevel = LevelService.GetLevel(settings.MessageCount);

        // A espera pelo portão NÃO usa o token deste turno: o CTS só nasce depois que o
        // turno anterior liberou o portão. Trocar o CTS antes disso descartava, no meio do
        // voo, o token que o turno anterior ainda estava usando.
        await _turnGate.WaitAsync().ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        lock (_ctsGate)
        {
            _generationCts?.Dispose();
            _generationCts = cts;
        }
        var ct = cts.Token;

        // Remontado a cada turno por causa dos anexos: o usuário pode ter clicado no "+" entre
        // dois turnos, e esperar o próximo capítulo para o modelo saber do arquivo seria
        // esperar demais. Custa uma montagem de string; quando nada mudou, o texto sai
        // idêntico e o cache de prefixo não percebe diferença.
        RefreshMemoryMessage(CurrentQuota(userLevel));

        try
        {
            bool needsPrompt;
            lock (_gate) { needsPrompt = _history.Count == 0; }
            if (needsPrompt) ResetHistory();

            lock (_gate) { _history.Add(ChatMessage.CreateUserMessage(userMessage)); }

            // Atualiza o contador na UI assim que o usuário envia a mensagem.
            NotifyTokenCount(userLevel);

            var request = new AgentTurnRequest(
                this,
                _toolRegistry.GetActiveTools(userLevel),
                userLevel,
                ChatRequestOptions.Default,
                settings.VerboseConsoleLogging);

            await foreach (var evt in _agentLoop.RunAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case AgentEvent.Text text:
                        yield return new ChatStreamItem.Text(text.Value);
                        break;

                    case AgentEvent.Technical technical:
                        RaiseTechnical(onTechnicalContent, technical.Value);
                        break;

                    // O raciocínio segue para o console como sempre foi; o que ele ganha aqui
                    // é um item EM BANDA, para a tela saber que a geração começou sem precisar
                    // do texto pensado.
                    case AgentEvent.Reasoning reasoning:
                        RaiseTechnical(onTechnicalContent, reasoning.Value);
                        yield return new ChatStreamItem.Thinking();
                        break;

                    // O contador ao vivo do laco ja mede o contexto do turno em andamento; a
                    // parte compactada e a mesma o turno inteiro, e vem dos campos guardados.
                    case AgentEvent.TokenUsage usage:
                        RaiseTokenCount(Relatorio(usage.Total, usage.Max));
                        break;

                    case AgentEvent.TurnSegment segment:
                        yield return new ChatStreamItem.SegmentBreak(segment.Iteration);
                        break;

                    case AgentEvent.ToolStarted iniciada:
                        yield return new ChatStreamItem.ToolStarted(
                            iniciada.Id,
                            iniciada.Tool,
                            Memory.ArtifactExtractor.ResumirArgumento(iniciada.Tool, iniciada.Arguments));
                        break;

                    case AgentEvent.ToolFinished terminada:
                        yield return new ChatStreamItem.ToolFinished(
                            terminada.Id,
                            terminada.Tool,
                            terminada.Failed,
                            Memory.ArtifactExtractor.Recusado(terminada.Result),
                            terminada.Artifact,
                            terminada.Failed ? PrimeiraLinhaDoErro(terminada.Result) : null);
                        break;

                    case AgentEvent.Completed completed
                        when completed.Outcome == TurnOutcome.IterationLimitReached:
                        // O teto do ReAct nunca vira sucesso silencioso: o usuário vê o corte.
                        // O número vem do EVENTO, não de uma constante: o teto virou
                        // configuração, e uma constante aqui anunciaria 18 para quem tivesse
                        // configurado 30 — errando justamente na frase que explica o corte.
                        RaiseTechnical(onTechnicalContent,
                            $"[LOOP] Teto de {completed.IterationsUsed} iterações atingido — turno encerrado incompleto.");
                        yield return new ChatStreamItem.Text(
                            $"\n\n⚠️ Limite de {completed.IterationsUsed} etapas atingido — a tarefa ficou incompleta. Peça para continuar.");
                        break;
                }
            }
        }
        finally
        {
            // Lido ANTES do descarte: depois dele o token não pode mais ser consultado.
            bool cancelado = ct.IsCancellationRequested;

            lock (_ctsGate)
            {
                // Só descarta se ainda for o CTS deste turno; um Cancel concorrente já não
                // pode entrar, porque o Cancel também corre sob este lock.
                if (ReferenceEquals(_generationCts, cts))
                {
                    _generationCts = null;
                    cts.Dispose();
                }
            }

            // Antes de liberar o portão: o turno seguinte não pode começar a mexer no
            // histórico enquanto este ainda não foi registrado.
            RecordLastTurn();

            // A titulação vem ANTES da compactação, e cedo: ela usa um prefixo próprio e
            // derruba o cache do prefixo da conversa. Depois do primeiro turno o histórico é
            // pequeno e reconstruí-lo custa quase nada; mais tarde custaria caro.
            if (!cancelado)
                await TitularSeNecessarioAsync().ConfigureAwait(false);

            // Arquiva a conversa a cada turno, por cima da própria entrada.
            //
            // Antes isto só acontecia no ResetHistory — fechar a janela, começar conversa nova,
            // trocar de personagem. Uma conversa interrompida de qualquer outra forma (processo
            // encerrado à força, queda de energia, atualização que reinicia o app) sumia
            // inteira do histórico, e o usuário não tinha como saber que ela nunca chegou a ser
            // gravada. Um histórico que só existe se o programa for fechado do jeito certo não
            // é um histórico.
            ArquivarConversaViva();

            // Compactação só depois de um turno que terminou inteiro. Cancelado no meio, o
            // histórico pode ter um tool_calls pendente, e resumir metade de uma cadeia
            // produziria um capítulo que afirma o que ainda não aconteceu.
            // Token próprio: o do turno já foi descartado, e o usuário já tem sua resposta.
            if (!cancelado)
                await CompactIfNeededAsync(userLevel, CancellationToken.None).ConfigureAwait(false);

            _turnGate.Release();
        }
    }

    /// <summary>
    /// Grava a conversa viva no histórico, substituindo a própria entrada.
    /// <para>
    /// Regravar o arquivo inteiro a cada turno é aceitável porque ele guarda no máximo 50
    /// conversas e é lido pelo painel de uma vez só. A alternativa — anexar incrementalmente —
    /// exigiria um formato novo e um caminho de recuperação para arquivo cortado no meio.
    /// </para>
    /// </summary>
    private void ArquivarConversaViva()
    {
        try
        {
            List<ChatMessage> transcricao;
            lock (_gate) { transcricao = new List<ChatMessage>(_transcricao); }

            // O primeiro item faz o papel do prompt de sistema, que o arquivador descarta: sem
            // ele, uma conversa de um turno só teria duas mensagens e passaria pelo corte de
            // "só o prompt de sistema".
            transcricao.Insert(0, ChatMessage.CreateSystemMessage(""));

            ChatHistoryService.SaveCurrentSession(
                transcricao, Title, _sessionId, _sessionMemory.SessionId);
            OnHistoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Arquivar é acréscimo. Nada aqui pode escapar para o turno, que já terminou.
            Console.WriteLine($"[HISTORICO] Falha ao arquivar a conversa: {ex.Message}");
        }
    }

    /// <summary>Quanto a titulação pode demorar antes de ser abandonada.</summary>
    private static readonly TimeSpan TitleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Dá nome à conversa depois do primeiro turno completo.
    /// <para>
    /// Uma vez só, e cedo. Cedo porque a chamada usa prefixo próprio e derruba o cache da
    /// conversa: com um turno no histórico, reconstruir o prefixo é barato; com a janela cheia,
    /// não. Uma vez porque renomear a conversa embaixo do usuário, a cada turno, é pior do que
    /// um nome imperfeito.
    /// </para>
    /// <para>
    /// Falha, tempo esgotado ou resposta impublicável deixam o título como estava. Quem arquiva
    /// cai na heurística antiga — a primeira mensagem do usuário. Nada aqui pode estragar o
    /// turno, que já terminou.
    /// </para>
    /// </summary>
    private async Task TitularSeNecessarioAsync()
    {
        if (Title != null || _turnsRecorded != 1) return;

        try
        {
            var turnos = TurnSplitter.Split(Snapshot());
            if (turnos.Count == 0) return;

            var primeiro = turnos[0];
            string material = ChatTitler.Material(primeiro.UserText, primeiro.AssistantText);

            var settings = _settingsService.LoadSettings();
            var titulador = new ChatTitler(_providerFactory.GetProvider(settings));

            using var timeout = new CancellationTokenSource(TitleTimeout);
            string? titulo = await titulador.TitularAsync(material, timeout.Token).ConfigureAwait(false);

            DefinirTitulo(titulo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TITULO] Falha ao nomear a conversa: {ex.Message}");
        }
    }

    /// <summary>
    /// Renomeia a conversa quando o primeiro capítulo fecha.
    /// <para>
    /// O título do primeiro turno nomeia a INTENÇÃO inicial, que às vezes não é o assunto: uma
    /// conversa que começa em "por que isso não compila" pode terminar sendo a reforma de um
    /// subsistema. O resumo do capítulo já sabe disso.
    /// </para>
    /// <para>
    /// Aqui não custa cache: a compactação que acabou de rodar já invalidou o prefixo. Por isso
    /// esta é a única retitulação — em qualquer outro momento ela seria paga duas vezes.
    /// </para>
    /// </summary>
    private async Task RetitularPeloCapituloAsync(string resumo, CancellationToken ct)
    {
        if (_tituloRevisado) return;
        _tituloRevisado = true;

        if (string.IsNullOrWhiteSpace(resumo)) return;

        try
        {
            var settings = _settingsService.LoadSettings();
            var titulador = new ChatTitler(_providerFactory.GetProvider(settings));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TitleTimeout);

            string? titulo = await titulador.TitularAsync(resumo, timeout.Token).ConfigureAwait(false);
            DefinirTitulo(titulo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TITULO] Falha ao revisar o nome da conversa: {ex.Message}");
        }
    }

    private void DefinirTitulo(string? titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return;
        if (titulo == Title) return;

        Title = titulo;
        Console.WriteLine($"[TITULO] Conversa nomeada: {titulo}");
        OnTitleChanged?.Invoke(titulo);

        // O nome muda DEPOIS de a conversa já ter sido arquivada com o nome antigo (ou sem
        // nome). Regravar aqui é o que faz a lista mostrar o nome novo em vez do provisório.
        ArquivarConversaViva();
    }

    /// <summary>Resposta única e sem estado (Shadow Assistant, utilitários).</summary>
    public async Task<string> AskStatelessAsync(string systemPrompt, string userPrompt, string? overrideModel = null)
    {
        var settings = _settingsService.LoadSettings();
        if (!string.IsNullOrEmpty(overrideModel)) settings.ModelName = overrideModel!;

        var messages = new List<ChatMessage>();
        if (settings.SendSystemPrompt) messages.Add(ChatMessage.CreateSystemMessage(systemPrompt));
        messages.Add(ChatMessage.CreateUserMessage(userPrompt));

        await _turnGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var provider = _providerFactory.GetProvider(settings);
            var result = await provider
                .CompleteAsync(messages, Array.Empty<ChatTool>(), ChatRequestOptions.Default, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Text;
        }
        catch (Exception ex)
        {
            return $"[ERROR]: {ex.Message}";
        }
        finally
        {
            _turnGate.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IMessageStore — o histórico vivo
    // ─────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_gate) { return _history.ToArray(); }
    }

    public void AppendAssistantToolCalls(IReadOnlyList<ChatToolCall> calls)
    {
        lock (_gate) { _history.Add(ChatMessage.CreateAssistantMessage(calls)); }
    }

    public void AppendToolResult(string toolCallId, string result)
    {
        lock (_gate) { _history.Add(ChatMessage.CreateToolMessage(toolCallId, result)); }
    }

    public void AppendAssistantText(string text)
    {
        lock (_gate) { _history.Add(ChatMessage.CreateAssistantMessage(text)); }
    }

    public int CountTokens()
    {
        lock (_gate) { return _tokenCounter.CountMessages(_history); }
    }

    /// <summary>
    /// Poda por janela deslizante: enquanto o histórico estiver acima do orçamento do nível,
    /// remove a mensagem mais antiga que PODE ser removida. A âncora do índice 0 só é
    /// intocável quando é de fato o system prompt — com "SendSystemPrompt" desligado não
    /// existe system prompt, e travar o índice 0 tornaria a primeira mensagem do usuário
    /// imortal. Cuidado especial para não deixar um Assistant com tool_calls sem as
    /// ToolMessages correspondentes: a API rejeita o par órfão.
    /// </summary>
    /// <summary>
    /// Primeiro índice podável: logo depois do bloco de mensagens de sistema do começo.
    /// Com "SendSystemPrompt" desligado não existe system prompt e o resultado é 0 — travar
    /// o índice 0 nesse caso tornaria a primeira mensagem do usuário imortal.
    /// Chamar sempre sob <see cref="_gate"/>.
    /// </summary>
    private int FirstRemovableIndex()
    {
        int i = 0;
        while (i < _history.Count && _history[i] is SystemChatMessage) i++;
        return i;
    }

    public void Trim(int userLevel)
    {
        int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);

        lock (_gate)
        {
            // Pula TODAS as mensagens de sistema do início, não só a do índice 0: o bloco de
            // memória compactada é a segunda, e podá-lo apagaria justamente o resumo que
            // acabou de custar uma chamada ao modelo — e junto com ele os turnos que ele
            // substituiu, que já saíram do histórico vivo.
            int firstRemovable = FirstRemovableIndex();
            int currentTokens = _tokenCounter.CountMessages(_history);

            int safetyCounter = 0;
            while (currentTokens > maxTokens
                   && _history.Count > firstRemovable + 2
                   && safetyCounter++ < 200)
            {
                var msg = _history[firstRemovable];
                _history.RemoveAt(firstRemovable);

                // Se a mensagem removida era um Assistant com tool_calls, remove também as
                // ToolMessages imediatamente seguintes (são as respostas dessas tool_calls).
                if (msg is AssistantChatMessage acm && acm.ToolCalls != null && acm.ToolCalls.Count > 0)
                {
                    while (_history.Count > firstRemovable && _history[firstRemovable] is ToolChatMessage)
                        _history.RemoveAt(firstRemovable);
                }

                currentTokens = _tokenCounter.CountMessages(_history);
            }
        }
    }

    public void NotifyTokenCount(int userLevel, int? cachedTokens = null)
    {
        RaiseTokenCount(MontarRelatorio(userLevel));
    }

    /// <summary>
    /// As duas medidas do contexto.
    /// <para>
    /// O total NAO e a soma de tudo que ja passou pela conversa: mensagem cortada pela poda de
    /// emergencia nao entra. A poda descarta sem substituto, e credita-la aqui faria o sistema
    /// de capitulos parecer melhor justamente quando ele nao deu conta.
    /// </para>
    /// </summary>
    private TokenReport MontarRelatorio(int userLevel) =>
        Relatorio(CountTokens(), LevelService.GetMaxTokensForLevel(userLevel));

    private TokenReport Relatorio(int contexto, int max)
    {
        int total = contexto - _tokensDeResumo + _tokensCompactados;

        // Guarda de sanidade: sem nada compactado os dois numeros sao o mesmo. O bloco de
        // memoria pode existir so com fatos ou anexos, e nenhum dos dois entrou no lugar de
        // conversa — descontar por eles produziria um total MENOR que o contexto.
        if (total < contexto) total = contexto;

        return new TokenReport(total, contexto, max);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers privados
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Monta o system prompt contextual. Devolve null quando o setting "SendSystemPrompt"
    /// está desligado — caso de quem usa um Modelfile do Ollama com SYSTEM embutido e não
    /// quer duplicar instruções.
    /// </summary>
    private string? BuildSystemPrompt()
    {
        var settings = _settingsService.LoadSettings();
        if (!settings.SendSystemPrompt) return null;

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var contextualPrompt = SYSTEM_PROMPT + $"\n\nContexto Local:\n- Diretório Home do Usuário (Raiz): {userHome}";

        // A alma do personagem ativo vem ANTES das instruções operacionais: é ela que define
        // quem responde, e o resto do prompt define o que ele pode fazer.
        var soul = LoadActiveCharacterSoul(settings.ActiveCharacter);
        if (!string.IsNullOrWhiteSpace(soul))
            contextualPrompt = soul + "\n\n---\n\n" + contextualPrompt;

        try
        {
            var skills = SkillService.ListLocalSkills();
            if (skills.Count > 0)
            {
                contextualPrompt += "\n\nHabilidades dinâmicas disponíveis (use a ferramenta 'execute_skill' para chamá-las passando 'skill_name'):\n";
                // Skill de documentacao entra na lista como qualquer outra: chama-la devolve
                // o texto de instrucoes em vez de rodar um script, e isso e util — e como uma
                // skill ensina um procedimento sem automatiza-lo. Pular as de markdown deixava
                // instalada uma habilidade que o modelo nunca ficava sabendo que existia.
                foreach (var skill in skills)
                    contextualPrompt += $"- {skill.Name}: {skill.Description}\n";
            }
        }
        catch { }

        return contextualPrompt;
    }

    /// <summary>
    /// Lê o SOUL.MD do personagem ativo. A autoridade é a pasta do usuário
    /// (<see cref="DirectoryService.CharactersDir"/>); a pasta da instalação só entra como
    /// failsafe, para o caso de o personagem ainda não ter sido semeado.
    /// Devolve null quando não há alma para carregar — nunca lança.
    /// </summary>
    private static string? LoadActiveCharacterSoul(string? activeCharacter)
    {
        if (string.IsNullOrWhiteSpace(activeCharacter)) return null;

        try
        {
            // Só o nome da pasta: um ActiveCharacter corrompido não pode virar caminho arbitrário.
            string name = Path.GetFileName(activeCharacter.Trim());
            if (string.IsNullOrEmpty(name)) return null;

            string path = Path.Combine(DirectoryService.CharactersDir, name, "SOUL.MD");

            if (!File.Exists(path))
            {
                string? failsafe = DirectoryService.FailsafeCharactersDir();
                if (failsafe == null) return null;
                path = Path.Combine(failsafe, name, "SOUL.MD");
                if (!File.Exists(path)) return null;
                Console.WriteLine($"[CHARACTERS] SOUL.MD de '{name}' lido do failsafe (ausente em .AIB).");
            }

            string soul = File.ReadAllText(path);

            // Placeholder usado por todos os SOUL.MD e que até aqui nunca era substituído:
            // o modelo recebia a string "{{usuario}}" literal e a tratava como nome do usuário.
            return soul.Replace("{{usuario}}", Environment.UserName);
        }
        catch (Exception ex)
        {
            // Alma ilegível não pode derrubar a conversa: segue sem persona.
            Console.WriteLine($"[CHARACTERS ERRO] Falha ao carregar SOUL.MD: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Primeira linha útil de um resultado de erro, para caber no chip que falhou (§4.7).
    /// <para>
    /// Só a primeira: a saída de um comando que falhou costuma trazer stack trace inteiro, e o
    /// chip tem uma linha. O texto completo continua no histórico e no tooltip.
    /// </para>
    /// </summary>
    private static string? PrimeiraLinhaDoErro(string? resultado)
    {
        if (string.IsNullOrWhiteSpace(resultado)) return null;

        foreach (var linha in resultado.Split('\n'))
        {
            string limpa = linha.Trim();
            if (limpa.Length > 0) return limpa.Length > 160 ? limpa[..160] + "…" : limpa;
        }

        return null;
    }

    private void RaiseTechnical(Action<string>? callback, string value)
    {
        if (callback == null) return;
        Post(() => callback(value));
    }

    private void RaiseTokenCount(TokenReport relatorio)
    {
        var handler = OnTokenCountChanged;
        if (handler == null) return;
        Post(() => handler(relatorio));
    }

    private void RaiseWarmupState(bool warming)
    {
        var handler = OnWarmupStateChanged;
        if (handler == null) return;
        Post(() => handler(warming));
    }

    /// <summary>
    /// Devolve a chamada para o contexto capturado na construção. Sem contexto (testes,
    /// modo CLI) executa direto na thread corrente.
    /// </summary>
    private void Post(Action action)
    {
        if (_uiContext != null) _uiContext.Post(_ => action(), null);
        else action();
    }
}
