using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Animation;
using AIB.Services;
using Markdig.Wpf;

// Aliases para eliminar ambiguidade entre System.Drawing e System.Windows.Media
using WColor   = System.Windows.Media.Color;
using WBrushes = System.Windows.Media.Brushes;
using WPoint   = System.Windows.Point;
using LinearGB = System.Windows.Media.LinearGradientBrush;
using SolidCB  = System.Windows.Media.SolidColorBrush;
using GStop    = System.Windows.Media.GradientStop;
using WTranslate = System.Windows.Media.TranslateTransform;

namespace AIB.Views;

public partial class ChatWindow : Window
{
    private readonly ConversationService _conversation;
    private readonly ShadowAssistantService _shadowService;
    // Um widget por monitor: o da tela do cursor fica com opacidade 0.7 (ativo),
    // os outros com 0.3. O balão de sugestão aparece só no widget ativo.
    private readonly List<ShadowWidget> _shadowWidgets = new();
    private int _activeScreenIndex = -1;
    private bool _isShadowModeEnabled = false;
    private readonly SettingsService _settingsService;

    private readonly VoiceService _voiceService;

    private bool _voiceReady = false;
    private bool _voiceListening = false;
    private bool _isSending = false;

    public ChatWindow(ConversationService conversation, SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _conversation = conversation;
        _conversation.OnTokenCountChanged += UpdateTokenCounterUI;

        // A compactação roda atrás do portão, depois do turno, e pode levar minutos. Sem estes
        // dois a espera era silêncio total — e silêncio longo não se distingue de travamento.
        _conversation.CompactacaoAndou += p =>
            Dispatcher.BeginInvoke(new Action(() => AnunciarCompactacao(p)));

        _conversation.CompactacaoAcabou += () =>
            Dispatcher.BeginInvoke(new Action(EsconderEspera));
        _conversation.OnWarmupStateChanged += HandleWarmupState;
        _conversation.OnTitleChanged += AplicarTitulo;
        _conversation.OnHistoryChanged += AtualizarPainelDeHistorico;

        _shadowService = new ShadowAssistantService(_conversation, _settingsService);
        _shadowService.OnSuggestionReceived += OnShadowSuggestion;
        _shadowService.OnActiveScreenChanged += OnActiveScreenChanged;

        StateChanged += ChatWindow_StateChanged;
        IsVisibleChanged += ChatWindow_IsVisibleChanged;

        // Registra os HWNDs do próprio AIB no Shadow (evita auto-OCR da janela do chat
        // e do widget). Precisa esperar Loaded para o HWND existir.
        this.Loaded += (s, e) =>
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            _shadowService.RegisterOwnWindow(helper.Handle);
        };

        // Mostra/esconde botão do Shadow conforme setting opt-in
        ApplyShadowAssistantSetting();

        _voiceService = new VoiceService();

        // Inicializa UI
        RefreshLevelUI(false);
        UpdateTokenCounterUI(_conversation.CurrentTokenReport);
        ApplyCharacterUI();
        InputBox.Focus();




        double guardada = _settingsService.LoadSettings().AlturaDaConversa;
        if (guardada > 0) Height = AlturaNoLimite(guardada, SystemParameters.WorkArea.Height);

        // Posiciona a janela: centralizada horizontal, flutuando acima da barra de tarefas
        this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
        this.Top = SystemParameters.WorkArea.Bottom - this.ActualHeight - FolgaDaBarraDeTarefas;

        // Reposiciona após a janela ter tamanho real (SizeToContent)
        this.Loaded += (s, e) => RepositionWindow();
        this.SizeChanged += (s, e) => RepositionWindow();
        MainAreaGrid.SizeChanged += (s, e) => AjustarTetoDaDigitacao();

        // O painel é uma janela separada, mas não uma janela independente: ele fica colado na
        // conversa. Reposicioná-la ou redimensioná-la (ela não se arrasta mais) leva o painel
        // junto, senão os dois se soltam e o conjunto deixa de parecer uma peça só.
        this.LocationChanged += (s, e) => PosicionarPainel();

        // Esconde ao clicar fora
        this.Deactivated += Window_Deactivated;

        // Inicialização assíncrona do Whisper (evita travamento na UI Thread)
        _ = InitVoiceAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Controle de Visibilidade
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O personagem que a tela e a conversa refletem agora. <c>null</c> antes da primeira
    /// aplicação, que é a do construtor.
    /// </summary>
    private string? _personagemAplicado;

    /// <summary>
    /// Põe na tela o nome do personagem ativo — header, estado vazio e placeholder.
    /// <para>
    /// Chamado também ao SALVAR AS CONFIGURAÇÕES, qualquer que tenha sido a mudança. Por isso
    /// ele não zera mais a conversa por conta própria: zerava sempre, e trocar o tema ou o
    /// prazo do diário apagava da tela a conversa em andamento — e, sendo uma cópia parcial de
    /// <see cref="NovaConversa"/>, deixava o título, o contador e a leitura de e-mail da
    /// conversa anterior.
    /// </para>
    /// <para>
    /// Só quando o PERSONAGEM muda começa uma conversa nova, e pelo caminho de sempre: o
    /// prompt de sistema é do personagem (SOUL), e continuar a conversa com a alma trocada no
    /// meio misturaria duas vozes no mesmo histórico.
    /// </para>
    /// </summary>
    public void ApplyCharacterUI()
    {
        string personagem = (_settingsService.LoadSettings().ActiveCharacter ?? "").Trim();
        string nome = NomeDaInteligencia();

        if (AgentNameText != null) AgentNameText.Text = nome.ToUpper();

        // O placeholder e o estado vazio falam com o nome do personagem ativo, como o
        // "Fale com a KAI..." de §3.7(b). O placeholder por AplicarPlaceholder, que sabe se
        // se está lendo um e-mail — escrevê-lo aqui apagava o "sobre este e-mail".
        if (EmptyTitleText != null) EmptyTitleText.Text = $"Converse com {nome}";
        AplicarPlaceholder();

        bool trocou = _personagemAplicado != null
                      && !string.Equals(_personagemAplicado, personagem, StringComparison.Ordinal);
        _personagemAplicado = personagem;

        if (!trocou) return;

        NovaConversa();

        // A conversa nova não é sobre e-mail nenhum: ficar na leitura mostraria o cabeçalho de
        // um e-mail em cima de uma conversa que não está mais ligada a ele.
        if (_emailEmLeitura != null)
        {
            _emailEmLeitura = null;
            AplicarEstadoDoModo();
            if (ModoEmail.IsChecked == true) MontarCaixaDeEntrada();
        }
    }

    private void HandleWarmupState(bool isWarmingUp)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (isWarmingUp)
            {
                InputBox.IsEnabled = false;
                SendButton.IsEnabled = false;
                StatusBar.Visibility = Visibility.Visible;
                StatusText.Text = "⏳ AIB conectando e aquecendo motores (pode levar 2 min na 1ª vez)...";
                InputBox.Text = "";
            }
            else
            {
                InputBox.IsEnabled = true;
                SendButton.IsEnabled = true;
                StatusBar.Visibility = Visibility.Collapsed;
                StatusText.Text = "🧠 Pensando...";
                InputBox.Focus();
            }
        });
    }

    /// <summary>
    /// Some quando o usuário vai fazer outra coisa — e SÓ nesse caso.
    /// <para>
    /// Perder o foco para outra janela do próprio AIB não é sair: o painel lateral é uma janela
    /// separada, e abrir o painel escondia a conversa inteira, deixando o painel sozinho na
    /// tela. O <see cref="ModalGuard"/> já cobria os modais; janela irmã não-modal não passava
    /// por ele.
    /// </para>
    /// <para>
    /// A verificação é adiada de propósito. No instante do <c>Deactivated</c> a janela que
    /// RECEBEU o foco ainda não se declarou ativa, então perguntar agora responderia sempre
    /// "ninguém do AIB está ativo" e o chat sumiria de qualquer jeito.
    /// </para>
    /// </summary>
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (ModalGuard.IsAnyModalOpen) return;

        Dispatcher.BeginInvoke(
            new Action(EsconderSeOFocoSaiuDoAib),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void EsconderSeOFocoSaiuDoAib()
    {
        if (ModalGuard.IsAnyModalOpen) return;
        if (AlgumaJanelaDoAibEstaAtiva()) return;

        EsconderTudo();
    }

    /// <summary>Se alguma janela do próprio AIB está com o foco agora.</summary>
    private static bool AlgumaJanelaDoAibEstaAtiva()
    {
        foreach (Window janela in System.Windows.Application.Current.Windows)
            if (janela.IsActive) return true;

        return false;
    }

    /// <summary>
    /// Esconde a conversa e o painel juntos.
    /// <para>
    /// O painel precisa vir junto porque esconder a janela DONA não esconde as janelas que ela
    /// possui: o painel ficava na tela sozinho, sem a conversa a que pertence, e sem o botão
    /// que o abriu para poder fechá-lo.
    /// </para>
    /// </summary>
    private void EsconderTudo()
    {
        _painel?.Hide();
        if (Visibility == Visibility.Visible) Hide();
    }

    /// <summary>
    /// Turno terminado: o texto inteiro da resposta. Existe para o orbe poder mostrar a
    /// resposta no balao dele sem reimplementar o laco de stream, as ferramentas, o portao de
    /// confirmacao e a gravacao de historico — tudo isso ja acontece aqui, mesmo com a janela
    /// escondida, e as bolhas ficam prontas para quando o usuario abrir a conversa.
    /// </summary>
    public event Action<string>? TurnoConcluido;

    /// <summary>
    /// O passo em que o turno está AGORA, em uma linha: "Pensando", "Lendo arquivo",
    /// "Esperando você autorizar". Vazio quando o turno acabou.
    /// <para>
    /// O orbe é a janela do que a AIB está fazendo, e até aqui ele só sabia do FIM: durante os
    /// minutos em que o modelo trabalha ele ficava parado, como se nada estivesse acontecendo.
    /// Este evento é o que acende o anel dele no passo certo.
    /// </para>
    /// <para>
    /// UM evento, e uma FRASE em vez de um enum, porque quem consome é um tooltip. Um enum
    /// obrigaria uma segunda tabela de frases do lado de lá, longe do único lugar que sabe o
    /// nome da ferramenta que está rodando — e as duas divergiriam na primeira ferramenta
    /// nova.
    /// </para>
    /// </summary>
    public event Action<string, string?>? PassoDoTurnoMudou;

    /// <summary>
    /// Se o turno corrente (ou o último) foi pedido pela barra do orbe, e não digitado aqui.
    /// <para>
    /// É o que decide se o orbe REPETE a resposta ou só mostra estado — ver
    /// <see cref="ShadowAssistantWindow.OrbeDeveFalar"/>. Quem perguntou pela barra está
    /// olhando para ela e tem de receber a resposta ali; quem digitou na conversa já a vê em
    /// balão, e um segundo balão sobre o desktop diria duas vezes a mesma coisa.
    /// </para>
    /// </summary>
    public bool TurnoVeioDoOrbe { get; private set; }

    /// <summary>
    /// Armado por <see cref="AbrirComMensagem"/> e consumido pelo envio.
    /// <para>
    /// Separado de <see cref="TurnoVeioDoOrbe"/> porque o envio tem várias saídas antecipadas
    /// (/skills, /compact, turno em andamento) e a marca não pode sobreviver a elas para
    /// carimbar o PRÓXIMO turno, que pode ter sido digitado na conversa.
    /// </para>
    /// </summary>
    private bool _pedidoPeloOrbe;

    /// <summary>
    /// Abre a conversa com uma mensagem ja enviada — e a porta que a barra do orbe usa (§5.3
    /// da spec do Shadow Assistant).
    /// <para>
    /// Reaproveita o mesmo caminho do botao de enviar, e nao uma copia dele: um segundo lugar
    /// que monta turno acabaria divergindo em qual dos dois grava historico, conta XP ou
    /// dispara compactacao.
    /// </para>
    /// </summary>
    public void AbrirComMensagem(string texto, bool mostrarJanela = true)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;

        // Sem mostrar a janela é o caminho da BARRA do orbe: o turno roda escondido e a
        // resposta tem de voltar para onde a pergunta foi feita.
        _pedidoPeloOrbe = !mostrarJanela;

        if (mostrarJanela && Visibility != Visibility.Visible) ToggleWindow();

        InputBox.Text = texto.Trim();
        InputBox.CaretIndex = InputBox.Text.Length;
        SendButton_Click(this, new RoutedEventArgs());
    }

    public void ToggleWindow()
    {
        if (this.Visibility == Visibility.Visible)
        {
            EsconderTudo();
            return;
        }

        RepositionWindow();
        Show();
        Activate();
        InputBox.Focus();

        // O painel volta com a conversa se estava aberto quando ela sumiu — quem escondeu os
        // dois juntos deve trazer os dois juntos de volta.
        if (_painelAberto && _painel != null)
        {
            PosicionarPainel();
            _painel.Show();
        }
    }

    /// <summary>
    /// Distância entre a base da janela e a barra de tarefas.
    /// <para>
    /// Era 45. A janela flutuava alto demais e o vão embaixo dela chamava mais atenção que a
    /// própria conversa. Metade disso mantém a impressão de flutuar sem o buraco.
    /// </para>
    /// </summary>
    private const double FolgaDaBarraDeTarefas = 22;

    /// <summary>
    /// A menor altura da janela (com a margem da sombra): abaixo disso a lista de mensagens
    /// some atrás do cabeçalho e da barra de digitação.
    /// </summary>
    public const double AlturaMinima = 445;

    /// <summary>
    /// A altura pedida pelo puxador, presa entre a mínima e a área de trabalho: a base fica
    /// acima da barra de tarefas, então o topo não pode passar do topo da tela.
    /// </summary>
    public static double AlturaNoLimite(double pedida, double area) =>
        Math.Clamp(pedida, AlturaMinima, Math.Max(AlturaMinima, area - FolgaDaBarraDeTarefas));

    private void PuxadorDeAltura_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // A base é presa; subir o puxador (VerticalChange negativo) cresce a janela para cima.
        Height = AlturaNoLimite(Height - e.VerticalChange, SystemParameters.WorkArea.Height);
    }

    private void PuxadorDeAltura_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var settings = _settingsService.LoadSettings();
        settings.AlturaDaConversa = Height;
        _settingsService.SaveSettings(settings);
    }

    private void RepositionWindow()
    {
        double taskbarBottom = SystemParameters.WorkArea.Bottom;
        this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
        this.Top = taskbarBottom - this.ActualHeight - FolgaDaBarraDeTarefas;

        // A altura muda com SizeToContent; o painel se alinha pela BASE da conversa, então
        // reposicionar a conversa sem reposicionar o painel desencontra os dois.
        PosicionarPainel();
    }

    public void RefreshLevelUI(bool incrementXp = false)
    {
        var settings = _settingsService.LoadSettings();

        if (incrementXp)
        {
            int oldLevel = LevelService.GetLevel(settings.MessageCount);
            settings.MessageCount++;
            _settingsService.SaveSettings(settings);

            int newLevel = LevelService.GetLevel(settings.MessageCount);
            if (newLevel > oldLevel)
            {
                AddAgentBubble($"🎉 **LEVEL UP!** Parabéns, AIB atingiu o **Nível {newLevel}**!\nNovas habilidades e pastas podem ter sido desbloqueadas.");
                ChatScrollViewer.ScrollToEnd();
            }
        }

        var s = settings;
        int lvl = LevelService.GetLevel(s.MessageCount);
        int xpBase = LevelService.GetXPForCurrentLevel(lvl);
        int xpNext = LevelService.GetXPForNextLevel(lvl);
        int currentXpInLevel = s.MessageCount - xpBase;
        int requiredXpInLevel = xpNext - xpBase;

        LevelText.Text = $"Nível {lvl}";

        // Tooltip e Progress Bar
        TooltipLevelInfo.Text = $"Nível {lvl} ({s.MessageCount}/{xpNext} XP)";
        XpProgressBar.Maximum = requiredXpInLevel == 0 ? 1 : requiredXpInLevel;
        XpProgressBar.Value = currentXpInLevel;

        // Faixa (Bronze, Prata, Ouro, Platina, Diamante). Ela pinta só a barra de XP, dentro
        // do ToolTip. O texto da pill fica sempre em lilás: §3.3(c) diz que é EXATAMENTE a
        // pill da tela de seleção, e a moldura neon precisa seguir sendo a única cor saturada
        // do header (O2). A informação da faixa não se perde — vive onde o usuário vai olhar
        // para saber do nível.
        WColor corDaFaixa;
        if (lvl <= 2) corDaFaixa = WColor.FromRgb(0xCD, 0x7F, 0x32);        // Bronze
        else if (lvl <= 4) corDaFaixa = WColor.FromRgb(0xC0, 0xC0, 0xC0);  // Prata
        else if (lvl <= 6) corDaFaixa = WColor.FromRgb(0xFF, 0xD7, 0x00);  // Ouro
        else if (lvl <= 8) corDaFaixa = WColor.FromRgb(0x00, 0xFF, 0x7F);  // Platina
        else corDaFaixa = WColor.FromRgb(0x00, 0xBF, 0xFF);                 // Diamante

        XpProgressBar.Foreground = new SolidCB(corDaFaixa);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Criação de Bolhas de Chat
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Monta a LINHA que hospeda uma bolha. §3.4 e §3.9.
    /// <para>
    /// A hora fica FORA da bolha, na coluna oposta ao alinhamento: nas linhas do usuário à
    /// esquerda, nas da IA à direita. Se ela fosse irmã da bolha na mesma direção, ocuparia
    /// espaço e empurraria a bolha para longe da borda. A5.
    /// </para>
    /// <para>
    /// Aparece só no hover da linha, e a bolha é limitada a 74% da largura da lista.
    /// </para>
    /// </summary>
    /// <summary>
    /// Casca da bolha da IA: a sombra numa camada, o conteúdo em OUTRA.
    /// <para>
    /// No WPF, um <c>Effect</c> obriga a subárvore inteira a ser rasterizada numa superfície
    /// intermediária, e ali o ClearType é desligado — o texto passa a ser suavizado em escala
    /// de cinza e fica visivelmente mais mole. Como a spec exige a sombra na bolha da IA (§3.5)
    /// e a do usuário não tem nenhuma (O5), o efeito aparecia como um borrão só do lado da IA.
    /// </para>
    /// <para>
    /// A saída é o conteúdo ser IRMÃO da camada que carrega a sombra, e não filho dela. Assim a
    /// sombra continua sendo desenhada com efeito, e o texto é desenhado direto na tela.
    /// </para>
    /// <para>
    /// Isto não vale para a bolha do usuário: sem sombra, ela nunca teve o problema, e uma
    /// camada extra ali seria custo sem retorno.
    /// </para>
    /// </summary>
    private Grid CascaDaIa(UIElement conteudo, Thickness recheio)
    {
        var grade = new Grid();

        var fundo = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SurfaceCardBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderCardBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Effect = (System.Windows.Media.Effects.Effect)FindResource("CardShadow")
        };

        var caixa = new Border { Padding = recheio, Child = conteudo };

        grade.Children.Add(fundo);
        grade.Children.Add(caixa);

        return grade;
    }

    private Grid NovaLinha(FrameworkElement bolha, bool doUsuario)
    {
        var linha = new Grid { Margin = new Thickness(0, 0, 0, 14), Background = WBrushes.Transparent };
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hora = new TextBlock
        {
            // FindResource, e nao Resources[]: ver a nota do AddUserBubble.
            Style = (Style)FindResource("StampText"),
            Text = DateTime.Now.ToString("HH:mm")
        };

        bolha.MaxWidth = Math.Max(120, ChatScrollViewer.ActualWidth > 0
            ? ChatScrollViewer.ActualWidth * 0.74
            : 770 * 0.74);

        if (doUsuario)
        {
            linha.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            Grid.SetColumn(hora, 0);
            Grid.SetColumn(bolha, 1);
        }
        else
        {
            linha.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            Grid.SetColumn(bolha, 0);
            Grid.SetColumn(hora, 1);
        }

        linha.Children.Add(hora);
        linha.Children.Add(bolha);

        linha.MouseEnter += (_, _) => hora.Opacity = 1;
        linha.MouseLeave += (_, _) => hora.Opacity = 0;

        MessagesPanel.Children.Add(linha);
        AtualizarEstadoVazio();
        return linha;
    }

    /// <summary>
    /// Tira do painel a LINHA que hospeda esta bolha.
    /// <para>
    /// Desde que a bolha passou a morar dentro de um Grid de linha, remover a bolha direto do
    /// MessagesPanel não faz nada: ela não é mais filha dele, e Children.Remove de quem não é
    /// filho falha em silêncio. O indicador de "digitando" ficaria para sempre na tela.
    /// </para>
    /// </summary>
    private void RemoverLinha(FrameworkElement? bolha)
    {
        if (bolha == null) return;

        DependencyObject? atual = bolha;
        while (atual != null)
        {
            if (atual is FrameworkElement fe && MessagesPanel.Children.Contains(fe))
            {
                MessagesPanel.Children.Remove(fe);
                AtualizarEstadoVazio();
                return;
            }

            atual = System.Windows.Media.VisualTreeHelper.GetParent(atual)
                    ?? LogicalTreeHelper.GetParent(atual);
        }
    }

    /// <summary>
    /// Cadeia de ações da fala corrente. Uma por FALA, e não por turno: cada balão do agente
    /// leva embaixo as ferramentas que ele anunciou, e as que rodam seguidas sem fala entre
    /// elas colapsam no mesmo chip.
    /// </summary>
    private ToolChainView? _cadeiaAtual;

    /// <summary>
    /// Devolve a cadeia do turno, criando-a se ainda não existir.
    /// <para>
    /// Ela entra ACIMA do indicador "digitando" (§4): o agente ainda está trabalhando, e ver os
    /// três pontos abaixo das ações é o que faz o conjunto ler como "fez isto, e continua".
    /// </para>
    /// </summary>
    private ToolChainView GarantirCadeia(ref FrameworkElement? typingBubble)
    {
        if (_cadeiaAtual != null) return _cadeiaAtual;

        var cadeia = new ToolChainView { Margin = new Thickness(0, 0, 0, 14) };

        // Posição: antes da linha do "digitando", se ela estiver na tela.
        int indice = MessagesPanel.Children.Count;
        if (typingBubble != null)
        {
            var linhaDoTyping = LinhaDe(typingBubble);
            if (linhaDoTyping != null)
            {
                int achado = MessagesPanel.Children.IndexOf(linhaDoTyping);
                if (achado >= 0) indice = achado;
            }
        }

        MessagesPanel.Children.Insert(indice, cadeia);
        AtualizarEstadoVazio();

        _cadeiaAtual = cadeia;
        return cadeia;
    }

    /// <summary>O texto do aviso: o nome da persona, e não "AIB guardou um fato".</summary>
    public static string TextoDoAviso(string nome) => $"{nome} lembrará disso…";

    /// <summary>
    /// A linha discreta que avisa que a persona guardou um fato sobre o usuário. O fato em si
    /// fica no tooltip e no facts.md: o aviso é para o usuário saber que ela vai lembrar, não
    /// para repetir o que ele acabou de dizer.
    /// </summary>
    private void AddAvisoDeMemoria(string fato, FrameworkElement? typingBubble)
    {
        var aviso = new TextBlock
        {
            Text = "✦ " + TextoDoAviso(NomeDaInteligencia()),
            FontSize = (double)FindResource("FontSizeToolArg"),
            FontStyle = FontStyles.Italic,
            Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
            Margin = new Thickness(6, 0, 0, 14),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            ToolTip = string.IsNullOrWhiteSpace(fato) ? null : fato
        };

        // Antes da linha do "digitando", como a cadeia.
        int indice = MessagesPanel.Children.Count;
        if (typingBubble != null && LinhaDe(typingBubble) is { } linha)
        {
            int achado = MessagesPanel.Children.IndexOf(linha);
            if (achado >= 0) indice = achado;
        }

        MessagesPanel.Children.Insert(indice, aviso);
        ChatScrollViewer.ScrollToEnd();
    }

    /// <summary>
    /// Alimenta as abas do painel a partir de uma ação concluída — §6.2 e §6.3.
    /// <para>
    /// Os mesmos eventos que desenham a cadeia servem aqui. A cadeia mostra o turno corrente e
    /// some com ele; o registro e a lista de arquivos atravessam a conversa inteira.
    /// </para>
    /// </summary>
    // Publico para ensaio: e o ponto exato onde a linha do registro nasce, e onde ela
    // nascia em branco. Nao toca em nada da janela — so no ActionLogService.
    public static void RegistrarAcao(ChatStreamItem.ToolFinished acao)
    {
        var artefato = acao.Artifact;

        // O nome vem do EVENTO. Tira-lo do artefato deixava a linha em branco sempre que a
        // ferramenta nao tinha extrator proprio — foi o que aconteceu com a skill.
        ActionLogService.Add(ActionLogService.Construir(
            acao.Tool,
            artefato,
            acao.Failed,
            acao.Detail,
            acao.RawOutput,
            acao.Argument,
            acao.Summary,
            troca: acao.Change));

        // Só arquivo entra na lista de contexto, e só quando a ação deu certo: um caminho que
        // falhou ou foi recusado não está no contexto de coisa nenhuma.
        if (acao.Failed || artefato == null) return;

        // A edição agora deixa artefato de gravação, mas a pill da lista diria "criado pela IA"
        // de um arquivo que já existia. Fica fora, como ficava antes de ter artefato.
        if (acao.Tool == Ferramentas.Editar) return;

        switch (artefato.Kind)
        {
            case AIB.Services.Memory.ArtifactKind.FileWritten:
                ContextService.AddFile(artefato.Value, ContextOrigin.CreatedByAi);
                break;

            case AIB.Services.Memory.ArtifactKind.FileRead:
                ContextService.AddFile(artefato.Value, ContextOrigin.ReadByAi);
                break;
        }
    }

    /// <summary>Card de confirmação à espera de decisão. Só pode haver um por vez.</summary>
    private ConfirmCardView? _confirmacaoPendente;

    /// <summary>
    /// Mostra o card de confirmação na conversa e espera a decisão do usuário — §5.3.
    /// <para>
    /// Chamado pelo portão, de dentro da execução da ferramenta, que roda FORA da thread de
    /// interface. Todo o trabalho visual é empurrado para o Dispatcher; o que volta é só a
    /// resposta.
    /// </para>
    /// <para>
    /// Enquanto o card está na tela a fila fica bloqueada (O7) e o chip da ação passa a
    /// "Aguardando". O <see cref="ModalGuard"/> segura o desaparecimento da janela: o chat se
    /// esconde ao perder o foco, e sumir com a pergunta pendente deixaria o agente parado sem
    /// nada visível na tela.
    /// </para>
    /// </summary>
    public async Task<(bool, bool)> PerguntarConfirmacaoAsync(CommandConfirmationContext contexto)
    {
        var (card, janelaDoOrbe) = await Dispatcher.InvokeAsync(() =>
        {
            var novo = new ConfirmCardView { Margin = new Thickness(0, 0, 0, 14) };
            novo.Preencher(contexto);

            _confirmacaoPendente = novo;
            _cadeiaAtual?.Aguardar();

            // O único passo do turno em que a máquina não está trabalhando: está esperando
            // uma pessoa. O orbe tem de dizer isso, e não continuar anunciando a ferramenta
            // que está parada no portão.
            PassoDoTurnoMudou?.Invoke("Esperando você autorizar", contexto.Tool);

            // No modo orbe — conversa fora da tela, orbe visível — a pergunta aparece numa
            // janela pequena logo acima dele. Abrir a conversa inteira para mostrar um card
            // tomava a tela de quem só pediu uma coisa rápida ao orbe.
            if (DeveUsarJanelaDoOrbe(IsVisible, PertoDoOrbe.Ativo))
            {
                var janela = new ConfirmacaoDoOrbeWindow(novo);
                janela.Show();
                return (novo, (ConfirmacaoDoOrbeWindow?)janela);
            }

            MessagesPanel.Children.Add(novo);
            AtualizarEstadoVazio();
            ChatScrollViewer.ScrollToEnd();

            // A janela precisa estar visível para a pergunta ser vista.
            if (Visibility != Visibility.Visible)
            {
                RepositionWindow();
                Show();
                Activate();
            }

            return (novo, (ConfirmacaoDoOrbeWindow?)null);
        });

        using (ModalGuard.Enter())
        {
            var resposta = await card.Resposta.ConfigureAwait(false);

            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_confirmacaoPendente, card)) _confirmacaoPendente = null;

                // Respondido, o turno volta ao trabalho — e o passo volta a ser a ferramenta
                // que estava parada no portão, que é a que segue daqui.
                PassoDoTurnoMudou?.Invoke(Ferramentas.Rotulo(contexto.Tool), contexto.Tool);

                // Decidido, o card SAI da conversa. Ele é uma pergunta, não uma mensagem: uma
                // pergunta já respondida ocupando espaço permanente empurra o que veio depois
                // para longe, e numa conversa com várias ações a lista vira uma pilha de
                // formulários mortos.
                //
                // Nada se perde: o que foi autorizado vira ícone na cadeia de ações, e o
                // histórico de ações do painel guarda a linha inteira, com o comando exato no
                // tooltip. Uma recusa vira ícone vermelho, com o mesmo registro.
                if (janelaDoOrbe is not null)
                {
                    janelaDoOrbe.Close();
                    return;
                }

                MessagesPanel.Children.Remove(card);
                AtualizarEstadoVazio();
            });

            return resposta;
        }
    }

    /// <summary>
    /// Se a confirmação vai para a janela do orbe em vez de para a conversa: só quando a
    /// conversa está fora da tela e há orbe onde ancorar. Com o orbe desligado, abrir a
    /// conversa continua sendo o único lugar onde perguntar.
    /// </summary>
    public static bool DeveUsarJanelaDoOrbe(bool conversaNaTela, bool orbeNaTela) =>
        !conversaNaTela && orbeNaTela;

    /// <summary>
    /// Descarta uma confirmação pendente, devolvendo recusa a quem espera.
    /// <para>
    /// Necessário porque o card vive na lista de mensagens: limpar a conversa ou trocar de
    /// personagem apaga o elemento da tela, e sem isto a ferramenta ficaria esperando para
    /// sempre por uma resposta que nunca viria.
    /// </para>
    /// </summary>
    private void DescartarConfirmacaoPendente()
    {
        _confirmacaoPendente?.Descartar();
        _confirmacaoPendente = null;
    }

    /// <summary>A linha do painel que hospeda este elemento, ou nulo se ele não estiver lá.</summary>
    private FrameworkElement? LinhaDe(FrameworkElement? elemento)
    {
        DependencyObject? atual = elemento;
        while (atual != null)
        {
            if (atual is FrameworkElement fe && MessagesPanel.Children.Contains(fe)) return fe;
            atual = System.Windows.Media.VisualTreeHelper.GetParent(atual)
                    ?? LogicalTreeHelper.GetParent(atual);
        }

        return null;
    }

    /// <summary>Some com o §5.6 assim que existe qualquer mensagem, e o traz de volta ao limpar.</summary>
    private void AtualizarEstadoVazio()
    {
        EmptyState.Visibility = MessagesPanel.Children.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AddUserBubble(string text)
    {
        var border = new Border
        {
            // FindResource, e NAO Resources["..."]. O indexador olha o dicionario DESTA janela
            // e mais nada; o FindResource sobe pela arvore ate o Application.Resources.
            //
            // A bolha azul sumiu por causa disso: o estilo morava no Window.Resources e mudou
            // para o Themes/Controls.xaml quando a pilha de falas do Shadow passou a precisar
            // dele. A partir dali o indexador devolvia null, o Style ficava nulo, e a Border
            // nascia sem fundo, sem padding e sem alinhamento — texto branco solto na tela. Sem
            // erro, sem excecao: um cast de null para Style e valido.
            Style = (Style)FindResource("UserBubble"),
            Child = TextoSelecionavel(text)
        };

        var linha = NovaLinha(border, doUsuario: true);
        AnimateBubbleIn(linha);
        ChatScrollViewer.ScrollToEnd();
    }

    /// <summary>
    /// O texto da bolha do usuário, selecionável para copiar um trecho. TextBox somente
    /// leitura, sem moldura: o TextBlock de antes não seleciona nada, e o que se digitou numa
    /// pergunta longa (um caminho, um comando) só voltava redigitando.
    /// </summary>
    public static System.Windows.Controls.TextBox TextoSelecionavel(string texto)
    {
        var caixa = new System.Windows.Controls.TextBox
        {
            Text = texto,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsTabStop = false,
            Background = WBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = WBrushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            // A seleção padrão é azul — invisível sobre a bolha azul.
            SelectionBrush = WBrushes.White,
            SelectionOpacity = 0.35,
            Cursor = System.Windows.Input.Cursors.IBeam,
            FocusVisualStyle = null
        };
        TextBlock.SetLineHeight(caixa, 21);

        // O TextBox tem rolagem própria e engole a roda do mouse: com o ponteiro sobre a bolha,
        // a conversa parava de rolar. A roda segue para a lista, como na bolha da IA.
        caixa.PreviewMouseWheel += (s, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            var repasse = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = s
            };
            ((UIElement)((FrameworkElement)s).Parent)?.RaiseEvent(repasse);
        };

        return caixa;
    }

    private MarkdownViewer AddAgentBubble(string? initialText = null)
    {

        var viewer = new MarkdownViewer
        {
            // Antes do texto: trocar o interpretador depois obrigaria a remontar o documento.
            Pipeline = AIB.Ui.MarkdownPipelines.Conversa,
            Markdown = initialText ?? "",
            Foreground = (System.Windows.Media.Brush)FindResource("TextBodyBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            FontSize = (double)FindResource("FontSizeBubble"),
        };

        // O documento de fluxo nasce com margem própria de página, e ela se somava ao padding
        // da bolha. A margem negativa que existia aqui compensava isso puxando o conteúdo para
        // fora nos quatro lados — o que também comia o padding de 17x13 que a spec pede.
        // Zerar na fonte é o certo.
        ZerarMargemDoDocumento(viewer);

        // §5.5 — código inline em lilás sobre surfaceCode; bloco em textBody sobre
        // surfaceCodeBlock. Os dois em mono 12,5. Antes o inline saía ciano, uma cor que não
        // existe em nenhum dos dois arquivos de spec.
        var fonteMono = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily");
        var tamanhoCodigo = (double)FindResource("FontSizeCode");

        var inlineCodeStyle = new Style();
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.BackgroundProperty, FindResource("SurfaceCodeBrush")));
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, FindResource("AccentLilacBrush")));
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontFamilyProperty, fonteMono));
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontSizeProperty, tamanhoCodigo));

        var blockCodeStyle = new Style();
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.BackgroundProperty, FindResource("SurfaceCodeBlockBrush")));
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, FindResource("TextBodyBrush")));
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontFamilyProperty, fonteMono));
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontSizeProperty, tamanhoCodigo));

        viewer.Resources.Add(Markdig.Wpf.Styles.CodeStyleKey, inlineCodeStyle);
        viewer.Resources.Add(Markdig.Wpf.Styles.CodeBlockStyleKey, blockCodeStyle);

        // Permite que o scroll do mouse funcione mesmo com o ponteiro sobre a bolha Markdown
        viewer.PreviewMouseWheel += (s, e) =>
        {
            if (!e.Handled)
            {
                e.Handled = true;
                var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = s
                };
                ChatScrollViewer.RaiseEvent(eventArg);
            }
        };

        // O ShrinkWrap é o que faz a bolha da IA encolher até o texto, como a do usuário.
        // Sem ele o FlowDocument aceita toda a largura oferecida e "Kai online. Olá." vira uma
        // bolha de 74% da lista com um vão enorme à direita.
        //
        // A medição vai por fora, sobre o documento: perguntar ao visualizador não adianta,
        // porque ele devolve como desejada a mesma largura que recebeu. Os 2px de folga cobrem
        // o arredondamento entre a medição do texto e o desenho dele.
        var encolhido = new AIB.Ui.ShrinkWrap
        {
            Child = viewer,
            MedirNatural = () => AIB.Ui.FlowDocumentMeasure.LarguraNatural(viewer.Document) + 2
        };

        var linha = NovaLinha(CascaDaIa(encolhido, new Thickness(17, 13, 17, 13)), doUsuario: false);
        AnimateBubbleIn(linha);
        ChatScrollViewer.ScrollToEnd();
        return viewer;
    }

    /// <summary>
    /// Zera a margem de página do <c>FlowDocument</c> do visualizador, agora e a cada vez que
    /// ele for trocado.
    /// <para>
    /// Reaplicar não é zelo: atribuir <c>Markdown</c> reconstrói o documento inteiro, e o
    /// streaming da resposta faz isso a cada pedaço que chega. Zerar uma vez só valeria até a
    /// primeira letra da resposta.
    /// </para>
    /// </summary>
    private static void ZerarMargemDoDocumento(MarkdownViewer viewer)
    {
        static void Aplicar(MarkdownViewer v)
        {
            if (v.Document == null) return;
            v.Document.PagePadding = new Thickness(0);
            v.Document.PageWidth = double.NaN;
        }

        Aplicar(viewer);

        var descritor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            MarkdownViewer.DocumentProperty, typeof(MarkdownViewer));

        descritor?.AddValueChanged(viewer, (_, _) => Aplicar(viewer));
    }

    /// <summary>
    /// Animação de entrada da bolha: desliza 18px para cima + fade-in em 280ms.
    /// Estilo WhatsApp / iMessage — suave e direto.
    /// </summary>
    private static void AnimateBubbleIn(FrameworkElement element)
    {
        var transform = new WTranslate(0, 20);
        element.RenderTransform = transform;
        element.Opacity = 0;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(260));

        var slideUp = new DoubleAnimation(20, 0, duration) { EasingFunction = ease };
        var fadeIn  = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)));

        Storyboard.SetTarget(slideUp, element);
        Storyboard.SetTargetProperty(slideUp,
            new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        Storyboard.SetTarget(fadeIn, element);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath("Opacity"));

        var sb = new Storyboard();
        sb.Children.Add(slideUp);
        sb.Children.Add(fadeIn);
        sb.Begin();
    }

    /// <summary>
    /// Adiciona uma bolha "digitando" com 3 pontos animados.
    /// Retorna o Border e o DispatcherTimer para que o chamador possa pará-los.
    /// </summary>
    private (FrameworkElement bubble, System.Windows.Threading.DispatcherTimer timer) AddTypingIndicator()
    {
        // §3.6 — mesma casca da bolha da IA, com recheio próprio e três pontos de 6px.

        var pontos = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var bolinhas = new List<System.Windows.Shapes.Ellipse>();

        for (int i = 0; i < 3; i++)
        {
            var bolinha = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = (System.Windows.Media.Brush)FindResource("AccentLilacBrush"),
                Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0),
                Opacity = 0.25,
                VerticalAlignment = VerticalAlignment.Center
            };

            bolinhas.Add(bolinha);
            pontos.Children.Add(bolinha);
        }

        var border = CascaDaIa(pontos, new Thickness(18, 15, 18, 15));
        var linha = NovaLinha(border, doUsuario: false);
        ChatScrollViewer.ScrollToEnd();

        // Opacidade 0.25 -> 1 em 1,2s, em laço, com atraso escalonado de 160ms. O timer
        // continua sendo devolvido porque o chamador já sabe pará-lo; aqui ele só serve para
        // encerrar as animações junto.
        for (int i = 0; i < bolinhas.Count; i++)
        {
            var pulso = new DoubleAnimation(0.25, 1.0, new Duration(TimeSpan.FromMilliseconds(600)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 160)
            };

            bolinhas[i].BeginAnimation(UIElement.OpacityProperty, pulso);
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        timer.Start();

        return (border, timer);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ações Locais (Skills)
    // ─────────────────────────────────────────────────────────────────────────
    
    private void ShowSkillsList()
    {
        try
        {
            var (natives, dynamics) = _conversation.Registry.GetCategorizedTools();
            var sb = new System.Text.StringBuilder();
            
            sb.AppendLine("## ⚙️ Skills Nativas (Embutidas)\n");
            foreach (var n in natives)
            {
                sb.AppendLine($"- **`{n.Name}`**\n  *_{n.Description.Replace("\n", " ")}_*");
            }

            sb.AppendLine("\n---\n## 🧩 Skills Custom (Locais)\n");
            if (dynamics.Count == 0)
            {
                sb.AppendLine("*Nenhuma skill customizada detectada.*");
            }
            else
            {
                foreach (var d in dynamics)
                {
                    sb.AppendLine($"- **`{d.Name}`**\n  *_{d.Description.Replace("\n", " ")}_*");
                }
            }

            sb.AppendLine("\n---\n## 🧑‍💻 Suas Próprias Skills\n");
            sb.AppendLine("Sabia que eu posso aprender novos truques sozinha? Você pode me pedir para criar uma nova skill!\n");
            sb.AppendLine("*Basta me explicar o que você precisa que eu automatize, pesquise ou execute. Eu mesma escreverei o código (em Python ou PowerShell), salvarei na pasta e a nova ferramenta aparecerá aqui automaticamente.*");

            AddAgentBubble(sb.ToString());
            ChatScrollViewer.ScrollToEnd();
        }
        catch (Exception ex)
        {
            AddAgentBubble($"❌ **Erro ao carregar skills:** {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helper para Colar Arquivos
    // ─────────────────────────────────────────────────────────────────────────

    private void InsertFilePaths(string[] files)
    {
        string pathsToPaste = string.Join(" ", files.Select(f => $"\"{f}\"")) + " ";
        int caretIndex = InputBox.CaretIndex;
        InputBox.Text = InputBox.Text.Insert(caretIndex, pathsToPaste);
        InputBox.CaretIndex = caretIndex + pathsToPaste.Length;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Envio de Mensagem
    // ─────────────────────────────────────────────────────────────────────────

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        // Enquanto grava, o primário é PARAR (§3.7(e)): ele encerra a escuta em vez de enviar.
        // A transcrição final ainda chega por OnTranscriptionUpdated e dispara o envio.
        if (_voiceListening)
        {
            VoiceButton_Click(sender, e);
            return;
        }

        if (_isSending)
        {
            _conversation.CancelGeneration();
            return;
        }
        
        string text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Se o usuário digitar /skills, lidamos localmente e nem chamamos a OpenAI
        if (text.Equals("/skills", StringComparison.OrdinalIgnoreCase))
        {
            InputBox.Clear();
            AddUserBubble(text);
            ShowSkillsList();
            return;
        }

        // Memória a pedido: fecha um capítulo agora e promove um ato se der, sem esperar o
        // gatilho de tokens.
        //
        // UM comando, e não os dois que havia antes. A escolha entre capítulo e ato depende de
        // quantos turnos fechados existem e de quantos capítulos estão soltos — dois números
        // que o usuário não tem como saber antes de pedir. Fazer ele escolher era fazer ele
        // adivinhar, e errar custava um comando e uma recusa.
        if (text.Equals("/compact", StringComparison.OrdinalIgnoreCase))
        {
            await RodarComandoDeMemoria(text, nivel => _conversation.ForcarCompactacaoAsync(nivel));
            return;
        }

        // A conta por extenso. Não passa pelo modelo e não trava o portão: é leitura de estado
        // que já existe, e fazer o usuário esperar um turno para ver um número seria absurdo.
        if (text.Equals("/memoria", StringComparison.OrdinalIgnoreCase)
            || text.Equals("/memória", StringComparison.OrdinalIgnoreCase))
        {
            InputBox.Clear();
            AddUserBubble(text);
            // Entre cercas: a conta e alinhada por espacos, e o markdown colapsaria as
            // colunas em uma linha so de texto corrido.
            string conta = _conversation.MemoriaEmTexto(
                LevelService.GetLevel(_settingsService.LoadSettings().MessageCount));
            AddAgentBubble("```\n" + conta + "\n```");
            ChatScrollViewer.ScrollToEnd();
            return;
        }

        // Comando secreto para desbloquear nível
        if (text.StartsWith("/unlock_level", StringComparison.OrdinalIgnoreCase))
        {
            InputBox.Clear();
            AddUserBubble(text);
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[1], out int targetLevel) && targetLevel >= 1 && targetLevel <= 9)
            {
                int neededXP = LevelService.GetXPForCurrentLevel(targetLevel);
                var settings = _settingsService.LoadSettings();
                settings.MessageCount = neededXP;
                _settingsService.SaveSettings(settings);
                
                int maxTokens = LevelService.GetMaxTokensForLevel(targetLevel);
                UpdateTokenCounterUI(new TokenReport(0, 0, maxTokens));
                AddAgentBubble($"Cheat ativado! Avançando MessageCount para {neededXP}. Você agora é Nível {targetLevel} e possui {maxTokens} tokens de memória local livre.");
            }
            else
            {
                AddAgentBubble("Uso incorreto. Tente: /unlock_level 9");
            }
            return;
        }

        _isSending = true;
        InputBox.Clear();
        InputBox.IsEnabled = false;
        AplicarIconeDeParar("Parar a resposta");

        // A ORIGEM do turno, fixada aqui e não antes: acima deste ponto o envio tem saídas
        // antecipadas (/skills, /memoria, turno em andamento), e a marca do orbe sobrevivendo
        // a uma delas carimbaria o turno seguinte, que pode ter sido digitado na conversa.
        TurnoVeioDoOrbe = _pedidoPeloOrbe;
        _pedidoPeloOrbe = false;

        // O primeiro passo. Em modelo de raciocínio o silêncio até a primeira palavra são
        // dezenas de segundos, e é justamente aí que o anel do orbe precisa já estar girando.
        PassoDoTurnoMudou?.Invoke("Pensando", null);

        // §3.11: quando o turno nasce de um e-mail, quem representa a fala do usuário é o
        // CARTÃO do e-mail, e não uma bolha. O texto que o modelo recebe é o mesmo; o que muda
        // é a forma na tela. Repetir os dois seria dizer duas vezes a mesma coisa, e a segunda
        // seria uma bolha azul com um resumo que o usuário não escreveu.
        if (_bolhaDoTurno != null)
        {
            var substituta = _bolhaDoTurno();
            _bolhaDoTurno = null;
            MessagesPanel.Children.Add(substituta);
            AtualizarEstadoVazio();
        }
        else
        {
            AddUserBubble(text);
        }

        Console.WriteLine($"\n[USER] [{DateTime.Now:HH:mm:ss}]: {text}");

        string fullText = "";

        // Texto das falas já fechadas em balão. fullText guarda só a fala corrente; este
        // acumula o turno inteiro, que é o que vai para o log e para a notificação.
        string allText = "";
        string? errorText = null;
        
        FrameworkElement? typingBubble = null;
        System.Windows.Threading.DispatcherTimer? typingTimer = null;
        var idleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        idleTimer.Tick += (s, ev) =>
        {
            if (typingBubble != null)
            {
                typingTimer?.Stop();
                RemoverLinha(typingBubble);
                typingBubble = null;
                typingTimer = null;
            }
            idleTimer.Stop();
        };

        // Fecha uma fala em balão(ões): um por parte separada pela QuebraDeFala. A fala nova
        // fecha a cadeia anterior: as ferramentas que vierem depois abrem uma cadeia própria,
        // LOGO ABAIXO do balão. Sem isto todas as ferramentas do turno iam para o chip da
        // primeira fala, lá em cima, e "Script criado. Executando agora:" aparecia sem nada
        // embaixo. Ferramentas seguidas sem fala entre elas continuam no mesmo chip.
        void FecharFala(IReadOnlyList<string> partes)
        {
            if (partes.Count == 0) return;

            if (typingBubble != null)
            {
                typingTimer?.Stop();
                RemoverLinha(typingBubble);
                typingBubble = null;
                typingTimer = null;
            }

            foreach (var parte in partes) AddAgentBubble(parte);
            ChatScrollViewer.ScrollToEnd();

            allText += string.Join("\n\n", partes) + "\n\n";

            _cadeiaAtual?.RecolherFalhaPendente();
            _cadeiaAtual = null;
        }

        try
        {
            // O log técnico volta a ser só log. A cadeia de ações é desenhada a partir dos
            // eventos tipados ToolStarted/ToolFinished, e não mais de uma expressão regular
            // sobre a frase que o console imprime: mudar a frase do log quebrava a exibição
            // sem quebrar teste nenhum.
            var stream = _conversation.StreamResponseAsync(text, Console.Write);

            // Acumula a resposta completa silenciosamente.
            // SEM ConfigureAwait(false) de propósito: este laço é de interface, não de rede.
            // A leitura do socket já roda fora do Dispatcher porque toda a camada de serviço
            // usa ConfigureAwait(false); capturar o contexto aqui garante que o corpo do laço,
            // o catch e o finally continuem na thread de UI, como sempre foi.
            await foreach (var item in stream)
            {
                // ── §4  CADEIA DE AÇÕES ──────────────────────────────────────────
                // Guardar um fato não é ação na máquina: não entra na cadeia, vira o aviso
                // "Ellen lembrará disso…" quando termina (AddAvisoDeMemoria).
                if (item is ChatStreamItem.ToolStarted { Tool: Ferramentas.Lembrar }) continue;

                if (item is ChatStreamItem.ToolFinished { Tool: Ferramentas.Lembrar } lembrado)
                {
                    RegistrarAcao(lembrado);
                    if (!lembrado.Failed) AddAvisoDeMemoria(lembrado.Argument, typingBubble);
                    continue;
                }

                if (item is ChatStreamItem.ToolStarted iniciada)
                {
                    // A cadeia entra ACIMA do indicador "digitando", na coluna da IA.
                    var cadeia = GarantirCadeia(ref typingBubble);
                    cadeia.RecolherFalhaPendente();
                    cadeia.Iniciar(iniciada.Id, iniciada.Tool, iniciada.Argument);
                    ChatScrollViewer.ScrollToEnd();

                    // O MESMO rótulo do chip da cadeia. "read" é endereço; "Lendo arquivo" é
                    // notícia, e é o que quem olha o orbe de longe quer saber.
                    PassoDoTurnoMudou?.Invoke(Ferramentas.Rotulo(iniciada.Tool), iniciada.Tool);
                    continue;
                }

                if (item is ChatStreamItem.ToolFinished terminada)
                {
                    _cadeiaAtual?.Concluir(
                        terminada.Id, terminada.Failed, terminada.Denied,
                        terminada.Artifact, terminada.Detail);

                    RegistrarAcao(terminada);
                    ChatScrollViewer.ScrollToEnd();

                    // A ferramenta saiu de cena e o modelo volta a trabalhar. Sem esta volta, o
                    // orbe ficaria anunciando "Executando comando" pelo resto do turno.
                    PassoDoTurnoMudou?.Invoke("Pensando", null);
                    continue;
                }

                // O modelo começou a raciocinar. Em modelo de raciocínio isto acontece bem
                // antes da primeira palavra — em CPU, dezenas de segundos antes — e às vezes é
                // tudo que acontece, porque o turno termina em ferramenta sem nenhuma fala.
                //
                // É o momento certo para os três pontos. Adiantá-los para o ENVIO era mentira:
                // ali o modelo pode nem ter sido carregado ainda. Aqui existe token saindo.
                if (item is ChatStreamItem.Thinking)
                {
                    if (typingBubble == null)
                    {
                        var pensando = AddTypingIndicator();
                        typingBubble = pensando.bubble;
                        typingTimer = pensando.timer;
                        ChatScrollViewer.ScrollToEnd();
                    }

                    idleTimer.Stop();
                    idleTimer.Start();
                    continue;
                }

                // Fronteira de fala: o agente terminou de dizer o que ia dizer e vai usar uma
                // ferramenta. Fecha o balão com o que foi acumulado e recomeça o acúmulo — o
                // balão continua sendo renderizado só quando completo, como sempre foi.
                if (item is ChatStreamItem.SegmentBreak)
                {
                    FecharFala(QuebraDeFala.Dividir(fullText));
                    fullText = "";
                    continue;
                }

                if (item is not ChatStreamItem.Text textItem) continue;
                string chunk = textItem.Value;

                fullText += chunk;

                // A persona separou uma fala com a marca: o que veio antes já é balão, sem
                // esperar o fim do turno. O tempo de escrever a fala seguinte é a pausa
                // natural entre os dois, com os três pontos de volta logo abaixo.
                var (prontas, resto) = QuebraDeFala.Separar(fullText);
                if (prontas.Count > 0)
                {
                    FecharFala(prontas);
                    fullText = resto;
                }

                // Os três pontos, quando o modelo não raciocina — aí a primeira palavra é
                // mesmo o primeiro sinal. Repõe também o indicador retirado pelos 3 segundos
                // de inatividade.
                if (typingBubble == null)
                {
                    var tuple = AddTypingIndicator();
                    typingBubble = tuple.bubble;
                    typingTimer = tuple.timer;
                }

                // Reseta o timer de inatividade de 3 segundos
                idleTimer.Stop();
                idleTimer.Start();

                await Task.Yield(); // Força a liberação da thread de UI para renderizar os updates
            }

            Console.WriteLine($"\n[AIB]  [{DateTime.Now:HH:mm:ss}]: {fullText}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] [{DateTime.Now:HH:mm:ss}]: {ex.Message}");
            errorText = $"❌ **Erro:** {ex.Message}";
        }
        finally
        {
            // Limpa ambos os timers e a bolha de digitação
            idleTimer.Stop();
            if (typingBubble != null)
            {
                typingTimer?.Stop();
                RemoverLinha(typingBubble);
            }

            // A cadeia deste turno fecha aqui. A falha que ainda estivesse visível recolhe
            // para a trilha, e a próxima pergunta começa uma cadeia nova — as ações de turnos
            // diferentes não se misturam no mesmo chip.
            _cadeiaAtual?.RecolherFalhaPendente();
            _cadeiaAtual = null;

            // Exibe a fala final de uma só vez. Quando o turno teve várias falas, as anteriores
            // já viraram balão na fronteira de cada ferramenta; aqui fecha só a última.
            if (errorText != null)
            {
                AddAgentBubble(errorText);
            }
            else if (!string.IsNullOrWhiteSpace(fullText))
            {
                foreach (var parte in QuebraDeFala.Dividir(fullText)) AddAgentBubble(parte);
            }
            else if (string.IsNullOrWhiteSpace(allText))
            {
                // Nada foi dito no turno inteiro. Só aqui o aviso faz sentido: se já houve
                // falas anteriores, um "Ação executada" solto no fim seria ruído.
                AddAgentBubble("*Ação executada com sucesso.*");
            }

            ChatScrollViewer.ScrollToEnd();
            // Restaura a UI do InputBox
            StatusBar.Visibility = Visibility.Collapsed;
            InputBox.IsEnabled = true;
            RestaurarIconeDeEnviar();
            InputBox.Focus();
            _isSending = false;

            if (errorText == null)
            {
                RefreshLevelUI(true);
            }

            // O passo vazio ANTES do fim: quem escuta os dois eventos apaga o anel no primeiro
            // e decide o que dizer no segundo, e não o contrário — o anel parando depois da
            // fala aparecer deixaria o orbe um instante falando e trabalhando ao mesmo tempo.
            PassoDoTurnoMudou?.Invoke("", null);

            // Sem a marca: o orbe e a notificação mostram o turno inteiro de uma vez.
            string textoDoTurno = QuebraDeFala.Limpar(allText + fullText).Trim();
            TurnoConcluido?.Invoke(errorText ?? textoDoTurno);

            // Se a janela estiver invisível ou sem foco (usuário fazendo outra coisa), emite notificação
            if (!this.IsActive || this.Visibility != Visibility.Visible)
            {
                string turnText = textoDoTurno;
                string notifyText = errorText ?? (string.IsNullOrWhiteSpace(turnText) ? "*Ação executada com sucesso.*" : turnText);
                if (notifyText.Length > 200) notifyText = notifyText.Substring(0, 197) + "...";
                
                ((App)System.Windows.Application.Current).ShowNotification("AIB", notifyText);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Botão de Visão (OCR)
    // ─────────────────────────────────────────────────────────────────────────

    private async void VisionButton_Click(object sender, RoutedEventArgs e)
    {
        VisionButton.IsEnabled = false;
        VisionButton.Content = "⏳";
        StatusBar.Visibility = Visibility.Visible;
        StatusText.Text = "👁 Lendo tela...";

        try
        {
            var ocr = new OcrService();
            string text = await ocr.ExtractTextFromActiveScreenAsync();

            if (string.IsNullOrWhiteSpace(text))
            {
                AddAgentBubble("👁 Não encontrei texto legível na tela.");
            }
            else
            {
                // Injeta o conteúdo da tela diretamente no InputBox como contexto
                InputBox.Text = $"[Contexto da tela]: {text.Substring(0, Math.Min(text.Length, 400))}";
                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text.Length;
            }
        }
        catch (Exception ex)
        {
            AddAgentBubble($"❌ Erro na leitura de tela: {ex.Message}");
        }
        finally
        {
            StatusBar.Visibility = Visibility.Collapsed;
            VisionButton.Content = "📷";
            VisionButton.IsEnabled = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Botão de Voz (Whisper Local)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task InitVoiceAsync()
    {
        try
        {
            await Task.Run(async () => await _voiceService.InitializeAsync());
            _voiceReady = true;
            _ = Dispatcher.BeginInvoke(() =>
            {
                AplicarEstadoDeGravacao(false);
                VoiceButton.IsEnabled = true;
                Console.WriteLine("[VOICE] Motor Whisper inicializado e pronto.");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Erro ao inicializar: {ex.Message}");
            _ = Dispatcher.BeginInvoke(() =>
            {
                VoiceButton.IsEnabled = false;
                VoiceButton.ToolTip = "Voz indisponível";
            });
        }
    }

    private void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_voiceReady)
        {
            AddAgentBubble("⏳ O motor de voz ainda está inicializando. Aguarde...");
            return;
        }

        if (!_voiceListening)
        {
            // Inicia escuta
            _voiceService.OnTranscriptionUpdated += OnTranscriptionUpdated;
            _voiceService.StartListening();
            _voiceListening = true;
            AplicarEstadoDeGravacao(true);
            InputBox.Text = "";
            StatusBar.Visibility = Visibility.Visible;
            StatusText.Text = "🎙 Ouvindo...";
        }
        else
        {
            // Para escuta
            _voiceService.StopListening();
            _voiceService.OnTranscriptionUpdated -= OnTranscriptionUpdated;
            _voiceListening = false;
            AplicarEstadoDeGravacao(false);
            StatusBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Troca a aparência do botão primário entre ENVIAR e PARAR — §3.7(e).
    /// <para>
    /// O estado de gravação mora no botão primário, e não no microfone: o primário é sempre o
    /// aviãozinho, e o microfone é um botão separado à esquerda. Já foram trocados por engano
    /// uma vez (A7).
    /// </para>
    /// </summary>
    private void AplicarEstadoDeGravacao(bool gravando)
    {
        if (gravando)
        {
            AplicarIconeDeParar("Parar de gravar");
            VoiceButton.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            VoiceButton.ToolTip = "Ouvindo… clique para parar.";
            return;
        }

        RestaurarIconeDeEnviar();
        VoiceButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        VoiceButton.ToolTip = "Falar com o AIB (Whisper)";
    }

    /// <summary>
    /// Põe o "parar" do §3.7(e) no botão primário: quadrado vermelho de 13px, r3.
    /// <para>
    /// Dois momentos usam este mesmo desenho — a gravação e o turno em andamento —, e por um
    /// tempo cada um tinha o seu: o turno escrevia o caractere "■" com um vermelho literal no
    /// código, que não era nenhum dos vermelhos da paleta. A cor vem do token, o mesmo
    /// <c>DangerBrush</c> que pinta toda ação destrutiva da interface.
    /// </para>
    /// </summary>
    /// <param name="dica">O que parar significa aqui — a gravação ou a resposta.</param>
    private void AplicarIconeDeParar(string dica)
    {
        SendButton.Content = new Border
        {
            Width = 13,
            Height = 13,
            CornerRadius = new CornerRadius(3),
            Background = (System.Windows.Media.Brush)FindResource("DangerBrush")
        };
        SendButton.ToolTip = dica;
    }

    /// <summary>
    /// Devolve ao botão primário o aviãozinho — o <c>SendIcon</c> desenhado no XAML, e não uma
    /// cópia dele.
    /// <para>
    /// O fim do turno escrevia "➔" no botão: a partir do primeiro envio o aviãozinho sumia da
    /// tela para sempre, trocado por uma seta de texto com outra forma e outro tamanho. Como o
    /// desenho original continua vivo no campo gerado pelo <c>x:Name</c> mesmo depois de ser
    /// tirado do botão, basta recolocá-lo para o botão voltar a ser o que o XAML descreve.
    /// </para>
    /// </summary>
    private void RestaurarIconeDeEnviar()
    {
        SendButton.Content = SendIcon;
        SendButton.ToolTip = "Enviar";
    }

    private void OnTranscriptionUpdated(object? sender, TranscriptionEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            InputBox.Text = e.Text;
            // Auto-submit quando a transcrição está finalizada (silêncio detectado)
            if (e.IsFinal && !string.IsNullOrWhiteSpace(e.Text))
            {
                _voiceService.StopListening();
                _voiceService.OnTranscriptionUpdated -= OnTranscriptionUpdated;
                _voiceListening = false;
                AplicarEstadoDeGravacao(false);
                StatusBar.Visibility = Visibility.Collapsed;
                SendButton_Click(this, new RoutedEventArgs());
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Handlers de controle da janela
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O que a lista de "/" oferece. SÓ comandos que existem.
    /// <para>
    /// Ela sugeria <c>/clear</c>, <c>/help</c> e <c>/vault</c>, e nenhum dos três era tratado
    /// em <c>SendButton_Click</c>: escolher um deles mandava o texto para o modelo como
    /// pergunta. Uma lista de comandos que inventa comandos é pior que lista nenhuma — ela
    /// ensina o atalho errado e só desmente depois do envio.
    /// </para>
    /// <para>
    /// <c>/unlock_level</c> fica de fora de propósito: é trapaça de desenvolvimento, não
    /// recurso. Oferecê-lo na lista o transformaria em recurso.
    /// </para>
    /// </summary>
    private readonly string[] _slashCommands = { "/compact", "/memoria", "/skills" };

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = InputBox.Text;

        // O placeholder é um TextBlock por baixo, e não a marca d'água do controle: o TextBox
        // do WPF não tem placeholder nativo.
        InputPlaceholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (text.StartsWith("/") && text.Length >= 1)
        {
            var matches = _slashCommands.Where(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Any())
            {
                CommandsList.ItemsSource = matches;
                CommandsPopup.IsOpen = true;
                CommandsList.SelectedIndex = 0;
            }
            else
            {
                CommandsPopup.IsOpen = false;
            }
        }
        else
        {
            CommandsPopup.IsOpen = false;
        }
    }

    private void InputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Intercepta CTRL+V para colar caminhos de arquivos copiados
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList();
                if (files != null && files.Count > 0)
                {
                    string[] fileArray = new string[files.Count];
                    files.CopyTo(fileArray, 0);
                    InsertFilePaths(fileArray);
                    e.Handled = true;
                    return;
                }
            }
        }

        if (CommandsPopup.IsOpen)
        {
            if (e.Key == Key.Down)
            {
                if (CommandsList.SelectedIndex < CommandsList.Items.Count - 1) CommandsList.SelectedIndex++;
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.Up)
            {
                if (CommandsList.SelectedIndex > 0) CommandsList.SelectedIndex--;
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                if (CommandsList.SelectedItem is string cmd)
                {
                    InputBox.Text = cmd + " ";
                    InputBox.CaretIndex = InputBox.Text.Length;
                    CommandsPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Key.Escape)
            {
                CommandsPopup.IsOpen = false;
                e.Handled = true;
                return;
            }
        }

        // Comportamento de quebra de linha vs envio:
        //   - Enter sozinho            -> envia mensagem
        //   - Ctrl+Enter ou Shift+Enter -> quebra linha
        // Shift+Enter o TextBox processa nativamente (AcceptsReturn=True). Já Ctrl+Enter
        // precisa de injeção manual porque WPF ignora Ctrl como modifier de inserção.
        if (e.Key == Key.Enter)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            if (ctrl)
            {
                int caret = InputBox.CaretIndex;
                InputBox.Text = InputBox.Text.Insert(caret, Environment.NewLine);
                InputBox.CaretIndex = caret + Environment.NewLine.Length;
                e.Handled = true;
                return;
            }
            if (shift) return; // deixa o TextBox inserir \n nativamente

            if (!_isSending)
            {
                SendButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        // Esc é uma saída, e sair é sair de tudo. Escondia só a conversa com Hide(): com o
        // painel lateral aberto, ele ficava na tela sozinho — o mesmo defeito que o atalho e a
        // perda de foco já resolvem por EsconderTudo.
        if (e.Key == Key.Escape) EsconderTudo();
    }

    private void CommandsList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Tratado no InputBox_PreviewKeyDown quando focado, mas como fallback:
        if (e.Key == Key.Enter || e.Key == Key.Tab)
        {
            if (CommandsList.SelectedItem is string cmd)
            {
                InputBox.Text = cmd + " ";
                InputBox.CaretIndex = InputBox.Text.Length;
                CommandsPopup.IsOpen = false;
                e.Handled = true;
                InputBox.Focus();
            }
        }
    }

    private System.Windows.Threading.DispatcherTimer? _timerDaFaixa;

    /// <summary>Bate de segundo em segundo enquanto uma espera longa está na faixa.</summary>
    private System.Windows.Threading.DispatcherTimer? _relogioDaFaixa;

    /// <summary>Quando a espera começou, para o contador de decorrido.</summary>
    private DateTime _esperaComecou;

    /// <summary>A frase da espera sem o tempo — o tempo é concatenado a cada batida.</summary>
    private string _fraseDaEspera = "";

    /// <summary>O que o botão da faixa faz agora. Nulo quando não há botão.</summary>
    private Action? _acaoDaFaixa;

    /// <summary>
    /// Uma espera LONGA na faixa de sistema, com anel girando, tempo decorrido e saída.
    /// <para>
    /// É o par do aviso de aquecimento, e existe pelo mesmo motivo: numa máquina que faz
    /// prefill a ~30 tok/s, uma espera de cinco minutos sem sinal nenhum é indistinguível de um
    /// travamento. O anel diz que está vivo; o tempo diz quanto já custou; o botão devolve a
    /// decisão a quem está esperando.
    /// </para>
    /// <para>
    /// NÃO some sozinha: quem abre, fecha. Uma espera que desaparece por conta própria com o
    /// trabalho ainda rodando volta a mentir sobre travamento.
    /// </para>
    /// </summary>
    private void MostrarEspera(string frase, string rotuloDaAcao, Action acao)
    {
        if (StatusBar == null || StatusText == null) return;

        _timerDaFaixa?.Stop();

        _fraseDaEspera = frase;
        _esperaComecou = DateTime.Now;
        _acaoDaFaixa = acao;

        AtualizarDecorrido();

        AcaoDaFaixa.Content = rotuloDaAcao;
        AcaoDaFaixa.IsEnabled = true;
        AcaoDaFaixa.Visibility = Visibility.Visible;

        AnelDaFaixa.Visibility = Visibility.Visible;
        Girar(GiroDaFaixa, true);

        StatusBar.Visibility = Visibility.Visible;

        _relogioDaFaixa?.Stop();
        _relogioDaFaixa = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _relogioDaFaixa.Tick += (_, _) => AtualizarDecorrido();
        _relogioDaFaixa.Start();
    }

    private void AtualizarDecorrido()
    {
        var d = DateTime.Now - _esperaComecou;

        string tempo = d.TotalMinutes >= 1
            ? $"{(int)d.TotalMinutes}min{d.Seconds:00}"
            : $"{d.Seconds}s";

        StatusText.Text = $"{_fraseDaEspera} · {tempo}";
    }

    /// <summary>Fecha a espera. Só mexe na faixa se ela ainda for a da espera.</summary>
    public void EsconderEspera()
    {
        if (StatusBar == null) return;
        if (_acaoDaFaixa == null) return;   // outra coisa tomou a faixa; não é nossa para fechar

        _relogioDaFaixa?.Stop();
        _relogioDaFaixa = null;
        _acaoDaFaixa = null;

        Girar(GiroDaFaixa, false);
        AnelDaFaixa.Visibility = Visibility.Collapsed;
        AcaoDaFaixa.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
    }

    private void AcaoDaFaixa_Click(object sender, RoutedEventArgs e)
    {
        // Desabilita ANTES de agir: interromper duas vezes não interrompe mais, e o botão
        // seguir clicável depois do primeiro clique sugere que o primeiro não pegou.
        AcaoDaFaixa.IsEnabled = false;
        StatusText.Text = "Interrompendo…";

        var acao = _acaoDaFaixa;
        acao?.Invoke();
    }

    /// <summary>
    /// O giro de §4.2: 360° em 0,9s, linear, para sempre. Um lugar só — a faixa e o botão
    /// "Recarregar" de §3.11 usam o MESMO movimento, e duas cópias divergiriam na primeira
    /// mudança feita só de um lado.
    /// </summary>
    private static void Girar(System.Windows.Media.RotateTransform? alvo, bool ligado)
    {
        if (alvo == null) return;

        if (!ligado)
        {
            alvo.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            alvo.Angle = 0;
            return;
        }

        alvo.BeginAnimation(
            System.Windows.Media.RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new Duration(TimeSpan.FromSeconds(0.9)),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            });
    }

    /// <summary>
    /// A compactação começou um capítulo ou um arco — §5.4.
    /// <para>
    /// Cada um é UMA chamada ao modelo, e nesta máquina isso é minutos. O aviso existe porque
    /// até aqui a espera acontecia em silêncio, atrás do portão, e a única saída era um prazo
    /// automático que cortava trabalho válido.
    /// </para>
    /// </summary>
    public void AnunciarCompactacao(ConversationService.PassoDaCompactacao passo)
    {
        string frase = passo.Feitos > 0
            ? $"Compactando a memória — {passo.Fase} {passo.Numero + 1} ({passo.Feitos} pronto(s))"
            : $"Compactando a memória — {passo.Fase} {passo.Numero + 1}";

        MostrarEspera(frase, "Interromper", () => _conversation.InterromperCompactacao());
    }


    /// <summary>
    /// Uma frase curta na faixa de sistema (§5.4), que some sozinha.
    /// <para>
    /// É o lugar de "não consegui reler este e-mail": não é fala da IA e não pode virar bolha,
    /// porque entraria no histórico como se o modelo tivesse dito.
    /// </para>
    /// <para>
    /// Ela ESCREVE por cima do aquecimento, se os dois coincidirem. É um empate improvável —
    /// reler exige caixa conectada e janela aberta — e o aquecimento reescreve a faixa sozinho
    /// quando termina.
    /// </para>
    /// </summary>
    private void MostrarFaixa(string texto, int segundos = 6)
    {
        if (StatusBar == null || StatusText == null) return;

        StatusText.Text = texto;
        StatusBar.Visibility = Visibility.Visible;

        _timerDaFaixa?.Stop();
        _timerDaFaixa = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(segundos)
        };

        _timerDaFaixa.Tick += (_, _) =>
        {
            _timerDaFaixa?.Stop();
            StatusBar.Visibility = Visibility.Collapsed;
        };

        _timerDaFaixa.Start();
    }

    /// <summary>Quanto da altura da janela a caixa de digitação pode ocupar.</summary>
    public const double FracaoDaDigitacao = 0.5;

    /// <summary>
    /// A altura máxima da caixa de digitação: metade da janela. Passou disso, ela rola por
    /// dentro. O teto fixo de 110 mostrava cinco linhas de um texto colado de cinquenta.
    /// </summary>
    public static double TetoDaDigitacao(double alturaDaJanela) =>
        Math.Max(32, alturaDaJanela * FracaoDaDigitacao);

    private void AjustarTetoDaDigitacao()
    {
        // MainAreaGrid é o quadro visível: a Window é maior, por causa da margem da sombra.
        if (MainAreaGrid.ActualHeight > 0) InputBox.MaxHeight = TetoDaDigitacao(MainAreaGrid.ActualHeight);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Desinscrever o Deactivated na unha era o padrão ANTIGO, o que fez o ModalGuard
        // existir: a regra fica num lugar só, e quem abre modal apenas declara que abriu. Na
        // unha, uma exceção dentro do ShowDialog deixaria o chat sem o handler para sempre.
        using (ModalGuard.Enter())
        {
            var settingsWin = new SettingsWindow(_settingsService);
            settingsWin.Owner = this;
            settingsWin.SimularPrimeiroEnvio = () => _conversation.SimularPrimeiroEnvio();
            settingsWin.ShowDialog();
        }

        // Settings podem ter mudado a flag Shadow Assistant — atualiza o botão.
        // Se o usuário desligou o setting com o Shadow ativo, paramos o serviço.
        ApplyShadowAssistantSetting();
    }

    /// <summary>
    /// O botão do olho fica SEMPRE escondido; a setting só decide se o Shadow antigo, se
    /// estiver rodando, tem de parar e fechar os widgets.
    /// <para>
    /// <c>ShadowAssistantEnabled</c> hoje liga o ORBE (<c>App.AplicarEstadoDoOrbe</c>), e não
    /// este Shadow antigo de captura de tela. Mostrar o botão com a chave ligada oferecia a
    /// quem acabou de ligar o orbe um segundo "Shadow" — o serviço vazio, com o aviso de
    /// captura de tela. O código do botão continua aqui; só não aparece.
    /// </para>
    /// </summary>
    private void ApplyShadowAssistantSetting()
    {
        bool enabled = _settingsService.LoadSettings().ShadowAssistantEnabled;
        BtnToggleShadow.Visibility = Visibility.Collapsed;

        if (!enabled && _isShadowModeEnabled)
        {
            _isShadowModeEnabled = false;
            BtnToggleShadow.Foreground = AIB.Ui.PincelDoTema.De("TextSecondaryBrush", "#FF8B8794");
            ManageShadowState();
        }
    }

    /// <summary>
    /// Põe no cabeçalho o nome que o modelo deu à conversa.
    /// <para>
    /// O evento vem do fim do turno, fora da thread de interface — a titulação roda ainda sob
    /// o portão, na thread do stream.
    /// </para>
    /// </summary>
    private void AplicarTitulo(string titulo)
    {
        Dispatcher.BeginInvoke(new Action(() => ChatTitleText.Text = titulo));
    }

    /// <summary>
    /// Remonta o painel quando o histórico arquivado muda.
    /// <para>
    /// A conversa é arquivada a cada turno, mas o painel lê o arquivo só ao montar. Sem este
    /// aviso, a conversa em andamento só aparecia na lista depois de fechar e reabrir o painel,
    /// e o nome dado pelo modelo chegava um turno atrasado.
    /// </para>
    /// <para>
    /// Só quando o painel está na tela: remontar uma janela escondida é trabalho para ninguém
    /// ver, e ela remonta sozinha ao abrir.
    /// </para>
    /// </summary>
    private void AtualizarPainelDeHistorico()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_painel is { IsVisible: true }) _painel.Recarregar();
        }));
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => NovaConversa();

    /// <summary>
    /// Começa uma conversa do zero. A anterior NÃO se perde: <c>ResetHistory</c> arquiva o que
    /// havia antes de zerar, e abre uma sessão nova em <c>memory/sessions</c>.
    /// </summary>
    private void NovaConversa()
    {
        DescartarConfirmacaoPendente();
        MessagesPanel.Children.Clear();
        _cadeiaAtual = null;
        _conversation.ResetHistory();

        // O registro é da CONVERSA (§6.4). Sem limpar, a conversa nova herdava na aba as ações
        // da anterior, que já foram arquivadas com ela.
        ActionLogService.Clear();

        // Os anexos também são da conversa — "arquivos no contexto desta conversa". Sem limpar,
        // eles entravam no prompt da conversa seguinte (RenderizarAnexados), sobre um assunto
        // em que ninguém os anexou. Os RECENTES ficam: são histórico, não estado.
        ContextService.Clear();

        int userLevel = LevelService.GetLevel(_settingsService.LoadSettings().MessageCount);
        int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
        UpdateTokenCounterUI(new TokenReport(0, 0, maxTokens));

        ChatTitleText.Text = "Nova conversa";
        AtualizarEstadoVazio();
    }

    /// <summary>
    /// Cor do contador por ECONOMIA de contexto, nao por ocupacao — §3.8.
    /// <para>
    /// A tela antiga pintava de verde a laranja conforme o historico enchia. A spec inverte o
    /// que o numero comunica. O que ele mede mudou de novo: era a fatia do prompt que o cache
    /// do Ollama nao precisou reprocessar, e agora e quanto o sistema de capitulos e atos
    /// esta poupando. Verde quer dizer "a memoria esta trabalhando"; magenta, "a conversa vai
    /// quase inteira em toda requisicao".
    /// </para>
    /// <para>
    /// Sem economia medida a cor e neutra, e nao magenta: antes do primeiro capitulo nao ha
    /// falha nenhuma a sinalizar.
    /// </para>
    /// </summary>
    /// <summary>
    /// A cor da barra mede OCUPAÇÃO do contexto, e não economia.
    /// <para>
    /// Antes ela media a economia, e a escala punia conversa curta: um capítulo que resumiu 200
    /// tokens em 128 fez o trabalho dele e a barra saía MAGENTA, acusando o sistema de falhar.
    /// É a mesma armadilha do "-0%" que a nota do primeiro turno já evitava, um nível acima.
    /// </para>
    /// <para>
    /// Ocupação é acionável: passar de 75% avisa que a compactação vai disparar; passar de 90%
    /// avisa que a poda de emergência está perto — e a poda descarta sem substituto.
    /// </para>
    /// </summary>
    private System.Windows.Media.Brush CorDaOcupacao(TokenReport r)
    {
        // A REDE em primeiro lugar: é o único ponto em que algo é perdido de verdade. Passar
        // dela é a poda de emergência voltando a descartar sem substituto.
        if (r.OcupacaoDaRedePct >= 90) return (System.Windows.Media.Brush)FindResource("DangerTextBrush");

        // Passar do teto do NÍVEL não interrompe nada e virou rotina desde que a janela ficou
        // bem maior que ele. Vale laranja — "vai compactar no fim do turno" — e não vermelho:
        // alarme que dispara todo turno deixa de ser alarme.
        if (r.AcimaDoOrcamento || r.OcupacaoPct >= 90)
            return (System.Windows.Media.Brush)FindResource("WarnBrush");

        return (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
    }

    /// <summary>
    /// Escreve o contador do rodape: quanto a conversa inteira pesaria, quanto ela pesa agora
    /// e quanto o sistema de capitulos e atos esta poupando.
    /// <para>
    /// A seta so aparece quando ha compactacao. Antes do primeiro capitulo os dois numeros sao
    /// o mesmo, e escrever "1.204 &gt; 1.204" seria ocupar o rodape para nao dizer nada. Sem
    /// economia medida o texto tambem nao ganha porcentagem nem cor: um "-0%" magenta no
    /// comeco da conversa acusaria o sistema de falhar quando ele so ainda nao teve trabalho.
    /// </para>
    /// </summary>
    private void UpdateTokenCounterUI(TokenReport relatorio)
    {
        Dispatcher.Invoke(() =>
        {
            // O teto do nivel no lugar da porcentagem. Os dois numeros da esquerda ja dizem
            // quanto foi poupado — a porcentagem repetia isso em outra forma, e ocupava o
            // espaco do unico dado que faltava: o quanto ainda cabe.
            //
            // A condição é o TOTAL diferir do contexto, e não haver economia. Uma conversa
            // reaberta sem capítulo nenhum não poupou nada, mas o custo cru dela continua sendo
            // maior que o contexto — e esconder isso é o que fazia 9.144 tokens virarem 1.838
            // sem explicação.
            bool temTotal = relatorio.Total > relatorio.Contexto;

            TokenCounterText.Inlines.Clear();

            if (temTotal)
            {
                // TACHADO e apagado: é o preço que a conversa NÃO está pagando. Riscar diz isso
                // sem precisar de legenda, e deixa o número vivo ser o que salta aos olhos.
                TokenCounterText.Inlines.Add(new Run($"{relatorio.Total:N0}")
                {
                    TextDecorations = System.Windows.TextDecorations.Strikethrough,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush")
                });

                // A seta fica. O risco diz que aquele preço não está sendo pago; a seta diz
                // que um número VIROU o outro. São duas informações, não uma repetida.
                TokenCounterText.Inlines.Add(new Run(" > "));
            }

            TokenCounterText.Inlines.Add(new Run(
                $"{relatorio.Contexto:N0} tokens | {relatorio.Max:N0}"));

            // O dinheiro, quando há. Só o OpenRouter cobra; no Ollama o campo é nulo e o rodapé
            // continua como sempre foi.
            if (relatorio.CustoUsd is decimal custo)
                TokenCounterText.Inlines.Add(new Run($" | {TokenReport.Dolares(custo)}"));

            TokenCounterText.Foreground = CorDaOcupacao(relatorio);

            // A conta atrás do número. Dois números e uma cor respondem "está economizando?",
            // e não respondem "de onde vem isso?" — que é a pergunta do dia em que a conta
            // parece errada. A dica não ocupa espaço na barra e está sempre a um mouse de
            // distância; o /memoria mostra o mesmo capítulo por capítulo.
            TokenCounterText.ToolTip = DicaDoContador(relatorio);
        });
    }

    /// <summary>
    /// O texto da dica do contador: as parcelas da conta, sem o detalhe por capítulo.
    /// <para>
    /// Enquanto nada foi compactado ela diz isso com todas as letras. Uma dica vazia, ou uma
    /// dica cheia de zeros, faria o sistema parecer quebrado justamente quando ele só ainda
    /// não teve trabalho.
    /// </para>
    /// </summary>
    private static string DicaDoContador(TokenReport r)
    {
        string gasto = r.CustoUsd is decimal custo
            ? $"gasto na conversa ...... {TokenReport.Dolares(custo)}\n\n"
            : "";

        if (r.Capitulos == 0 && r.Atos == 0)
            return $"Nada compactado ainda.\n"
                 + $"No prompt: {r.Contexto:N0} de {r.Max:N0} tokens.\n\n"
                 + gasto
                 + "/memoria mostra a conta; /compact compacta agora.";

        var texto = new StringBuilder();
        texto.Append("ECONOMIA DA MEMÓRIA").Append('\n').Append('\n');
        texto.Append($"{r.Capitulos} capítulo(s), {r.Atos} ato(s)").Append('\n').Append('\n');
        texto.Append($"conversa crua resumida .. {r.Cru,8:N0}").Append('\n');
        texto.Append($"memória no prompt ....... {r.Memoria,8:N0}").Append('\n');

        // As parcelas da faixa. Três números que não fecham entre si fazem quem confere
        // desistir de confiar no contador inteiro.
        if (r.DiferencaDaFaixa > 0)
        {
            texto.Append($"  capítulos e atos ...... {r.MemoriaDosRegistros,8:N0}").Append('\n');
            texto.Append($"  cabeçalho do bloco .... {r.DiferencaDaFaixa,8:N0}").Append('\n');
        }
        else if (r.DiferencaDaFaixa < 0)
        {
            texto.Append($"  capítulos e atos ...... {r.MemoriaDosRegistros,8:N0}").Append('\n');
            texto.Append($"  não coube na cota ..... {-r.DiferencaDaFaixa,8:N0}").Append('\n');
        }

        texto.Append($"poupado ................. {r.Economia,8:N0}");

        if (r.EconomiaPct is int pct) texto.Append($"  ({pct}%)");
        texto.Append('\n');

        if (r.Descartado > 0)
            texto.Append($"descartado ao reabrir ... {r.Descartado,8:N0}").Append('\n')
                 .Append("  (ferramentas: só as falas voltam)").Append('\n');

        texto.Append('\n');
        texto.Append($"custo cru da conversa ... {r.Total,8:N0}").Append('\n');
        texto.Append($"vai ao modelo agora ..... {r.Contexto,8:N0}").Append('\n');
        texto.Append($"teto deste nível ........ {r.Max,8:N0}")
             .Append($"  ({r.OcupacaoPct}% ocupado)").Append('\n');

        // O teto do nível é PLACAR: passar dele não interrompe nada. Sem dizer isso, um número
        // acima de 100% na dica parece defeito.
        texto.Append(r.AcimaDoOrcamento
            ? "acima do orçamento do nível — a conversa segue, e a compactação roda no fim do turno"
            : "o teto do nível não interrompe nada: é o ponto em que a compactação passa a agir");

        if (r.Rede > 0)
            texto.Append('\n').Append('\n')
                 .Append($"janela do modelo ........ {r.Rede,8:N0}").Append('\n')
                 .Append("(daí em diante a poda descarta sem substituto)");

        if (!r.MedidaCompleta)
            texto.Append('\n').Append('\n')
                 .Append("Capítulos antigos sem medida: o poupado é um piso.");

        texto.Append('\n').Append('\n').Append(gasto).Append("/memoria mostra capítulo por capítulo.");
        return texto.ToString();
    }

    /// <summary>
    /// Roda um comando de memoria e devolve a resposta numa bolha.
    /// <para>
    /// Trava a interface como um turno normal, e pelo mesmo motivo: o resumidor pode levar ate
    /// quatro minutos, e nesse tempo ele segura o portao do turno. Sem travar, a mensagem
    /// seguinte do usuario ficaria parada esperando sem nenhum sinal na tela.
    /// </para>
    /// <para>
    /// A frase mostrada vem do servico, inclusive quando ele recusa. Uma recusa muda de motivo
    /// — nao ha turno fechado, so ha um capitulo solto, o resumidor estourou o tempo — e
    /// traduzir tudo para "nao foi possivel" aqui apagaria justamente o que o usuario precisa
    /// saber para tentar de outro jeito.
    /// </para>
    /// </summary>
    private async Task RodarComandoDeMemoria(string comando, Func<int, Task<string>> acao)
    {
        InputBox.Clear();
        AddUserBubble(comando);

        _isSending = true;
        InputBox.IsEnabled = false;

        var pensando = AddTypingIndicator();
        ChatScrollViewer.ScrollToEnd();

        string resposta;
        try
        {
            int nivel = LevelService.GetLevel(_settingsService.LoadSettings().MessageCount);
            resposta = await acao(nivel);
        }
        catch (Exception ex)
        {
            resposta = $"O comando falhou: {ex.Message}";
        }
        finally
        {
            pensando.timer.Stop();

            // RemoverLinha, e nao Children.Remove: a bolha mora dentro de um Grid de linha, e
            // remove-la direto do painel falha EM SILENCIO porque ela nao e filha dele. O
            // indicador ficava na tela para sempre — a mesma armadilha que o proprio
            // RemoverLinha foi escrito para consertar.
            RemoverLinha(pensando.bubble);

            InputBox.IsEnabled = true;
            InputBox.Focus();
            _isSending = false;
        }

        AddAgentBubble(resposta);
        AtualizarPainelDeHistorico();
        ChatScrollViewer.ScrollToEnd();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => EsconderTudo();

    // ─────────────────────────────────────────────────────────────────────────
    // Side Panel (Dashboard) Handlers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O painel lateral é uma segunda JANELA (§6), e não um filho desta.
    /// <para>
    /// Como filho, ele dividia a largura com o chat: a janela de conversa encolhia ao abrir o
    /// painel, que é exatamente a armadilha A2. Agora a de chat tem largura fixa e o painel
    /// aparece ao lado, alinhado pela base.
    /// </para>
    /// </summary>
    private SidePanelWindow? _painel;

    /// <summary>
    /// Se o painel deve reaparecer junto com a conversa.
    /// <para>
    /// Separado de <c>IsVisible</c> porque os dois somem juntos ao perder o foco: na volta,
    /// perguntar se o painel está visível responderia sempre "não", e ele nunca voltaria. Este
    /// campo guarda a INTENÇÃO do usuário, não o estado da janela.
    /// </para>
    /// </summary>
    private bool _painelAberto;

    private void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_painel is { IsVisible: true })
        {
            _painelAberto = false;
            _painel.Hide();
            return;
        }

        _painelAberto = true;

        if (_painel == null)
        {
            _painel = new SidePanelWindow(
                RecuperarChat, AbrirChat, ExcluirChat, () => _conversation.SessionId)
            {
                Owner = this
            };

            // A janela é criada uma vez e escondida, nunca fechada pelo botão: recriar a cada
            // abertura perderia a aba selecionada e a posição que o usuário escolheu.
            _painel.Closed += (_, _) => _painel = null;

            // O painel entra na MESMA regra de foco da conversa: clicar fora dos dois esconde
            // os dois. Sem isto, clicar fora com o painel em foco deixaria o painel na tela.
            _painel.Deactivated += (_, _) => Window_Deactivated(null, EventArgs.Empty);

            // Fechar pelo X do painel é decisão do usuário: ele não volta sozinho depois.
            _painel.FechadoPeloUsuario += () => _painelAberto = false;
        }

        PosicionarPainel();
        _painel.Recarregar();
        _painel.Show();
    }

    /// <summary>Encosta o painel na direita da janela de chat, alinhado pela BASE.</summary>
    private void PosicionarPainel()
    {
        if (_painel == null) return;

        _painel.Left = Left + Width - 10;
        _painel.Top = Top + Height - _painel.Height;
    }

    /// <summary>
    /// Restaura uma conversa do histórico — §6.1. O painel permanece ABERTO depois da troca.
    /// </summary>
    private void RecuperarChat(ChatSession sessao)
    {
        if (string.IsNullOrWhiteSpace(sessao.Content)) return;

        _conversation.AppendRecoveredContext(sessao.Title, sessao.Content);

        AddUserBubble($"Recuperando contexto: {sessao.Title}");
        AddAgentBubble("Contexto antigo carregado com sucesso. Como deseja continuar?");

        ChatTitleText.Text = string.IsNullOrWhiteSpace(sessao.Title) ? "Conversa recuperada" : sessao.Title;
        ChatScrollViewer.ScrollToEnd();
    }

    /// <summary>
    /// Abre uma conversa do histórico no lugar da atual — botão direito, "Abrir conversa".
    /// <para>
    /// Não é o mesmo que recuperar contexto, e a diferença é a razão de existirem as duas.
    /// Recuperar SOMA: a conversa antiga entra como material dentro da que está na tela.
    /// Abrir SUBSTITUI: a conversa antiga volta a ser a conversa, com cada fala no seu balão e
    /// no seu papel dentro do histórico do modelo.
    /// </para>
    /// <para>
    /// A conversa que estava aberta não se perde: o ResetHistory lá dentro a arquiva antes.
    /// </para>
    /// </summary>
    private void AbrirChat(ChatSession sessao)
    {
        var falas = ChatHistoryService.Parse(sessao.Content);
        if (falas.Count == 0) return;

        RestaurarConversaGravada(sessao, falas);

        AtualizarEstadoVazio();
        ChatScrollViewer.ScrollToEnd();

        // O painel continua aberto, mas a lista mudou de posição: a conversa que estava na
        // tela foi arquivada e agora é o item mais recente.
        _painel?.Recarregar();
    }

    /// <summary>
    /// Põe uma conversa gravada de volta no lugar da atual: no modelo, no registro de ações, nas
    /// bolhas, no título e no contador.
    /// <para>
    /// UM caminho para "Abrir conversa" (<see cref="AbrirChat"/>) e para a reabertura de um
    /// e-mail (<c>RetomarConversaDoEmail</c>). Eram duas cópias, e a do e-mail já tinha
    /// esquecido o título: reabrir um e-mail deixava no cabeçalho o nome da conversa anterior.
    /// </para>
    /// </summary>
    /// <param name="cartaoNoLugarDaPrimeiraFala">
    /// Quando presente, entra no lugar da PRIMEIRA fala, se ela for do usuário — é o
    /// enquadramento do e-mail, montado pelo programa, e não algo que o usuário escreveu.
    /// </param>
    private void RestaurarConversaGravada(
        ChatSession sessao, IReadOnlyList<ChatTurn> falas,
        Func<FrameworkElement>? cartaoNoLugarDaPrimeiraFala = null)
    {
        DescartarConfirmacaoPendente();

        _conversation.LoadConversation(falas, sessao.MemorySessionId, sessao.Id);

        // O registro de ações vive só em memória: volta remontado do raw.jsonl da conversa.
        // Conversa gravada antes da ligação com a memória não tem raw, e a aba fica vazia.
        ActionLogService.Restaurar(ActionLogService.Reconstruir(_conversation.TurnosGravados()));

        MessagesPanel.Children.Clear();
        _cadeiaAtual = null;

        bool primeira = true;

        foreach (var fala in falas)
        {
            if (primeira && fala.DoUsuario && cartaoNoLugarDaPrimeiraFala != null)
            {
                MessagesPanel.Children.Add(cartaoNoLugarDaPrimeiraFala());
                primeira = false;
                continue;
            }

            primeira = false;

            if (fala.DoUsuario) AddUserBubble(fala.Texto);
            else foreach (var parte in QuebraDeFala.Dividir(fala.Texto)) AddAgentBubble(parte);
        }

        ChatTitleText.Text = string.IsNullOrWhiteSpace(sessao.Title) ? "Conversa recuperada" : sessao.Title;

        UpdateTokenCounterUI(_conversation.CurrentTokenReport);
    }

    /// <summary>
    /// Apaga uma conversa do histórico — botão direito, "Excluir".
    /// <para>
    /// Passa por confirmação porque não há desfazer: o arquivo é reescrito sem a entrada. O
    /// que está na tela não é tocado, mesmo que seja a conversa excluída — apagar o registro
    /// não é apagar o que o usuário está lendo.
    /// </para>
    /// </summary>
    private void ExcluirChat(ChatSession sessao)
    {
        string titulo = string.IsNullOrWhiteSpace(sessao.Title) ? "(sem título)" : sessao.Title;

        bool confirmado;
        using (ModalGuard.Enter())
        {
            confirmado = ConfirmDialog.Perguntar(
                _painel ?? (Window)this,
                "Excluir esta conversa do histórico?",
                "A conversa sai do histórico e não é possível recuperá-la. O que está na tela "
                + "agora não é alterado.",
                ferramenta: "histórico",
                alvo: titulo,
                dica: "não há desfazer");
        }

        if (!confirmado) return;

        ChatHistoryService.DeleteSession(sessao.Id);
        _painel?.Recarregar();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Uma pergunta pendente vira recusa: fechar a janela não autoriza nada.
        DescartarConfirmacaoPendente();
        _conversation?.ResetHistory();
        _voiceService?.Dispose();
        _shadowService?.Stop();
        CloseAllShadowWidgets();
        base.OnClosed(e);
    }

    private void BtnToggleShadow_Click(object sender, RoutedEventArgs e)
    {
        if (!_isShadowModeEnabled)
        {
            MessageBoxResult result;
            using (ModalGuard.Enter())
            {
                result = System.Windows.MessageBox.Show(
                    "O Shadow Assistant roda em segundo plano capturando o texto da sua tela e tentando prever o que você precisa.\n\n" +
                    "Como é uma função Alpha, a AIB às vezes pode alucinar ou interpretar a tela erroneamente.\n\n" +
                    "Tem certeza que deseja ativar o monitoramento em segundo plano?",
                    "Shadow Assistant (Alpha)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            }

            if (result == MessageBoxResult.Yes)
            {
                _isShadowModeEnabled = true;
                BtnToggleShadow.Foreground = AIB.Ui.PincelDoTema.De("AccentLilacBrush", "#FFC79EF0");
                ManageShadowState();
            }
        }
        else
        {
            _isShadowModeEnabled = false;
            BtnToggleShadow.Foreground = AIB.Ui.PincelDoTema.De("TextSecondaryBrush", "#FF8B8794");
            ManageShadowState();
        }
    }

    private void ChatWindow_StateChanged(object? sender, EventArgs e)
    {
        ManageShadowState();
    }

    private void ChatWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ManageShadowState();
    }

    private void ManageShadowState()
    {
        if (!_isShadowModeEnabled)
        {
            _shadowService.Stop();
            CloseAllShadowWidgets();
            return;
        }

        if (this.Visibility == Visibility.Visible && this.WindowState == WindowState.Normal)
        {
            _shadowService.Stop();
            foreach (var w in _shadowWidgets) w.Hide();
        }
        else
        {
            EnsureShadowWidgetsForAllScreens();
            foreach (var w in _shadowWidgets) w.Show();

            // Sincroniza opacidade inicial baseada na tela do cursor agora
            _activeScreenIndex = ShadowAssistantService.GetCurrentScreenIndex();
            ApplyActiveScreenOpacity();

            _shadowService.Start();
        }
    }

    private void EnsureShadowWidgetsForAllScreens()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (_shadowWidgets.Count == screens.Length) return; // já está OK

        // Configuração mudou (monitor conectado/desconectado): recria do zero
        CloseAllShadowWidgets();

        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var widget = new ShadowWidget();

            // Posiciona centralizado horizontal na WorkingArea da tela, flutuando 40px da base
            widget.Left = screen.WorkingArea.X + (screen.WorkingArea.Width / 2.0) - (widget.Width / 2.0);
            widget.Top = screen.WorkingArea.Bottom - widget.Height - 40;

            widget.SetActiveState(false); // todos começam inativos (0.3)

            // Registra HWND no Shadow para não fazer auto-OCR do próprio widget
            widget.SourceInitialized += (s, args) =>
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(widget);
                _shadowService.RegisterOwnWindow(helper.Handle);
            };

            _shadowWidgets.Add(widget);
        }
    }

    private void CloseAllShadowWidgets()
    {
        foreach (var w in _shadowWidgets)
        {
            try { w.Close(); } catch { }
        }
        _shadowWidgets.Clear();
        _activeScreenIndex = -1;
    }

    private void ApplyActiveScreenOpacity()
    {
        for (int i = 0; i < _shadowWidgets.Count; i++)
        {
            _shadowWidgets[i].SetActiveState(i == _activeScreenIndex);
        }
    }

    private void OnActiveScreenChanged(int screenIdx)
    {
        Dispatcher.Invoke(() =>
        {
            _activeScreenIndex = screenIdx;
            ApplyActiveScreenOpacity();
        });
    }

    private void OnShadowSuggestion(string suggestion)
    {
        Dispatcher.Invoke(() =>
        {
            // Mostra o balão APENAS no widget da tela ativa (a do cursor)
            if (_activeScreenIndex >= 0 && _activeScreenIndex < _shadowWidgets.Count)
            {
                var target = _shadowWidgets[_activeScreenIndex];
                if (target.IsVisible) target.ShowSuggestion(suggestion);
            }
        });
    }
}
