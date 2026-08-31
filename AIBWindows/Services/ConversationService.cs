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
        _warmupService.OnTokenCountChanged += (total, max, cached) => RaiseTokenCount(total, max, cached);

        // Popula o histórico inicial (System Prompt / SOUL) para já termos a métrica de
        // tokens. Nenhuma tarefa de fundo nasce daqui: o aquecimento é explícito.
        ResetHistory();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Eventos e superfície pública
    // ─────────────────────────────────────────────────────────────────────────

    public event Action<int, int, int?>? OnTokenCountChanged;
    public event Action<bool>? OnWarmupStateChanged;

    public ToolRegistry Registry => _toolRegistry;

    public int CurrentTokenCount => CountTokens();

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
        List<ChatMessage>? toSave = null;
        lock (_gate)
        {
            if (_history.Count > 1) toSave = new List<ChatMessage>(_history);
            _history.Clear();
        }

        // IO de disco fora do lock: nada bloqueante segura o histórico.
        if (toSave != null) ChatHistoryService.SaveCurrentSession(toSave);

        // Histórico zerado é sessão nova: pasta nova em memory/sessions e contagem de turnos
        // reiniciada. Continuar gravando na pasta anterior misturaria duas conversas num
        // raw.jsonl só, e o resumo de capítulo sairia costurando assuntos sem relação.
        if (toSave != null)
        {
            _sessionMemory = NewSessionMemory();
            _turnsRecorded = 0;
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

            // Turno aberto (cancelado no meio, ou teto de iterações com ferramenta pendente):
            // gravá-lo deixaria em disco um tool_calls sem resultado. Fica para a próxima —
            // as mensagens continuam no histórico vivo.
            if (!TurnSplitter.IsClosed(ultimo)) return;

            if (_sessionMemory.AppendTurn(ultimo with { Index = _turnsRecorded }))
                _turnsRecorded++;
        }
        catch (Exception ex)
        {
            // Registro é acréscimo. Nenhuma falha aqui pode escapar para o turno do usuário.
            Console.WriteLine($"[MEMORIA] Falha ao registrar o turno: {ex.Message}");
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
            int gatilho = MemoryBudget.CompactionThreshold(quota);
            if (vivo <= gatilho) return;

            var candidatos = SelectTurnsToCompact(quota, vivo);
            if (candidatos.Count == 0) return;

            Console.WriteLine($"[MEMORIA] Compactando {candidatos.Count} turno(s): vivo={vivo} > gatilho={gatilho}.");

            var settings = _settingsService.LoadSettings();
            var compactor = new Compactor(_providerFactory.GetProvider(settings));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SummaryTimeout);

            var capitulo = await compactor
                .SummarizeAsync(_memory.NextChapterIndex, candidatos, timeout.Token)
                .ConfigureAwait(false);

            // Só remove DEPOIS que o capítulo existe. Remover antes e falhar o resumo perderia
            // os turnos das duas pontas: fora do contexto e sem substituto.
            RemoveOldestMessages(candidatos.Sum(t => t.Messages.Count));

            _memory.Add(capitulo);
            _sessionMemory.AppendChapter(capitulo);

            // Promoção antes de reescrever o bloco: se um ato nascer agora, ele já entra no
            // mesmo prompt, e o prefixo é invalidado UMA vez em vez de duas.
            await PromoteIfNeededAsync(compactor, ct).ConfigureAwait(false);

            RefreshMemoryMessage(quota);

            Console.WriteLine($"[MEMORIA] Capítulo {capitulo.Index} fechado ({capitulo.Artifacts.Count} artefato(s)). Vivo agora: {LiveTokens()} tokens.");
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
    private async Task PromoteIfNeededAsync(Compactor compactor, CancellationToken ct)
    {
        try
        {
            var soltos = _memory.UncoveredChapters;
            if (soltos.Count < ChaptersPerAct) return;

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
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[MEMORIA] Promoção cancelada. Os capítulos seguem soltos.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Promoção falhou: {ex.Message}");
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

        return MemoryBudget.Compute(LevelService.GetMaxTokensForLevel(userLevel), prefixo);
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
    private List<Turn> SelectTurnsToCompact(MemoryQuota quota, int vivo)
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

        for (int i = 0; i < teto && restante > alvo; i++)
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

        string bloco = _memory.Render(quota, _tokenCounter);

        lock (_gate)
        {
            bool temBase = _history.Count > 0 && _history[0] is SystemChatMessage;
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

                    case AgentEvent.TokenUsage usage:
                        RaiseTokenCount(usage.Total, usage.Max, usage.Cached);
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
                            terminada.Failed,
                            Memory.ArtifactExtractor.Recusado(terminada.Result),
                            terminada.Artifact,
                            terminada.Failed ? PrimeiraLinhaDoErro(terminada.Result) : null);
                        break;

                    case AgentEvent.Completed completed
                        when completed.Outcome == TurnOutcome.IterationLimitReached:
                        // O teto do ReAct nunca vira sucesso silencioso: o usuário vê o corte.
                        RaiseTechnical(onTechnicalContent,
                            $"[LOOP] Teto de {AgentLoop.MaxIterations} iterações atingido — turno encerrado incompleto.");
                        yield return new ChatStreamItem.Text(
                            $"\n\n⚠️ Limite de {AgentLoop.MaxIterations} etapas atingido — a tarefa ficou incompleta. Peça para continuar.");
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

            // Compactação só depois de um turno que terminou inteiro. Cancelado no meio, o
            // histórico pode ter um tool_calls pendente, e resumir metade de uma cadeia
            // produziria um capítulo que afirma o que ainda não aconteceu.
            // Token próprio: o do turno já foi descartado, e o usuário já tem sua resposta.
            if (!cancelado)
                await CompactIfNeededAsync(userLevel, CancellationToken.None).ConfigureAwait(false);

            _turnGate.Release();
        }
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
        RaiseTokenCount(CountTokens(), LevelService.GetMaxTokensForLevel(userLevel), cachedTokens);
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
                foreach (var skill in skills)
                {
                    if (skill.Interpreter.Equals("markdown", StringComparison.OrdinalIgnoreCase)) continue;
                    contextualPrompt += $"- {skill.Name}: {skill.Description}\n";
                }
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

    private void RaiseTokenCount(int total, int max, int? cached)
    {
        var handler = OnTokenCountChanged;
        if (handler == null) return;
        Post(() => handler(total, max, cached));
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
