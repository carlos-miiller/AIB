using System;
using System.Windows;
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification;
using NHotkey;
using NHotkey.Wpf;
using AIB.Views;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Ai;

namespace AIB;

public partial class App : System.Windows.Application
{
    private TaskbarIcon? _notifyIcon;
    private ChatWindow? _chatWindow;
    private ShadowAssistantWindow? _orbe;

    /// <summary>O item da bandeja que liga o orbe. Guardado para não sair de sincronia.</summary>
    private System.Windows.Controls.MenuItem? _itemDoOrbe;

    /// <summary>
    /// O vigia de e-mail. Vive enquanto o programa vive e decide sozinho quando trabalhar — a
    /// chave que o liga é lida a cada batida, não capturada aqui.
    /// </summary>
    private Services.Mail.MailDigestService? _vigia;

    /// <summary>
    /// O espelho do console em arquivo. Ligado o mais cedo possível: o que interessa depurar
    /// costuma acontecer no arranque, e um registro que começa depois perde justamente isso.
    /// </summary>
    private RegistroDeExecucao? _registro;

    // Composition root: os serviços são construídos aqui, uma única vez, e injetados.
    // O SettingsService precisa nascer DEPOIS de EnsureDirectories para enxergar o caminho certo.
    private readonly System.Net.Http.HttpClient _httpClient = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private SettingsService _settingsService = null!;
    private ToolRegistry _toolRegistry = null!;
    private ChatConfirmationPrompt _confirmationPrompt = null!;
    private TokenCounter _tokenCounter = null!;
    private IToolCallHealer _healer = null!;
    private IChatProviderFactory _providerFactory = null!;
    private AgentLoop _agentLoop = null!;
    private ConversationService _conversation = null!;
    private ConversaDoOrbe _conversaDoOrbe = null!;

    /// <summary>
    /// Liga e desliga o orbe, gravando a escolha. É a porta da bandeja; a outra é a página
    /// Shadow das configurações, e as duas desembocam em <see cref="SincronizarOrbe"/>.
    /// </summary>
    private void AlternarOrbe(bool ligado)
    {
        var settings = _settingsService.LoadSettings();
        settings.ShadowAssistantEnabled = ligado;
        _settingsService.SaveSettings(settings);

        SincronizarOrbe();
    }

    /// <summary>
    /// Põe o orbe no estado que a configuração manda, agora.
    /// <para>
    /// Existe porque a mesma chave passou a ter DUAS portas: a bandeja e a página Shadow da
    /// tela de configurações. Sem um lugar só aplicando a decisão, salvar nas configurações
    /// gravaria o arquivo e não mudaria nada na tela até o próximo arranque — a chave pareceria
    /// quebrada — e o visto da bandeja continuaria contando a história antiga.
    /// </para>
    /// </summary>
    public static void AplicarEstadoDoOrbe()
    {
        if (Current is App app) app.SincronizarOrbe();
    }

    /// <summary>O que a iniciativa aprendeu, para a página Shadow. Vazio antes de ela existir.</summary>
    public static string ResumoDaIniciativa() =>
        Current is App { _iniciativa: not null } app ? Iniciativa.Resumo(app._iniciativa.Estado) : "";

    /// <summary>Zera o aprendizado da iniciativa, pela instância viva (ela regrava o arquivo).</summary>
    public static void ZerarIniciativa()
    {
        if (Current is App { _iniciativa: not null } app) app._iniciativa.Zerar();
    }

    private void SincronizarOrbe()
    {
        bool ligado = _settingsService.LoadSettings().ShadowAssistantEnabled;

        if (!ligado)
        {
            SoltarOrbe();
            _orbe?.Close();
            _orbe = null;
            if (_itemDoOrbe != null) _itemDoOrbe.IsChecked = false;
            return;
        }

        _orbe ??= CriarOrbe();

        // Esconder, e não fechar: fechado, o Shadow perderia a pilha de falas e a posição que o
        // usuário escolheu, e voltaria zerado toda vez que a conversa fosse aberta.
        if (ShadowAssistantWindow.DeveAparecer(true, _chatWindow?.IsVisible == true))
            _orbe.Show();
        else
            _orbe.Hide();

        if (_itemDoOrbe != null) _itemDoOrbe.IsChecked = true;
    }

    /// <summary>
    /// Monta o orbe ja ligado a janela de chat. O nome do personagem vem daqui porque a
    /// janela do orbe nao conhece SettingsService — ela desenha, e quem sabe quem esta ativo
    /// e quem a criou.
    /// </summary>
    private ShadowAssistantWindow CriarOrbe()
    {
        var configuracoes = _settingsService.LoadSettings();

        var orbe = new ShadowAssistantWindow
        {
            NomeDoAgente = configuracoes.ActiveCharacter,
            TetoDeEmails = configuracoes.ShadowMailPreviewCount
        };

        // "Leu e não respondeu" pesa menos que "nem viu" no aprendizado da iniciativa.
        orbe.FalasLidas += () => _iniciativa?.Leu();

        // Solta a ligação do orbe ANTERIOR antes de criar a do novo. Sem isto, desligar e
        // religar o orbe deixava a conversa com duas assinaturas: a do orbe vivo e a do orbe
        // fechado, que continuava recebendo o fim de cada turno e mexendo numa janela morta.
        SoltarOrbe();

        if (_chatWindow != null) _soltarOrbe = LigarOrbe(_chatWindow, orbe, _conversaDoOrbe);

        return orbe;
    }

    /// <summary>Como soltar a ligação do orbe que está de pé. Nulo quando não há orbe.</summary>
    private Action? _soltarOrbe;

    private void SoltarOrbe()
    {
        _soltarOrbe?.Invoke();
        _soltarOrbe = null;
    }

    /// <summary>
    /// Liga um orbe a uma conversa e devolve COMO SOLTÁ-LOS.
    /// <para>
    /// A ligação tem dois sentidos. Do orbe para a conversa: a barra manda a mensagem e o turno
    /// inteiro roda ESCONDIDO na janela de chat — laço de stream, ferramentas, portão de
    /// confirmação, histórico e XP acontecem lá, como sempre. Duas implementações de turno
    /// divergiriam em qual delas grava o quê, e o usuário acabaria com metade da conversa em
    /// cada lugar.
    /// </para>
    /// <para>
    /// Da conversa para o orbe: o orbe é a JANELA DO QUE A AIB ESTÁ FAZENDO. Ele acende o anel
    /// no passo corrente de qualquer turno — inclusive dos digitados na conversa — e só REPETE
    /// a fala quando ela não tem outro lugar onde aparecer (ver
    /// <see cref="ShadowAssistantWindow.OrbeDeveFalar"/>). Com a conversa do orbe ligada, ele
    /// não repete NENHUMA fala da janela: as duas conversas são apartadas, e a pilha é só da dele.
    /// </para>
    /// <para>
    /// Devolve o desfazer, e é estático, porque a conversa VIVE MAIS que o orbe: ela nasce no
    /// arranque e morre com o programa, enquanto o orbe é fechado e recriado a cada vez que a
    /// chave é desligada e religada. Assinar sem guardar como soltar foi o que deixou orbes
    /// fechados recebendo o fim de cada turno. Estático também para o ensaio conferir a
    /// não-duplicação sem subir o App inteiro.
    /// </para>
    /// </summary>
    /// <summary>Quantas falas da conversa do orbe voltam à pilha no arranque.</summary>
    public const int FalasAoReabrir = 12;

    /// <param name="doOrbe">
    /// A conversa própria do orbe. Com ela, a barra fala com ela, e não com a janela. Nula nos
    /// ensaios antigos: aí a barra cai na conversa principal, escondida, como era antes.
    /// </param>
    public static Action LigarOrbe(ChatWindow conversa, ShadowAssistantWindow orbe, ConversaDoOrbe? doOrbe = null)
    {
        void Enviou(string texto)
        {
            if (doOrbe != null) _ = doOrbe.EnviarAsync(texto);
            else conversa.AbrirComMensagem(texto, mostrarJanela: false);
        }

        // O turno da conversa do orbe: o passo acende o anel, e a resposta volta para a barra.
        void AndouNoOrbe(string passo, string? ferramenta) => orbe.MostrarEstado(passo, ferramenta);
        void RespondeuNoOrbe(string texto) => orbe.ResponderTurno(texto);

        if (doOrbe != null)
        {
            doOrbe.PassoMudou += AndouNoOrbe;
            doOrbe.Respondeu += RespondeuNoOrbe;

            // A conversa do orbe continua de onde parou, e a pilha também: reiniciado o AIB, o
            // modelo lembrava de tudo e a barra abria vazia, como se a conversa tivesse sumido.
            orbe.RestaurarFalas(doOrbe.Conversa.UltimasFalas(FalasAoReabrir, int.MaxValue));
        }

        // O clique num item da pilha leva ao MESMO lugar que a lista da área central: o e-mail
        // entra na conversa, e a janela vem à frente. Antes o item era desenho — clicar nele
        // não fazia nada.
        void EscolheuEmail(MailSummary item) => conversa.AbrirEmailDoOrbe(item);

        void Andou(string passo, string? ferramenta) => orbe.MostrarEstado(passo, ferramenta);

        void Concluiu(string texto)
        {
            // Com conversa própria, a pilha do orbe é DELA: a resposta da janela entrava ali
            // quando a janela era fechada no meio do turno, misturada a outra conversa — e
            // responder a ela pela barra ia para um modelo que nunca a tinha dito.
            if (doOrbe != null) return;

            // O anel já parou: o passo vazio chega imediatamente antes deste evento. O que
            // sobra decidir aqui é só se a resposta tem de ser DITA outra vez no orbe.
            if (ShadowAssistantWindow.OrbeDeveFalar(conversa.TurnoVeioDoOrbe, conversa.IsVisible))
                orbe.ResponderTurno(texto);
        }

        orbe.MensagemEnviada += Enviou;
        orbe.EmailEscolhido += EscolheuEmail;
        conversa.PassoDoTurnoMudou += Andou;
        conversa.TurnoConcluido += Concluiu;

        return () =>
        {
            if (doOrbe != null)
            {
                doOrbe.PassoMudou -= AndouNoOrbe;
                doOrbe.Respondeu -= RespondeuNoOrbe;
            }
            orbe.MensagemEnviada -= Enviou;
            orbe.EmailEscolhido -= EscolheuEmail;
            conversa.PassoDoTurnoMudou -= Andou;
            conversa.TurnoConcluido -= Concluiu;
        };
    }

    /// <summary>
    /// Um digest ficou pronto: põe o resultado onde houver superfície para ele.
    /// <para>
    /// A TRIAGEM É INDEPENDENTE DO ORBE — o orbe só mostra. Quem ligou "triar os e-mails" não
    /// pediu, junto, uma bola no desktop, e a chave da triagem funciona com ele desligado. O
    /// que não pode acontecer é o trabalho rodar sem aparecer em lugar nenhum, que era o caso:
    /// com o orbe fora, o digest acontecia e morria ali.
    /// </para>
    /// <para>
    /// As superfícies já existem, e são estas duas: a lista de e-mails da janela de conversa e
    /// o aviso da bandeja. Nenhuma tela nova.
    /// </para>
    /// </summary>
    private void AnunciarDigesto(Services.Mail.DigestoDeEmail digesto)
    {
        // A lista é montada ao ENTRAR no modo e-mail. Com a caixa já na tela, a passada nova
        // não aparecia até o usuário sair do modo e voltar.
        _chatWindow?.AtualizarCaixaDeEntrada();

        if (_orbe != null)
        {
            // RAJADA é o incidente aberto — doze alertas do mesmo monitor em quarenta minutos —
            // e é o que pinta o pulso de vermelho. O argumento existia no orbe desde o começo e
            // nunca chegava até aqui: o pulso vermelho era código que não rodava.
            _orbe.TerminarDeProcessarEmail(digesto.Frase(), digesto.Itens,
                                           urgente: digesto.Rajadas.Count > 0);
            return;
        }

        // Sem orbe não há pulso, e o pulso era o único aviso. A bandeja é o que sobra, e é a
        // mesma porta que o fim de turno com a janela escondida já usa.
        ShowNotification("AIB", digesto.Frase());
    }

    /// <summary>
    /// Roda um digest agora, a pedido. Mesmo caminho do laço — nada de uma segunda
    /// implementação que divergiria em qual das duas grava o progresso.
    /// </summary>
    private async System.Threading.Tasks.Task RodarDigestAgoraAsync()
    {
        if (_vigia == null) return;

        if (!_settingsService.LoadSettings().ShadowHandlesMail)
        {
            ShowNotification("AIB", "Ligue a triagem de e-mail em Configurações > Shadow.");
            return;
        }

        try
        {
            await _vigia.ExecutarAsync(comModelo: true, System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] digest sob demanda falhou — {ex.Message}");
        }
    }

    private Lembretes _lembretes = null!;
    private System.Windows.Threading.DispatcherTimer? _agenda;

    /// <summary>
    /// A agenda dos lembretes: a cada 20 s entrega os vencidos. Não chama o modelo — o texto foi
    /// escrito pela persona quando o lembrete foi pedido.
    /// </summary>
    private void IniciarAgenda()
    {
        _lembretes = new Lembretes();
        string personagem = _settingsService.LoadSettings().ActiveCharacter;
        _iniciativa = new Iniciativa(personagem: personagem, temperamento: TemperamentoDe(personagem));

        // Conversa na janela: só deixa a hora como recente — ele está ocupado com outra coisa,
        // não respondendo a ela.
        _chatWindow!.TurnoConcluido += _ => _ultimaConversaUtc = DateTime.UtcNow;

        // Conversa no orbe: é a resposta à iniciativa, se havia uma esperando — e quanto ele
        // conversou, e se foi um "agora não", é o que ela aprende. Sem fala esperando, é ele
        // puxando conversa, e isso também sobe a chance.
        _conversaDoOrbe.UsuarioFalou += texto =>
        {
            _ultimaConversaUtc = DateTime.UtcNow;
            AcompanharPersonagem(_settingsService.LoadSettings().ActiveCharacter);
            _iniciativa.UsuarioFalou(DateTime.UtcNow, texto);
        };

        _agenda = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _agenda.Tick += (_, _) =>
        {
            EntregarLembretes();
            _ = TalvezPuxarAssuntoAsync();
            _ = _conversaDoOrbe.CompactarSeParadaAsync(DateTime.UtcNow);
        };
        _agenda.Start();
        Exit += (_, _) => _agenda?.Stop();

        EntregarLembretes();
    }

    private Iniciativa _iniciativa = null!;
    private DateTime? _ultimaConversaUtc;
    private bool _ponderando;

    private static Temperamento TemperamentoDe(string? personagem) =>
        Temperamento.De(ConversationService.PerfilDoPersonagem(personagem)?.Temperament);

    /// <summary>
    /// O vínculo é por personagem: trocado nas configurações, a iniciativa passa a usar o do
    /// novo, com o temperamento dele. Só lê o info.json quando o nome muda.
    /// </summary>
    private void AcompanharPersonagem(string? personagem)
    {
        string nome = (personagem ?? "").Trim();
        if (nome != _iniciativa.Personagem) _iniciativa.Trocar(nome, TemperamentoDe(nome));
    }

    /// <summary>
    /// A cada batida, vê se a persona pode puxar assunto; podendo, sorteia (de 10 em 10 min) e,
    /// acertando, pergunta ao modelo se ela quer, e o quê. As condições e o sorteio são baratos e
    /// vêm antes: só a ponderação custa.
    /// <para>
    /// Só com o orbe na tela. Sem ele a fala viraria aviso da bandeja com bipe, e conversa solta
    /// apitando no canto da tela não é companhia, é interrupção. Lembrete pedido não depende disso.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task TalvezPuxarAssuntoAsync()
    {
        if (_ponderando || _chatWindow == null) return;

        var s = _settingsService.LoadSettings();
        if (!s.IniciativaLigada) return;

        AcompanharPersonagem(s.ActiveCharacter);

        var agora = DateTime.Now;
        var utc = DateTime.UtcNow;
        _iniciativa.NovoDia(agora);
        _iniciativa.Classificar(utc);

        bool livre = _orbe != null && !_chatWindow.Ocupada && !_chatWindow.IsVisible && !_conversaDoOrbe.Ocupada;
        string? impedimento = Iniciativa.Impedimento(
            _iniciativa.Estado, utc, agora.TimeOfDay,
            Iniciativa.Hora(s.SilencioInicio) ?? TimeSpan.FromHours(22),
            Iniciativa.Hora(s.SilencioFim) ?? TimeSpan.FromHours(8),
            livre && Presenca.Disponivel(), livre, _ultimaConversaUtc, s.MensagensPorDia);
        if (impedimento != null) return;

        if (!_iniciativa.Sortear(utc, agora.TimeOfDay, Random.Shared.NextDouble())) return;

        _ponderando = true;
        try
        {
            // O sorteio decidiu que ela fala; o código escolhe o gancho, e ela só escreve.
            var conversa = _conversaDoOrbe.Conversa;
            var (pendencias, fatos) = conversa.GanchosDisponiveis();
            var gancho = Iniciativa.EscolherGancho(pendencias, fatos, _iniciativa.Estado.GanchosRecentes, Random.Shared);

            var contexto = new ConversationService.ContextoDaIniciativa(
                s.NomeDoUsuario,
                new[] { _ultimaConversaUtc, _conversaDoOrbe.UltimaAtividadeUtc }.Max(),
                Proximos: _iniciativa.Estado.Geral >= Iniciativa.Proximidade,
                _iniciativa.Estado.UltimoDesfecho,
                _iniciativa.Estado.Recentes.ToList());

            string? fala = await conversa.EscreverIniciativaAsync(gancho, contexto, System.Threading.CancellationToken.None);

            // Enquanto ela escrevia, o usuário pode ter começado a falar com ela.
            if (fala != null && _conversaDoOrbe.Ocupada) fala = null;

            _iniciativa.Falou(DateTime.UtcNow, DateTime.Now.TimeOfDay, fala, gancho);
            Console.WriteLine($"[INICIATIVA] {(fala == null ? "sem mensagem" : "falou")} "
                              + $"(gancho: {gancho?.Tipo ?? "conhecer"}; geral {_iniciativa.Estado.Geral:0.00}, "
                              + $"chance {Iniciativa.Chance(_iniciativa.Estado, agora.TimeOfDay):P1}).");

            if (fala != null) FalarPorIniciativa(fala);
        }
        finally
        {
            _ponderando = false;
        }
    }

    private void EntregarLembretes()
    {
        // Com turno rodando, espera a próxima batida: a fala entraria no histórico no meio do
        // turno, entre a chamada de uma ferramenta e o resultado dela.
        if (_chatWindow == null || _chatWindow.Ocupada || _conversaDoOrbe.Ocupada) return;

        var agora = DateTime.UtcNow;
        foreach (var lembrete in _lembretes.Retirar(agora))
            FalarPorIniciativa(Lembretes.Atrasado(lembrete, agora));
    }

    /// <summary>
    /// A persona fala sem ter sido chamada. A fala entra na conversa (para a resposta continuar
    /// o assunto) e, com a conversa fora da tela, avisa pelo orbe — pulso, o texto só no clique —
    /// ou, sem orbe, pela bandeja.
    /// </summary>
    private void FalarPorIniciativa(string texto)
    {
        if (_chatWindow == null || string.IsNullOrWhiteSpace(texto)) return;

        // Com orbe: a fala é da conversa DELE, e espera no pulso. A responder pela barra
        // continua o assunto ali.
        if (_orbe != null)
        {
            _conversaDoOrbe.ReceberFalaPropria(texto);
            _orbe.EnfileirarFala(QuebraDeFala.Limpar(texto));
            return;
        }

        // Sem orbe (só lembrete chega aqui): na conversa da janela, e pela bandeja se ela
        // estiver fechada.
        _chatWindow.ReceberIniciativa(texto);
        if (!_chatWindow.IsVisible)
            ShowNotification(_settingsService.LoadSettings().ActiveCharacter ?? "AIB", QuebraDeFala.Limpar(texto));
    }

    public void ShowNotification(string title, string message)
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.ShowBalloonTip(title, message, BalloonIcon.Info);
            System.Media.SystemSounds.Beep.Play();
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Chamado pelo ssh/git como askpass (ver AskpassServidor): não é o app subindo, é um
        // mensageiro que leva o prompt à instância que rodou o comando e devolve a resposta.
        // Antes de tudo — nada de diretório, registro ou janela para ele.
        if (Services.Terminal.AskpassCliente.EstaNoModoAskpass(e.Args))
        {
            Environment.Exit(Services.Terminal.AskpassCliente.Rodar(e.Args));
            return;
        }

        base.OnStartup(e);

        try
        {
            DirectoryService.EnsureDirectories();
            _settingsService = new SettingsService();

            // Antes de qualquer coisa interessante acontecer. O que se precisa depurar mora no
            // arranque — carga de modelo, registro de ferramentas, primeira leitura da caixa —,
            // e um registro que comeca depois perde justamente isso.
            _registro = RegistroDeExecucao.Iniciar(_settingsService.LoadSettings());
            Exit += (_, _) => _registro?.Dispose();

            var settings = _settingsService.LoadSettings();

            // O tema antes de qualquer janela: as cores são StaticResource, resolvidas quando
            // cada tela é criada. Trocar o tema vale no próximo arranque.
            AIB.Ui.Tema.Carregar(Resources, AIB.Ui.Tema.Efetivo(settings.Tema, AIB.Ui.Tema.WindowsUsaClaro()));

            DirectoryService.ApplyFromSettings(settings);
            // ApplyFromSettings pode ter movido o diretório de dados: o cache aponta para o caminho antigo.
            _settingsService.InvalidateCache();

            // Portão humano ligado aqui: é o único lugar do app onde existe UI para pedir
            // autorização. Sem este argumento o registry recusa toda ferramenta destrutiva.
            //
            // A pergunta agora acontece DENTRO da conversa (§5.3 da spec de chat), e não numa
            // janela modal. Quem apresenta é a ChatWindow, que se conecta logo abaixo; até lá
            // — e se ela morrer — o prompt recusa por padrão.
            _confirmationPrompt = new ChatConfirmationPrompt();

            // Prompt de senha dos comandos do shell (ssh, scp, git): abre a janela do AIB, e o
            // que o usuário digita vai direto para o programa, sem passar pelo modelo.
            // As janelas de pergunta (confirmação, senha) aparecem logo acima do orbe quando ele
            // está na tela. Lido na hora: o orbe é recriado quando a chave é religada.
            PertoDoOrbe.Ancora = () => _orbe?.RetanguloDoOrbe();

            Services.Terminal.AskpassServidor.Padrao = new Services.Terminal.AskpassServidor(
                pedido => Dispatcher.InvokeAsync(() => SenhaDoTerminalDialog.Perguntar(pedido)).Task);
            _toolRegistry = new ToolRegistry(_confirmationPrompt, _settingsService);
            _tokenCounter = new TokenCounter();
            _healer = new RegexToolCallHealer();
            _providerFactory = new ChatProviderFactory(_httpClient, _healer);
            _agentLoop = new AgentLoop(_toolRegistry, _providerFactory, _settingsService, _tokenCounter);
            _conversation = new ConversationService(_settingsService, _toolRegistry, _agentLoop,
                                                    _tokenCounter, _providerFactory);

            // A conversa do orbe: separada da janela, uma só para sempre (ConversaDoOrbe). Laço
            // de agente próprio — o rastreador de prefixo dele é por conversa.
            _conversaDoOrbe = new ConversaDoOrbe(
                new ConversationService(_settingsService, _toolRegistry,
                    new AgentLoop(_toolRegistry, _providerFactory, _settingsService, _tokenCounter),
                    _tokenCounter, _providerFactory, sessaoFixa: ConversaDoOrbe.Sessao),
                _settingsService);

            _chatWindow = new ChatWindow(_conversation, _settingsService);

            // O Shadow some enquanto a conversa está na tela e volta quando ela sai — pelo
            // atalho global, pela bandeja ou por perder o foco. Escutar a VISIBILIDADE, e não
            // cada um desses gestos, é o que garante que os três caminhos concordem.
            _chatWindow.IsVisibleChanged += (_, _) => SincronizarOrbe();

            // A partir daqui o portão tem onde perguntar. Antes desta linha, e depois que a
            // janela morrer, ele recusa por padrão.
            _confirmationPrompt.Conectar(_chatWindow.PerguntarConfirmacaoAsync);

            // MainWindow explícito. Sem atribuir, o WPF elege a primeira janela criada — que pode
            // ser a FirstRunWindow já fechada, e qualquer diálogo que a use como Owner lança.
            MainWindow = _chatWindow;

            // ── O vigia de e-mail ────────────────────────────────────────
            // Ele é quem faz a leitura virar produto: roda com a janela fechada, três vezes ao
            // dia com o modelo e de vinte em vinte minutos só com código. Nasce sempre; quem
            // decide se ele trabalha é a chave "Deixar a AIB triar os e-mails", lida a cada
            // batida. Ligá-lo condicionalmente aqui faria a chave só valer no próximo arranque.
            _vigia = new Services.Mail.MailDigestService(
                _settingsService,
                new Services.Mail.MailKitMailService(settings.MailTimeoutSeconds),
                _providerFactory);

            // Os eventos chegam do relógio, fora da thread de interface.
            _vigia.Trabalhando += () =>
                Dispatcher.BeginInvoke(new Action(() => _orbe?.ComecarAProcessarEmail()));

            _vigia.Pronto += digesto =>
                Dispatcher.BeginInvoke(new Action(() => AnunciarDigesto(digesto)));

            // Pronto não apaga o anel: ele só fala quando há o que dizer, e a passada calada é
            // o caso comum. Este é o par do Trabalhando, e chega DEPOIS do Pronto quando os
            // dois disparam — por isso ele não pode mexer no que o Pronto deixou.
            _vigia.Terminou += () =>
                Dispatcher.BeginInvoke(new Action(() => _orbe?.PararDeProcessarEmail()));

            // O modo e-mail da área central mostra o último digest. A conversa não conhece o
            // vigia: ela só repassa as duas funções.
            _chatWindow.FonteDeEmails = () => _vigia?.Ultimo.Itens
                                              ?? (System.Collections.Generic.IReadOnlyList<MailSummary>)
                                                 Array.Empty<MailSummary>();

            // "Ignorar" (§3.10): tira a conversa da tela até chegar mensagem nova. Não toca o
            // servidor — a caixa continua somente leitura.
            _chatWindow.IgnorarEmail = item => _vigia?.Ignorar(item);

            // "Recarregar" (§3.11): relê UMA conversa no servidor e refaz o resumo. Sem vigia
            // não há o que reler, e a janela avisa em vez de fingir que releu.
            _chatWindow.RecarregarEmail = (item, ct) =>
                _vigia == null
                    ? System.Threading.Tasks.Task.FromResult<MailSummary?>(null)
                    : _vigia.RecarregarItemAsync(item, ct);

            // ANTES de ligar o laço: a tela tem de abrir com o que já se sabe. A primeira
            // sondagem só acontece minutos depois, e a partir do ponteiro de UID — sem isto, a
            // caixa passava esse intervalo vazia e, quando a passada vinha, só trazia mensagem
            // NOVA. O que já tinha sido triado nunca mais voltava para a tela.
            _vigia.Reconstituir();

            _vigia.Iniciar();
            Exit += (_, _) => _vigia?.Dispose();

            // O orbe do Shadow Assistant. Opt-in: a setting ja nascia false, e ela continua
            // mandando — quem nao ligou nao ganha uma bola nova sobre o desktop depois de
            // atualizar. Aqui ele so nasce na abertura; ligar e desligar depois passa por
            // AplicarEstadoDoOrbe (configuracoes) e AlternarOrbe (bandeja), e e nele que a
            // triagem de e-mail mostra o digest.
            if (settings.ShadowAssistantEnabled)
            {
                _orbe = CriarOrbe();
                SincronizarOrbe();
            }

            _notifyIcon = new TaskbarIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                ToolTipText = "AIB (Ctrl+Shift+Space)"
            };

            var contextMenu = new System.Windows.Controls.ContextMenu();

            var openItem = new System.Windows.Controls.MenuItem { Header = "✦ Abrir Chat" };
            openItem.Click += (s, ev) => AlternarChat();

            var orbeItem = new System.Windows.Controls.MenuItem
            {
                Header = "✦ Shadow no desktop",
                IsCheckable = true,
                IsChecked = _orbe != null
            };
            orbeItem.Click += (s, ev) => AlternarOrbe(orbeItem.IsChecked);
            _itemDoOrbe = orbeItem;

            // Era um andaime que simulava um digest. Agora o vigia existe, e o gesto passa a
            // rodar o digest DE VERDADE — quem não quer esperar as 12h55 tem por onde pedir.
            var digestItem = new System.Windows.Controls.MenuItem { Header = "Ler os e-mails agora" };
            digestItem.Click += (s, ev) => _ = RodarDigestAgoraAsync();

            var exitItem = new System.Windows.Controls.MenuItem { Header = "Sair" };
            exitItem.Click += (s, ev) => Current.Shutdown();

            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(orbeItem);
            contextMenu.Items.Add(digestItem);
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenu = contextMenu;
            _notifyIcon.TrayLeftMouseDown += (s, ev) => AlternarChat();

            try
            {
                HotkeyManager.Current.AddOrReplace(
                    "ToggleChat",
                    Key.Space,
                    ModifierKeys.Control | ModifierKeys.Shift,
                    OnHotkeyDetected);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HOTKEY] Não foi possível registrar o atalho global: {ex.Message}");
            }

            // Depois do orbe e da bandeja: o primeiro passe já entrega os lembretes que venceram
            // com o app fechado, e eles precisam de onde aparecer.
            IniciarAgenda();

            // Aquecimento só depois que a UI existe — nunca de dentro de um construtor.
            _ = _conversation.StartWarmupAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Erro crítico ao iniciar o AIB:\n\n{ex.Message}",
                "AIB — Erro de Inicialização",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    private void OnHotkeyDetected(object? sender, HotkeyEventArgs e)
    {
        e.Handled = true;
        AlternarChat();
    }

    /// <summary>Se o primeiro arranque está aberto — ver <see cref="AlternarChat"/>.</summary>
    private bool _primeiroArranqueAberto;

    /// <summary>
    /// A ÚNICA porta para o chat: atalho, "Abrir Chat" da bandeja e clique no ícone.
    /// <para>
    /// Só o atalho checava o primeiro arranque (D-01 + D-03 + D-08). Pela bandeja, o chat abria
    /// sem provedor escolhido ou sem a chave do OpenRouter no cofre, e o primeiro turno falhava
    /// com um 401 em vez de levar à tela que resolve. Três portas com a mesma checagem copiada
    /// voltariam a divergir; uma função não.
    /// </para>
    /// <para>
    /// O primeiro arranque é modal, mas o ícone da bandeja não é da janela: sem a guarda, um
    /// segundo clique com ele aberto abria outro por cima.
    /// </para>
    /// </summary>
    private void AlternarChat()
    {
        if (_primeiroArranqueAberto) return;

        if (NeedsFirstRun(_settingsService.LoadSettings()))
        {
            _primeiroArranqueAberto = true;
            try { ShowFirstRunWindow(); }
            finally { _primeiroArranqueAberto = false; }
            return;
        }

        _chatWindow?.ToggleWindow();
    }

    private static bool NeedsFirstRun(UserAppSettings s)
    {
        // Sem provedor escolhido: nunca passou pelo primeiro arranque.
        if (string.IsNullOrEmpty(s.AiProvider)) return true;

        // Provedor com chave e sem a chave DELE no cofre. Leitura estrita: a busca global do
        // cofre acharia a chave de outro serviço e daria o arranque por concluído.
        string? sistema = ProvedoresDeIa.SistemaDaChave(s.AiProvider);
        return sistema != null && CredentialService.LerDoSistema(sistema, ProvedoresDeIa.NomeDaChave) == null;
    }

    private void ShowFirstRunWindow()
    {
        var win = new FirstRunWindow(_settingsService);
        bool? ok = win.ShowDialog();
        if (ok == true)
        {
            // D-08 sequencing — ChatWindow only appears after Save.
            _chatWindow?.ToggleWindow();
        }
        else
        {
            // D-04 + Pitfall 6 — parent owns shutdown on UI thread; window only set DialogResult.
            _ = AuditLogService.AppendAsync(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                outcome = "firstrun_cancelled"
            });
            Current.Shutdown();
        }
    }

    /// <summary>
    /// Fecha o que o próprio AIB mantém aberto em <c>~/.AIB</c>, para o reset de fábrica poder
    /// apagar: o Edge do navegador segura o perfil inteiro, e o registro de execução segura o
    /// arquivo dele em <c>logs/</c>. Sem isto, as duas pastas sobravam do reset.
    /// </summary>
    public void SoltarArquivos()
    {
        try { Services.Navegador.NavegadorService.Padrao.DisposeAsync().AsTask().Wait(5000); } catch { }

        _registro?.Dispose();
        _registro = null;
        _notifyIcon?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _httpClient.Dispose();

        // O Edge do navegador é filho do AIB: fecha junto. Com prazo, para o app não travar ao
        // sair se o Edge não responder.
        try { Services.Navegador.NavegadorService.Padrao.DisposeAsync().AsTask().Wait(3000); } catch { }

        // Senhas lembradas "até fechar o AIB" morrem aqui.
        Services.Terminal.AskpassServidor.Padrao?.Dispose();

        base.OnExit(e);
    }
}
