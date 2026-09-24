using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

// WinForms entra junto com o WPF em net8.0-windows e traz homonimos. Sem os aliases,
// KeyEventArgs e TextChangedEventArgs sao ambiguos.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using Brush = System.Windows.Media.Brush;
using AIB.Services;
using AIB.Ui;
using System.Collections.ObjectModel;
using Microsoft.Win32;

namespace AIB.Views;

/// <summary>
/// O orbe do Shadow Assistant — refactor-interface/shadow-assistant.html.
/// <para>
/// Passos P1–P4 da §-1: a janela (§4.1), o posicionamento (§1), a casca que morfa (§4.2), o
/// glyph (§4.3), o hover (§4.4), o estado Idle (§5.1) e a animação de morph (§6). O pulso, a
/// trilha de ações, o balão de fala e a lista de e-mails ainda não existem.
/// </para>
/// </summary>
public partial class ShadowAssistantWindow : Window
{
    /// <summary>
    /// Espaço vazio em volta do desenho, para a sombra ter onde ser desenhada.
    /// <para>
    /// O WPF recorta <c>Effect</c> no limite da janela. A barShadow tem 44 de blur com 16 de
    /// deslocamento para baixo, então sem esta margem a sombra sairia cortada em linha reta —
    /// o defeito clássico de janela sem chrome. Precisa casar com o Margin do Palco no XAML.
    /// </para>
    /// <para>
    /// É também o teto da onda do pulso: ela cresce por RenderTransform, que não mexe no
    /// layout, mas a janela recorta o que passar dela. Com a onda indo a
    /// <see cref="ExpansaoDoPulso"/> vezes 61px, o raio máximo passa de 71px — 40 de margem
    /// cortaria a borda da onda nos últimos quadros.
    /// </para>
    /// </summary>
    private const double MargemDaSombra = 56;

    /// <summary>Duração do morph de ida — §6. O foco só vai para o campo no fim dela.</summary>
    private static readonly TimeSpan DuracaoDoMorph = TimeSpan.FromSeconds(0.28);

    /// <summary>Largura interna do orbe: 56 menos os 1,5 de borda de cada lado.</summary>
    private const double LarguraInternaDoOrbe = 53;

    /// <summary>Recuo do glyph na barra — §4.7, o padding esquerdo do conteúdo.</summary>
    private const double MargemNaBarra = 16;

    /// <summary>
    /// Até onde a onda do pulso cresce. A §5.2 pede 1,8; este valor é 30% maior, a pedido.
    /// Quem mexer aqui precisa conferir a <see cref="MargemDaSombra"/> junto: a onda é
    /// recortada pelo limite da janela.
    /// </summary>
    private const double ExpansaoDoPulso = 2.34;

    private bool _emModoBarra;
    private bool _fechando;

    /// <summary>
    /// Nome do personagem ativo, para o placeholder da barra (§4.7 b). Vem de fora: a janela
    /// não conhece SettingsService, e não precisa — ela desenha, quem sabe quem está ativo é
    /// quem a criou.
    /// </summary>
    public string NomeDoAgente
    {
        get => _nomeDoAgente;
        set
        {
            _nomeDoAgente = string.IsNullOrWhiteSpace(value) ? "AIB" : value.Trim();
            Dica.Text = $"Fale com o {_nomeDoAgente}...";
        }
    }

    private string _nomeDoAgente = "AIB";

    /// <summary>
    /// Quantos e-mails cabem na fala antes de o resto virar uma linha de texto. Vem de fora
    /// pelo mesmo motivo do nome: a janela desenha, e quem sabe o que está configurado é quem
    /// a criou.
    /// </summary>
    public int TetoDeEmails { get; set; } = Ui.FalaDaIA.TetoDeEmails;

    /// <summary>
    /// Se o Shadow deve estar na tela agora.
    /// <para>
    /// Ele some enquanto a conversa está aberta. Os dois juntos disputam a mesma atenção e o
    /// mesmo canto da tela, e o Shadow existe justamente para as horas em que a conversa NÃO
    /// está aberta — é a porta de entrada, não um segundo lugar para falar com a mesma IA.
    /// </para>
    /// <para>
    /// Função pura porque a decisão é chamada de três lugares diferentes — o atalho global, a
    /// chave das configurações e o visto da bandeja — e três cópias da mesma condição
    /// divergiriam na primeira correção feita num deles.
    /// </para>
    /// </summary>
    public static bool DeveAparecer(bool ligado, bool conversaNaTela) => ligado && !conversaNaTela;

    /// <summary>
    /// Se o orbe deve REPETIR a resposta de um turno, ou só mostrar o estado dele.
    /// <para>
    /// O orbe é a janela do que a AIB está fazendo, e não um segundo lugar onde ela fala. Com a
    /// conversa na tela, quem mostra a resposta é ela: o orbe repetindo a mesma frase num balão
    /// sobre o desktop diria duas vezes a mesma coisa, e a segunda vez apareceria escondida
    /// atrás da janela que já tinha a primeira.
    /// </para>
    /// <para>
    /// As DUAS exceções, que são o desenho inteiro: o turno pedido pela BARRA do orbe — quem
    /// perguntou ali está olhando para ali —, e a conversa fora da tela, quando o balão do orbe
    /// é o único lugar onde a resposta pode aparecer.
    /// </para>
    /// <para>
    /// Função pura pelo mesmo motivo de <see cref="DeveAparecer"/>: a decisão vale para o fim
    /// de todo turno, e uma cópia dela escrita à mão no meio de um handler é a que envelhece
    /// errado.
    /// </para>
    /// </summary>
    public static bool OrbeDeveFalar(bool turnoVeioDoOrbe, bool conversaNaTela) =>
        turnoVeioDoOrbe || !conversaNaTela;

    /// <summary>
    /// Texto enviado pela barra. §5.3: a conversa continua na janela de chat, não aqui — a
    /// barra é porta de entrada, não um segundo chat.
    /// </summary>
    public event Action<string>? MensagemEnviada;

    /// <summary>Se há um turno em andamento disparado por esta barra.</summary>
    public bool Trabalhando { get; private set; }

    /// <summary>Se há fala na tela. Diagnóstico e ensaio.</summary>
    public bool BalaoVisivel => RoloDasFalas.Visibility == Visibility.Visible && _falas.Count > 0;

    /// <summary>Texto da ÚLTIMA fala da IA. Diagnóstico e ensaio.</summary>
    public string TextoDaFala
    {
        get
        {
            for (int i = _falas.Count - 1; i >= 0; i--)
                if (_falas[i] is FalaDaIA fala) return fala.Texto;
            return "";
        }
    }

    /// <summary>
    /// As falas que estão na pilha, da mais antiga para a mais recente. Diagnóstico e ensaio.
    /// </summary>
    public IReadOnlyList<FalaDoOrbe> Falas => _falas;

    private readonly ObservableCollection<FalaDoOrbe> _falas = new();

    /// <summary>Se o orbe está pulsando — §5.2.</summary>
    public bool Pulsando { get; private set; }

    /// <summary>
    /// Falas que a IA quer dizer e que ainda não foram lidas.
    /// <para>
    /// Elas NÃO viram balão sozinhas (§0 O3, §8 A5). Ficam aqui, o orbe pulsa, e o texto só
    /// aparece quando o usuário clica. É o desenho inteiro do aviso que não interrompe: quem
    /// está trabalhando vê o pulso pelo canto do olho e decide quando parar.
    /// </para>
    /// </summary>
    private readonly List<string> _falasPendentes = new();

    /// <summary>Quantas falas esperam ser lidas. Diagnóstico e ensaio.</summary>
    public int FalasPendentes => _falasPendentes.Count;

    public ShadowAssistantWindow()
    {
        InitializeComponent();

        IconeDeInbox.Data = Geometry.Parse(DesenhoDeInbox);
        PilhaDeFalas.ItemsSource = _falas;

        // §1 — os quatro gatilhos de reposicionamento, todos obrigatórios.
        SystemEvents.DisplaySettingsChanged += AoMudarAsTelas;
        SystemParameters.StaticPropertyChanged += AoMudarParametroDoSistema;
        DpiChanged += (_, _) => Reposicionar();
        SizeChanged += (_, _) => Reposicionar();

        Loaded += (_, _) =>
        {
            VisualStateManager.GoToElementState(Palco, "Orbe", false);

            // Sem animação: é a posição de partida, não uma transição.
            AjustarCelulaAoConteudo();

            Reposicionar();
        };

        Campo.LostFocus += (_, _) => AtualizarDica();

        Casca.MouseEnter += (_, _) => Escalar(1.06);
        Casca.MouseLeave += (_, _) => Escalar(1.00);
        Casca.MouseLeftButtonUp += (_, _) => AbrirBarra();

        // §5.3 — clique fora volta ao orbe. Deactivated cobre o caso de o usuário ir
        // trabalhar em outra janela sem clicar em lugar nenhum aqui.
        Deactivated += (_, _) => FecharBarra();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _emModoBarra)
            {
                FecharBarra();
                e.Handled = true;
            }
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // §1  Posicionamento
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recoloca a janela pela regra da §1: monitor primário, centralizada, base do desenho a
    /// 45px acima da barra de tarefas.
    /// </summary>
    private void Reposicionar()
    {
        if (_fechando) return;

        var canto = ScreenAnchorService.Calcular(
            SystemParameters.WorkArea,
            largura: ActualWidth > 0 ? ActualWidth : Width,
            altura: ActualHeight,
            margemInferior: MargemDaSombra);

        Left = canto.X;
        Top = canto.Y;
    }

    private void AoMudarAsTelas(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(Reposicionar));

    private void AoMudarParametroDoSistema(object? sender, PropertyChangedEventArgs e)
    {
        // A barra de tarefas apareceu, sumiu ou mudou de borda.
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
            Dispatcher.BeginInvoke(new Action(Reposicionar));
    }

    /// <summary>
    /// Solta os eventos ESTÁTICOS do sistema.
    /// <para>
    /// <c>SystemEvents</c> e <c>SystemParameters</c> vivem enquanto o processo viver. Uma
    /// janela que assina os dois e não se desassina fica presa na memória para sempre, e cada
    /// abertura do orbe deixaria mais uma cópia recebendo eventos e tentando reposicionar uma
    /// janela que não existe mais.
    /// </para>
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _fechando = true;
        SystemEvents.DisplaySettingsChanged -= AoMudarAsTelas;
        SystemParameters.StaticPropertyChanged -= AoMudarParametroDoSistema;

        // Animação pendente morre junto com a janela. O morph dura 0,28s e o hover 0,15s;
        // fechar o orbe no meio de um deles deixaria o relógio do WPF mirando elementos de uma
        // janela que já foi. Passar null desliga a animação e devolve a propriedade ao valor
        // local.
        EscalaDaCasca.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        EscalaDaCasca.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        Casca.BeginAnimation(WidthProperty, null);
        Casca.BeginAnimation(HeightProperty, null);
        Glyph.BeginAnimation(TextBlock.FontSizeProperty, null);
        CelulaDoGlyph.BeginAnimation(MarginProperty, null);
        GiroDoAnel.BeginAnimation(RotateTransform.AngleProperty, null);
        GiroDoAnelDoOrbe.BeginAnimation(RotateTransform.AngleProperty, null);
        EscalaDoPulso.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        EscalaDoPulso.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AnelDePulso.BeginAnimation(OpacityProperty, null);

        // As bolhas animam por gatilho do próprio template, então não há relógio nomeado
        // para desligar aqui. Esvaziar a fonte tira os elementos da árvore, que é o
        // equivalente: uma animação de 0,18s sem alvo não tem o que segurar.
        _falas.Clear();

        base.OnClosed(e);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // §6  Morph
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Se a casca está na forma de barra. Diagnóstico e ensaio.</summary>
    public bool EmModoBarra => _emModoBarra;

    /// <summary>§6 — o círculo VIRA a barra. Nada abre ao lado.</summary>
    public void AbrirBarra()
    {
        if (_emModoBarra) return;

        _emModoBarra = true;
        Escalar(1.00);
        VisualStateManager.GoToElementState(Palco, "Barra", true);
        AnimarMargemDaCelula(MargemNaBarra, DuracaoDoMorph, EasingMode.EaseOut);
        AtualizarAnelDeProgresso();

        // A pilha volta com o que já estava nela. Fechar a barra ESCONDE as bolhas; só o X
        // descarta. Sem isto, sair da barra por um clique fora apagava a resposta que o
        // usuário tinha acabado de pedir, e reabrir dava uma barra vazia.
        if (_falas.Count > 0) RoloDasFalas.Visibility = Visibility.Visible;

        // §5.4 — clicar num orbe que estava pulsando faz as duas coisas de uma vez: a barra
        // abre E a fala aparece acima dela. É o único caminho pelo qual uma fala proativa
        // chega à tela.
        if (Pulsando) RevelarFalasPendentes();

        // §8 A7 — o foco vai para o campo no FIM da animação. Focar no início faz o cursor
        // piscar dentro de um círculo de 56px enquanto ele ainda está virando barra.
        var relogio = new DispatcherTimer { Interval = DuracaoDoMorph };
        relogio.Tick += (s, _) =>
        {
            relogio.Stop();
            if (_emModoBarra && !_fechando) Campo.Focus();
        };
        relogio.Start();

        // §8 A3 — o orbe só rouba foco quando é CLICADO, nunca ao aparecer ou pulsar.
        // E só se estiver na tela: ativar janela invisível não faz nada de útil e, na thread
        // STA compartilhada dos ensaios, mexe com a janela de outra classe.
        if (IsVisible) Activate();
    }

    /// <summary>§6 reverso — a barra volta a ser círculo.</summary>
    public void FecharBarra()
    {
        if (!_emModoBarra) return;

        _emModoBarra = false;
        VisualStateManager.GoToElementState(Palco, "Orbe", true);
        AnimarMargemDaCelula(MargemQueCentraliza(LarguraDaCelula()), TimeSpan.FromSeconds(0.22), EasingMode.EaseIn);
        AtualizarAnelDeProgresso();

        // §6 — o rascunho curto é descartado ao fechar; o longo sobrevive para a próxima
        // abertura, porque perder um parágrafo digitado por causa de um clique fora seria
        // pior que a barra reabrir com texto velho.
        if (Campo.Text.Trim().Length <= RascunhoPreservadoAcimaDe) Campo.Clear();

        AtualizarDica();

        // As bolhas são ancoradas na barra: sem ela ficariam flutuando sozinhas sobre o
        // desktop, apontando para nada. Some a PILHA, e não o conteúdo dela — reabrir a
        // barra devolve a conversa onde estava.
        RoloDasFalas.Visibility = Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // §4.7  Barra de input
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Acima disto o rascunho sobrevive ao fechamento — §6.</summary>
    private const int RascunhoPreservadoAcimaDe = 40;

    private void Campo_TextChanged(object sender, TextChangedEventArgs e) => AtualizarDica();

    private void AtualizarDica() =>
        Dica.Visibility = string.IsNullOrEmpty(Campo.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void Campo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Shift+Enter quebraria linha, mas a barra é de uma linha só: o campo tem
        // AcceptsReturn=False, então aqui só o Enter puro tem efeito. Quem quer escrever um
        // parágrafo faz isso na janela de chat, que é onde a conversa acontece.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            Enviar();
            e.Handled = true;
        }
    }

    private void BotaoEnviar_Click(object sender, RoutedEventArgs e) => Enviar();

    /// <summary>
    /// §5.3 — manda o texto para a janela de chat e volta ao orbe. A barra não responde nada:
    /// a conversa continua lá, e ter duas telas mostrando pedaços do mesmo diálogo seria a
    /// pior das duas opções.
    /// </summary>
    public void Enviar()
    {
        string texto = Campo.Text.Trim();
        if (texto.Length == 0) return;

        Campo.Clear();
        AtualizarDica();

        // A bolha do usuário entra na pilha. Antes o texto sumia do campo e não reaparecia
        // em lugar nenhum: quem mandava a mensagem ficava olhando para uma barra vazia com um
        // anel girando, sem confirmação do que tinha sido enviado.
        AdicionarFala(new FalaDoUsuario(texto));

        // A barra CONTINUA aberta. §5.4: o usuário lê e pode responder no mesmo lugar,
        // sem abrir o chat — fechar aqui o obrigaria a clicar de novo para ver a resposta
        // que ele acabou de pedir.
        ComecarATrabalhar();
        MensagemEnviada?.Invoke(texto);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // §4.3  Centragem do glyph
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Margem esquerda que deixa o glyph EXATAMENTE no centro do orbe.
    /// <para>
    /// A versão anterior usava 20 fixo, chutado a partir de uma estimativa da largura do "✦".
    /// O chute errou e o símbolo ficava alguns pixels à direita do centro. A largura de um
    /// glyph depende da fonte instalada, do tamanho e do DPI — não é constante e não deve ser
    /// escrita à mão.
    /// </para>
    /// </summary>
    public static double MargemQueCentraliza(double larguraDoGlyph, double larguraInterna = LarguraInternaDoOrbe) =>
        Math.Max(0, (larguraInterna - larguraDoGlyph) / 2);

    /// <summary>Largura real do glyph no tamanho pedido, medida na fonte de verdade.</summary>
    private double MedirGlyph(double tamanhoDaFonte)
    {
        var tipo = new Typeface(Glyph.FontFamily, Glyph.FontStyle, Glyph.FontWeight, Glyph.FontStretch);

        var texto = new FormattedText(
            Glyph.Text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            tipo,
            tamanhoDaFonte,
            System.Windows.Media.Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        return texto.Width;
    }

    /// <summary>Largura do que está ocupando a célula agora: o ícone de inbox ou o símbolo.</summary>
    private double LarguraDaCelula() =>
        IconeDeInbox.Visibility == Visibility.Visible ? IconeDeInbox.Width
        : IconeDaAcao.Visibility == Visibility.Visible ? IconeDaAcao.Width
        : MedirGlyph(20);

    private void AnimarMargemDaCelula(double para, TimeSpan duracao, EasingMode modo)
    {
        var animacao = new ThicknessAnimation(new Thickness(para, 0, 0, 0), duracao)
        {
            EasingFunction = new CubicEase { EasingMode = modo }
        };

        CelulaDoGlyph.BeginAnimation(MarginProperty, animacao);
    }

    /// <summary>
    /// Recoloca a célula sem animação. Usado quando o CONTEÚDO dela muda de tamanho — o
    /// símbolo de 20px vira um ícone de 23 — e a margem que centralizava um não centraliza
    /// mais o outro.
    /// </summary>
    private void AjustarCelulaAoConteudo()
    {
        if (_emModoBarra) return;

        CelulaDoGlyph.BeginAnimation(MarginProperty, null);
        CelulaDoGlyph.Margin = new Thickness(MargemQueCentraliza(LarguraDaCelula()), 0, 0, 0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // §5.6 / §5.7  E-mail
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Ícone de caixa de entrada, 23px, no lugar do glyph durante a varredura.</summary>
    private const string DesenhoDeInbox =
        "M14.1,8 H10.6 L9.5,9.9 H6.5 L5.4,8 H1.9 M4.1,2.9 H11.9 L14.1,8 V11.8 " +
        "A1.3,1.3 0 0 1 12.8,13.1 H3.2 A1.3,1.3 0 0 1 1.9,11.8 V8 Z";

    /// <summary>Se a varredura de e-mails está em curso. Diagnóstico e ensaio.</summary>
    public bool ProcessandoEmail { get; private set; }

    /// <summary>
    /// §5.6 — a IA está varrendo a caixa de entrada. Visualmente é o Working, com UMA
    /// diferença: NÃO há cápsula de trilha à direita. Nenhuma ação de arquivo aconteceu, então
    /// não há o que listar, e o orbe fica sozinho.
    /// </summary>
    public void ComecarAProcessarEmail()
    {
        ProcessandoEmail = true;
        MostrarSimbolo();
        Casca.ToolTip = "Processando e-mails";
        ComecarATrabalhar();
    }

    /// <summary>
    /// Fim da varredura. Sem nada a relatar, volta a Idle; com algo, ENFILEIRA e pulsa — nunca
    /// abre o balão sozinho (§0 O3).
    /// </summary>
    public void TerminarDeProcessarEmail(string? relatorio = null, IReadOnlyList<MailSummary>? emails = null,
                                         bool urgente = false)
    {
        PararDeProcessarEmail();

        _emailsPendentes = emails;

        // A varredura é proativa por definição: SEMPRE enfileira e pulsa, mesmo com a barra
        // aberta. Ninguém pediu por ela, então ela não tem direito de ocupar a tela.
        if (!string.IsNullOrWhiteSpace(relatorio)) EnfileirarFala(relatorio!, urgente);
    }

    /// <summary>
    /// Volta do estado de varredura, e NADA MAIS.
    /// <para>
    /// Separado do <see cref="TerminarDeProcessarEmail"/> porque o vigia agora avisa o fim de
    /// toda passada, inclusive das que não têm o que mostrar. Se esse aviso caísse no outro
    /// método, ele chegaria com relatório nulo depois de um digest cheio e apagaria a lista de
    /// e-mails que o digest tinha acabado de entregar.
    /// </para>
    /// <para>
    /// Idempotente de propósito: chamar duas vezes é o caso normal, não o erro.
    /// </para>
    /// </summary>
    public void PararDeProcessarEmail()
    {
        ProcessandoEmail = false;
        MostrarSimbolo();

        // A varredura roda em paralelo com o turno: o fim dela não apaga o anel de um turno que
        // continua. É o espelho do cuidado que o MostrarEstado já tinha com a varredura.
        if (_passoAtual != null)
        {
            Casca.ToolTip = _passoAtual;
            return;
        }

        Casca.ToolTip = null;
        PararDeTrabalhar();
    }

    private IReadOnlyList<MailSummary>? _emailsPendentes;

    /// <summary>
    /// Um e-mail da pilha foi escolhido — §4.8.
    /// <para>
    /// O item já existia na fala do orbe e o clique nele não fazia NADA: a lista parecia um
    /// desenho. Quem liga o evento a alguma coisa é o App, e o destino é o mesmo da lista da
    /// área central — o e-mail entra na conversa. O orbe não conhece a janela de chat e não
    /// precisa conhecer; ele avisa que foi clicado.
    /// </para>
    /// </summary>
    public event Action<MailSummary>? EmailEscolhido;

    /// <summary>
    /// O clique no item da pilha. No orbe o corpo do acordeão fica recolhido — não há
    /// <c>Aberto</c> aqui —, então este é o ÚNICO gesto que o item oferece, e ele tem de levar
    /// ao mesmo lugar que "Abrir com &lt;NOME&gt;" leva na lista da janela.
    /// </summary>
    private void EmailDaPilha_Click(object sender, EventArgs e)
    {
        if (sender is MailListItem item && item.DataContext is MailSummary email)
            EmailEscolhido?.Invoke(email);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // §5.2  Pulso
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Guarda uma fala e faz o orbe pulsar. É por aqui que qualquer coisa proativa entra —
    /// o digest de e-mail, o resultado de uma tarefa longa, um aviso.
    /// </summary>
    /// <param name="urgente">
    /// Pinta o pulso de vermelho em vez de lilás. NÃO é toast, NÃO abre balão: continua sendo
    /// só o pulso, como manda a §0 O3. O que muda é a cor, para um incidente aberto — vários
    /// alertas do mesmo monitor em poucos minutos — se distinguir de uma fala comum sem
    /// precisar interromper ninguém. Extensão à spec, que só prevê o pulso lilás.
    /// </param>
    public void EnfileirarFala(string texto, bool urgente = false)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;

        _falasPendentes.Add(texto.Trim());
        Pulsar(urgente);
    }

    /// <summary>
    /// §5.2 — o anel expande em laço até alguém clicar. A10: ele NÃO expira sozinho; se
    /// sumisse por conta própria, a mensagem se perderia sem ninguém saber que existiu.
    /// </summary>
    public void Pulsar(bool urgente = false)
    {
        AnelDePulso.BorderBrush = urgente
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("AccentLilacBrush");

        if (Pulsando) return;
        Pulsando = true;

        var duracao = new Duration(TimeSpan.FromSeconds(2));

        var escala = new DoubleAnimation(1.0, ExpansaoDoPulso, duracao) { RepeatBehavior = RepeatBehavior.Forever };
        EscalaDoPulso.BeginAnimation(ScaleTransform.ScaleXProperty, escala);
        EscalaDoPulso.BeginAnimation(ScaleTransform.ScaleYProperty, escala);

        // A opacidade cai antes do fim da expansão e fica em zero no resto do ciclo: é o que
        // dá o intervalo entre uma onda e a seguinte, em vez de um anel piscando sem pausa.
        var sumico = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        sumico.KeyFrames.Add(new LinearDoubleKeyFrame(0.55, KeyTime.FromPercent(0.0)));
        sumico.KeyFrames.Add(new LinearDoubleKeyFrame(0.00, KeyTime.FromPercent(0.7)));
        sumico.KeyFrames.Add(new LinearDoubleKeyFrame(0.00, KeyTime.FromPercent(1.0)));
        sumico.Duration = duracao;
        AnelDePulso.BeginAnimation(OpacityProperty, sumico);
    }

    /// <summary>Para o pulso. Chamado quando o usuário clica, nunca por tempo.</summary>
    public void PararDePulsar()
    {
        if (!Pulsando) return;
        Pulsando = false;

        EscalaDoPulso.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        EscalaDoPulso.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AnelDePulso.BeginAnimation(OpacityProperty, null);
        AnelDePulso.Opacity = 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // §4.6 / §5.4  Balão de fala
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Liga o anel de progresso — §5.6. Enquanto o turno roda, ele gira em volta do glyph.
    /// <para>
    /// O balão NÃO aparece agora, de propósito: ele só entra quando a resposta está pronta.
    /// É a mesma regra que a janela de chat já segue, e o motivo é o mesmo — texto brotando
    /// palavra a palavra num balão que muda de tamanho a cada quadro sobre o desktop do
    /// usuário chama mais atenção que a resposta em si.
    /// </para>
    /// </summary>
    public void ComecarATrabalhar()
    {
        Trabalhando = true;

        var giro = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        // Os dois giram; quem decide qual se vê é o AtualizarAnelDeProgresso. Animar só o
        // visível pouparia nada — é uma rotação de dois Paths — e obrigaria a religar a
        // animação no meio do morph, que é justamente quando ela não pode piscar.
        GiroDoAnel.BeginAnimation(RotateTransform.AngleProperty, giro);
        GiroDoAnelDoOrbe.BeginAnimation(RotateTransform.AngleProperty, giro);

        AtualizarAnelDeProgresso();
    }

    /// <summary>
    /// O passo em que a AIB está AGORA, em uma linha. Vazio ou nulo: parou.
    /// <para>
    /// É o que o orbe mostra de um turno que não é dele — anel girando e o passo no tooltip,
    /// sem balão. A fala é resposta e tem dono (ver <see cref="OrbeDeveFalar"/>); o estado é
    /// notícia, e um balão por ferramenta usada encheria o desktop com a mesma conversa que a
    /// janela já está mostrando.
    /// </para>
    /// <para>
    /// Vale mesmo com o orbe ESCONDIDO, que é o caso comum: ele sai da tela enquanto a
    /// conversa está aberta (<see cref="DeveAparecer"/>). Esconder a conversa no meio de um
    /// turno traz o orbe de volta, e ele tem de voltar girando no passo certo — não parado,
    /// como se nada estivesse acontecendo.
    /// </para>
    /// </summary>
    /// <param name="ferramenta">
    /// A ferramenta que está rodando (<see cref="Ferramentas"/>), ou null quando ela só pensa ou
    /// espera. Com ferramenta, o ícone dela entra no lugar do glyph: antes o orbe só girava o
    /// anel, e quem olhava de longe não sabia se ela lia um arquivo, rodava um comando ou
    /// navegava — o texto do passo ficava escondido no tooltip.
    /// </param>
    public void MostrarEstado(string? passo, string? ferramenta = null)
    {
        if (string.IsNullOrWhiteSpace(passo))
        {
            _passoAtual = null;
            _ferramentaAtual = null;
            MostrarSimbolo();

            // A varredura de e-mail tem o estado DELA — ícone de inbox e tooltip próprios — e
            // roda em paralelo com o turno. Deixar o fim de um turno apagar o anel dela é o
            // mesmo defeito que separou PararDeProcessarEmail de TerminarDeProcessarEmail: o
            // anel apagado com a caixa ainda sendo lida.
            if (ProcessandoEmail) return;

            Casca.ToolTip = null;
            PararDeTrabalhar();
            return;
        }

        _passoAtual = passo;
        _ferramentaAtual = string.IsNullOrWhiteSpace(ferramenta) ? null : ferramenta;
        MostrarSimbolo();

        // Durante a varredura o tooltip é dela; o do turno volta quando ela acabar.
        if (!ProcessandoEmail) Casca.ToolTip = passo;

        // Só quando ainda não está girando: ComecarATrabalhar recomeça a animação do zero, e
        // religá-la a cada ferramenta faria o anel dar um salto visível a cada passo do turno.
        if (!Trabalhando) ComecarATrabalhar();
    }

    private string? _passoAtual;
    private string? _ferramentaAtual;

    /// <summary>A ferramenta cujo ícone o orbe mostra agora, ou null. Diagnóstico e ensaio.</summary>
    public string? FerramentaMostrada =>
        IconeDaAcao.Visibility == Visibility.Visible ? _ferramentaAtual : null;

    /// <summary>
    /// Um símbolo só na célula do glyph, por prioridade: a varredura de e-mail (inbox), depois a
    /// ferramenta do turno (o ícone dela), e o glyph quando ela só pensa ou está parada.
    /// </summary>
    private void MostrarSimbolo()
    {
        bool inbox = ProcessandoEmail;
        bool acao = !inbox && _ferramentaAtual != null;

        if (acao) IconeDaAcao.Data = ToolIcons.De(_ferramentaAtual);

        IconeDeInbox.Visibility = inbox ? Visibility.Visible : Visibility.Collapsed;
        IconeDaAcao.Visibility = acao ? Visibility.Visible : Visibility.Collapsed;
        Glyph.Visibility = inbox || acao ? Visibility.Collapsed : Visibility.Visible;
        AjustarCelulaAoConteudo();
    }

    private void PararDeTrabalhar()
    {
        Trabalhando = false;
        GiroDoAnel.BeginAnimation(RotateTransform.AngleProperty, null);
        GiroDoAnelDoOrbe.BeginAnimation(RotateTransform.AngleProperty, null);
        AtualizarAnelDeProgresso();
    }

    /// <summary>
    /// Escolhe o anel pela forma da casca — §5.5 e §5.6.
    /// <para>
    /// Círculo: o anel fica POR FORA da borda, envolvendo o orbe. Barra: não há círculo em
    /// volta do que girar, e a spec manda o anel para o glyph pequeno da ponta esquerda. São
    /// duas posições, e a versão anterior tinha só a segunda — o arco girava espremido dentro
    /// do círculo em vez de contorná-lo.
    /// </para>
    /// </summary>
    private void AtualizarAnelDeProgresso()
    {
        bool naBarra = _emModoBarra;

        AnelDoOrbe.Visibility = Trabalhando && !naBarra ? Visibility.Visible : Visibility.Collapsed;
        AnelDeProgresso.Visibility = Trabalhando && naBarra ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Mostra o que estava esperando e encerra o pulso.
    /// <para>
    /// Mais de uma fala pendente entra num balão só, separadas por linha em branco, no
    /// máximo as três mais recentes — a §4.6 prevê empilhar até três balões, e um só com o
    /// texto junto entrega a mesma informação sem construir uma pilha que pode cobrir meia
    /// tela do usuário.
    /// </para>
    /// </summary>
    private void RevelarFalasPendentes()
    {
        PararDePulsar();
        if (_falasPendentes.Count == 0) return;

        int primeira = Math.Max(0, _falasPendentes.Count - 3);
        string texto = string.Join("\n\n", _falasPendentes.GetRange(primeira, _falasPendentes.Count - primeira));

        _falasPendentes.Clear();

        // §5.7 — mesmo estado Speaking; o que muda é o balão levar a lista abaixo do texto.
        AdicionarFala(new FalaDaIA(texto, _emailsPendentes, TetoDeEmails));
        _emailsPendentes = null;
    }

    /// <summary>
    /// A resposta de um turno chegou.
    /// <para>
    /// Com a barra ABERTA, o texto entra direto: o usuário está olhando para ela, esperando o
    /// que ele mesmo pediu, e fazê-lo clicar de novo para ver a própria resposta seria pedir um
    /// gesto que não informa nada.
    /// </para>
    /// <para>
    /// Com a barra FECHADA, enfileira e PULSA. Este era o defeito: a resposta abria um balão
    /// sozinho, e pior — um balão ancorado numa barra que não estava mais na tela, flutuando
    /// sobre o desktop apontando para nada. Quem perguntou e foi fazer outra coisa merece o
    /// mesmo tratamento de qualquer fala proativa: o pulso espera, sem interromper.
    /// </para>
    /// </summary>
    public void ResponderTurno(string texto)
    {
        PararDeTrabalhar();

        if (string.IsNullOrWhiteSpace(texto)) return;

        if (_emModoBarra) MostrarFala(texto);
        else EnfileirarFala(texto);
    }

    /// <summary>
    /// §4.6 — mostra a fala acima da barra. Entrada por fade + deslocamento de 8px, que
    /// mora no gatilho Loaded do próprio template da bolha.
    /// </summary>
    public void MostrarFala(string texto)
    {
        PararDeTrabalhar();

        if (string.IsNullOrWhiteSpace(texto)) return;

        AdicionarFala(new FalaDaIA(texto));
    }

    /// <summary>
    /// Empilha mais uma bolha, mostra a pilha e desce para o fim dela.
    /// <para>
    /// A pilha NÃO tem teto de bolhas: o que a limita é a ALTURA do rolo. Um teto de
    /// contagem apagava a pergunta que explicava a resposta ainda visível logo abaixo dela —
    /// e o que precisa não crescer sem fim é o espaço que a janela ocupa na tela, que a
    /// MaxHeight do rolo já resolve.
    /// </para>
    /// </summary>
    private void AdicionarFala(FalaDoOrbe fala)
    {
        _falas.Add(fala);
        RoloDasFalas.Visibility = Visibility.Visible;

        // O layout precisa acontecer ANTES do ScrollToEnd: sem ele o rolo ainda não sabe que
        // ficou mais alto e desce para o fim ANTIGO, deixando a bolha recém-chegada fora da
        // vista — que é exatamente a que interessa.
        RoloDasFalas.UpdateLayout();
        RoloDasFalas.ScrollToEnd();
        AtualizarDesvanecimento();
    }

    private void RoloDasFalas_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        AtualizarDesvanecimento(RoloDasFalas.VerticalOffset);

    private void AtualizarDesvanecimento() => AtualizarDesvanecimento(RoloDasFalas.VerticalOffset);

    /// <summary>
    /// Põe o desvanecimento no topo do rolo, e só quando há conteúdo escondido acima.
    /// <para>
    /// Sem a máscara, a pilha cheia termina em corte reto no meio de uma bolha, que sobre o
    /// desktop lê como defeito de desenho. Com a máscara FIXA, o topo da primeira bolha
    /// ficaria lavado mesmo sem nada cortado atrás dela. Então ela entra e sai conforme a
    /// posição do rolo.
    /// </para>
    /// </summary>
    /// <param name="deslocamento">
    /// Quanto da pilha ficou acima da vista. É parâmetro, e não leitura direta do rolo, porque
    /// o VerticalOffset só existe depois de uma passada de layout de verdade — e uma janela que
    /// nunca foi mostrada não tem nenhuma. Sem a costura, a regra ficaria sem ensaio.
    /// </param>
    public void AtualizarDesvanecimento(double deslocamento)
    {
        RoloDasFalas.OpacityMask = deslocamento > 0.5
            ? (Brush)FindResource("DesvanecimentoDoTopo")
            : null;
    }

    /// <summary>
    /// Descarta a conversa da barra e MANTÉM a barra aberta. §5.7: leva o balão INTEIRO,
    /// lista incluída.
    /// <para>
    /// Não há mais X em cada bolha: o cabeçalho saiu, e com ele o nome, a hora e o botão de
    /// dispensar. Quatro elementos de moldura em volta de uma frase de dez palavras pesavam
    /// mais que a frase. O método continua sendo o caminho programático para zerar a pilha.
    /// </para>
    /// </summary>
    public void DispensarFala()
    {
        _falas.Clear();
        RoloDasFalas.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// O Border da bolha de índice <paramref name="indice"/>, já materializado. Diagnóstico e
    /// ensaio: o que a pilha desenha vem de DataTemplate, e só existe depois de uma medição.
    /// </summary>
    public FrameworkElement? ElementoDaFala(int indice, string nome)
    {
        PilhaDeFalas.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

        var recipiente = PilhaDeFalas.ItemContainerGenerator.ContainerFromIndex(indice);
        return recipiente == null ? null : Procurar(recipiente, nome);
    }

    private static FrameworkElement? Procurar(DependencyObject raiz, string nome)
    {
        if (raiz is FrameworkElement elemento && elemento.Name == nome) return elemento;

        int filhos = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < filhos; i++)
        {
            var achado = Procurar(VisualTreeHelper.GetChild(raiz, i), nome);
            if (achado != null) return achado;
        }

        return null;
    }

    /// <summary>
    /// §4.4 — hover. ScaleTransform, e não mudança de tamanho: alterar Width dispararia
    /// layout e reposicionamento a cada entrada do mouse.
    /// </summary>
    private void Escalar(double fator)
    {
        if (_emModoBarra && fator > 1) return; // a barra não cresce no hover

        var animacao = new DoubleAnimation(fator, TimeSpan.FromSeconds(0.15))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        EscalaDaCasca.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, animacao);
        EscalaDaCasca.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, animacao);
    }
}
