using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

// WinForms entra junto com o WPF em net8.0-windows e traz homonimos. Sem os aliases,
// KeyEventArgs e TextChangedEventArgs sao ambiguos.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using AIB.Services;
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
    /// </summary>
    private const double MargemDaSombra = 40;

    /// <summary>Duração do morph de ida — §6. O foco só vai para o campo no fim dela.</summary>
    private static readonly TimeSpan DuracaoDoMorph = TimeSpan.FromSeconds(0.28);

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
    /// Texto enviado pela barra. §5.3: a conversa continua na janela de chat, não aqui — a
    /// barra é porta de entrada, não um segundo chat.
    /// </summary>
    public event Action<string>? MensagemEnviada;

    public ShadowAssistantWindow()
    {
        InitializeComponent();

        // §1 — os quatro gatilhos de reposicionamento, todos obrigatórios.
        SystemEvents.DisplaySettingsChanged += AoMudarAsTelas;
        SystemParameters.StaticPropertyChanged += AoMudarParametroDoSistema;
        DpiChanged += (_, _) => Reposicionar();
        SizeChanged += (_, _) => Reposicionar();

        Loaded += (_, _) =>
        {
            VisualStateManager.GoToElementState(Palco, "Orbe", false);
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
        Glyph.BeginAnimation(MarginProperty, null);

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

        // §6 — o rascunho curto é descartado ao fechar; o longo sobrevive para a próxima
        // abertura, porque perder um parágrafo digitado por causa de um clique fora seria
        // pior que a barra reabrir com texto velho.
        if (Campo.Text.Trim().Length <= RascunhoPreservadoAcimaDe) Campo.Clear();

        AtualizarDica();
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
        FecharBarra();

        MensagemEnviada?.Invoke(texto);
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
