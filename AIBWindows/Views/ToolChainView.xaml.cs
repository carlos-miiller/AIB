using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AIB.Services.Memory;
using AIB.Ui;

// System.Drawing e WinForms trazem homônimos destes; o WPF usa os de System.Windows.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Path = System.Windows.Shapes.Path;
using FontFamily = System.Windows.Media.FontFamily;

namespace AIB.Views;

/// <summary>
/// A cadeia de ações de um turno — §4 da spec de chat.
/// <para>
/// Uma cadeia por bloco de ferramentas: as concluídas colapsam num único chip de ícones, e a
/// que está em curso fica ao lado. É a diferença entre uma conversa que empilha vinte linhas de
/// "usando ferramenta X..." e uma que mostra o trabalho inteiro numa linha só.
/// </para>
/// </summary>
public partial class ToolChainView : System.Windows.Controls.UserControl
{
    /// <summary>Ações que já terminaram, na ordem de conclusão. Uma entrada por ícone.</summary>
    private readonly Dictionary<string, string> _emCurso = new(StringComparer.Ordinal);

    private Storyboard? _giro;

    public ToolChainView()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            PararSpinner();
            _ritmo?.Stop();
            _ritmo = null;
        };
    }

    /// <summary>Quantas ações desta cadeia já terminaram.</summary>
    public int Concluidas { get; private set; }

    /// <summary>Se ainda há ferramenta em execução.</summary>
    public bool TemAcaoEmCurso => ChipEmCurso.Visibility == Visibility.Visible;

    // ─────────────────────────────────────────────────────────────────────
    // Ciclo de vida de uma ação
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Anuncia uma ferramenta em execução (§4.2).</summary>
    public void Iniciar(string id, string ferramenta, string argumento)
    {
        _emCurso[id] = ferramenta;

        IconeFalha.Visibility = Visibility.Collapsed;
        Spinner.Visibility = Visibility.Visible;
        ChipEmCurso.BorderBrush = (Brush)FindResource("AccentBorderBrush");

        RotuloEstado.Text = "Executando";
        RotuloEstado.Foreground = (Brush)FindResource("TextSecondaryBrush");
        NomeFerramenta.Foreground = (Brush)FindResource("AccentLilacBrush");

        // O modelo pede várias ferramentas na MESMA iteração e elas rodam em paralelo. O chip
        // é um só, e antes cada anúncio sobrescrevia o anterior: das três em curso, a tela
        // mostrava uma, e depois as três terminavam quase juntas e viravam três ícones de uma
        // vez. Parecia rajada; era a fila inteira escondida atrás do último nome.
        if (_emCurso.Count > 1)
        {
            NomeFerramenta.Text = $"{_emCurso.Count} ferramentas";
            ArgumentoResumido.Text = "em paralelo";
            ChipEmCurso.ToolTip = MontarTooltipSimples(
                string.Join(", ", _emCurso.Values.Select(AIB.Services.Ferramentas.Rotulo)), null);
        }
        else
        {
            // O ROTULO, nao o nome da funcao. "read" e endereco; "Lendo arquivo" e noticia,
            // e e o que a pessoa olhando a tela quer saber.
            NomeFerramenta.Text = AIB.Services.Ferramentas.Rotulo(ferramenta);
            ArgumentoResumido.Text = argumento ?? "";
            ChipEmCurso.ToolTip = MontarTooltipSimples(
                AIB.Services.Ferramentas.Rotulo(ferramenta), argumento);
        }

        ChipEmCurso.Visibility = Visibility.Visible;
        IniciarSpinner();
    }

    /// <summary>
    /// Marca a ação como "Aguardando": ela existe, mas está parada esperando o usuário decidir
    /// no card de confirmação. A fila fica bloqueada enquanto isso (§5.3).
    /// </summary>
    public void Aguardar()
    {
        if (ChipEmCurso.Visibility != Visibility.Visible) return;
        RotuloEstado.Text = "Aguardando";
    }

    /// <summary>Tempo mínimo que uma ação fica visível como "em execução".</summary>
    /// <remarks>
    /// Ler um arquivo pequeno leva milissegundos. Sem um piso, a ação aparecia e virava ícone
    /// no mesmo quadro — e, com várias em paralelo, a tela cuspia a lista inteira de uma vez,
    /// sem que desse para ver o que estava sendo feito. O atraso é SÓ do desenho: a ferramenta
    /// já executou e o resultado já seguiu para o modelo. Nada espera por isto.
    /// </remarks>
    private static readonly TimeSpan TempoMinimoVisivel = TimeSpan.FromMilliseconds(500);

    /// <summary>Conclusões esperando a vez de virar ícone.</summary>
    private readonly Queue<(string Ferramenta, bool Falhou, bool Recusada, Artifact? Artefato, string? Detalhe)>
        _aguardandoDesenho = new();

    private System.Windows.Threading.DispatcherTimer? _ritmo;

    /// <summary>
    /// Encerra a ação: ela vira ícone na trilha do chip concluído, ou deixa o chip em curso em
    /// estado de erro (§4.3 e §4.7).
    /// <para>
    /// A conclusão entra numa FILA em vez de ser desenhada na hora. As ferramentas de um mesmo
    /// bloco rodam em paralelo e terminam quase juntas; desenhá-las conforme chegam faz a
    /// trilha aparecer inteira num piscar. A fila dá a cada uma o seu momento na tela.
    /// </para>
    /// </summary>
    public void Concluir(string id, bool falhou, bool recusada, Artifact? artefato, string? detalhe)
    {
        _emCurso.TryGetValue(id, out string? ferramenta);
        ferramenta ??= artefato?.Tool ?? "";

        // A remoção de _emCurso acontece só quando a conclusão for DESENHADA: até lá a ação
        // ainda é uma das que estão em curso, e a contagem do chip precisa dizer isso.
        _aguardandoDesenho.Enqueue((ferramenta, falhou, recusada, artefato, detalhe));
        _idsAguardando.Enqueue(id);

        GarantirRitmo();
    }

    private readonly Queue<string> _idsAguardando = new();

    private void GarantirRitmo()
    {
        if (_ritmo != null) return;

        _ritmo = new System.Windows.Threading.DispatcherTimer { Interval = TempoMinimoVisivel };
        _ritmo.Tick += (_, _) => DesenharProxima();

        // Começa a contar AGORA e só desenha no primeiro disparo: é isso que garante o piso de
        // meio segundo para a ação que acabou de ser anunciada.
        _ritmo.Start();
    }

    /// <summary>
    /// Desenha na hora tudo que está na fila, ignorando o ritmo.
    /// <para>
    /// Existe para os ensaios: esperar meio segundo por ação transformaria um teste de lógica
    /// em teste de relógio, lento e instável. O caminho de produção continua sendo a fila.
    /// </para>
    /// </summary>
    private void DrenarParaEnsaio()
    {
        while (_aguardandoDesenho.Count > 0) DesenharProxima();

        _ritmo?.Stop();
        _ritmo = null;
    }

    private void DesenharProxima()
    {
        if (_aguardandoDesenho.Count == 0)
        {
            _ritmo?.Stop();
            _ritmo = null;
            return;
        }

        var item = _aguardandoDesenho.Dequeue();
        string id = _idsAguardando.Count > 0 ? _idsAguardando.Dequeue() : "";
        if (id.Length > 0) _emCurso.Remove(id);

        if (item.Falhou)
        {
            MostrarFalha(item.Ferramenta, item.Recusada, item.Artefato, item.Detalhe);
            return;
        }

        AcrescentarIcone(item.Ferramenta, item.Artefato, falhou: false, recusada: false, detalhe: null);

        // Ainda há paralelas em curso: o chip continua, com a contagem atualizada.
        if (_emCurso.Count > 0)
        {
            var restante = _emCurso.First();
            Iniciar(restante.Key, restante.Value, "");
            return;
        }

        PararSpinner();
        ChipEmCurso.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Chip de erro: borda vermelha, alerta no lugar do spinner e a mensagem no argumento.
    /// <para>
    /// A falha NÃO colapsa em ícone junto com os sucessos: ela é o que o usuário precisa ler, e
    /// esconder atrás de um ícone de 22px transformaria o erro em detalhe. Ela entra na trilha
    /// só quando a ação seguinte começa.
    /// </para>
    /// </summary>
    private void MostrarFalha(string ferramenta, bool recusada, Artifact? artefato, string? detalhe)
    {
        PararSpinner();

        Spinner.Visibility = Visibility.Collapsed;
        IconeFalha.Visibility = Visibility.Visible;
        ChipEmCurso.Visibility = Visibility.Visible;
        ChipEmCurso.BorderBrush = (Brush)FindResource("DangerBorderBrush");

        RotuloEstado.Text = recusada ? "Recusado" : "Falhou";
        RotuloEstado.Foreground = (Brush)FindResource("DangerBrush");
        NomeFerramenta.Text = ferramenta;
        NomeFerramenta.Foreground = (Brush)FindResource("DangerBrush");
        ArgumentoResumido.Text = detalhe ?? artefato?.Detail ?? "";

        ChipEmCurso.ToolTip = MontarTooltipSimples(
            ferramenta, artefato?.Value ?? detalhe ?? "");

        // Guardado para virar ícone vermelho na trilha quando a próxima ação começar.
        _falhaPendente = (ferramenta, artefato, detalhe, recusada);
    }

    private (string Ferramenta, Artifact? Artefato, string? Detalhe, bool Recusada)? _falhaPendente;

    /// <summary>
    /// Recolhe a falha que estava visível para dentro da trilha. Chamado quando outra ação
    /// começa ou quando o turno termina.
    /// </summary>
    public void RecolherFalhaPendente()
    {
        if (_falhaPendente == null) return;

        var f = _falhaPendente.Value;
        _falhaPendente = null;

        AcrescentarIcone(f.Ferramenta, f.Artefato, falhou: true, recusada: f.Recusada, detalhe: f.Detalhe);

        PararSpinner();
        ChipEmCurso.Visibility = Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Trilha de ícones
    // ─────────────────────────────────────────────────────────────────────

    private void AcrescentarIcone(
        string ferramenta, Artifact? artefato, bool falhou, bool recusada, string? detalhe)
    {
        var desenho = artefato != null
            ? ToolIcons.De(recusada ? ArtifactKind.Denied : artefato.Kind)
            : ToolIcons.De(ferramenta);

        var icone = new Path
        {
            Data = desenho,
            Stroke = (Brush)FindResource(falhou ? "DangerBrush" : "TextSecondaryBrush"),
            StrokeThickness = ToolIcons.Espessura,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 22,
            Height = 22,
            Stretch = Stretch.None,
            Margin = new Thickness(Trilha.Children.Count == 0 ? 0 : 8, 0, 0, 0),
            // O tooltip é ToolTip nativo de propósito: ele abre num popup, FORA do
            // ScrollViewer da trilha. Um elemento filho nunca escaparia — a rolagem
            // horizontal recorta nos DOIS eixos (A6).
            ToolTip = MontarTooltipSimples(ferramenta, artefato?.Value ?? detalhe ?? "")
        };

        var brilho = falhou ? "#FFFF7A7E" : null;
        if (brilho != null)
        {
            icone.MouseEnter += (_, _) => icone.Stroke = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(brilho));
            icone.MouseLeave += (_, _) => icone.Stroke = (Brush)FindResource("DangerBrush");
        }
        else
        {
            icone.MouseEnter += (_, _) => icone.Stroke = (Brush)FindResource("TextBodyBrush");
            icone.MouseLeave += (_, _) => icone.Stroke = (Brush)FindResource("TextSecondaryBrush");
        }

        Trilha.Children.Add(icone);
        Concluidas++;
        ChipConcluido.Visibility = Visibility.Visible;

        // A trilha rola para o fim: a ação mais recente é a que interessa ver.
        Dispatcher.BeginInvoke(new Action(() => TrilhaScroll.ScrollToRightEnd()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Tooltip simples de uma linha (§4.6 i): "write • C:\caminho\arquivo.txt".
    /// </summary>
    private static object MontarTooltipSimples(string ferramenta, string? literal)
    {
        string texto = string.IsNullOrWhiteSpace(literal)
            ? ferramenta
            : $"{ferramenta} • {literal}";

        return new TextBlock
        {
            Text = texto,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
        };
    }

    /// <summary>
    /// A roda vertical do mouse sobre a trilha rola na HORIZONTAL — é o gesto natural sobre uma
    /// tira de ícones, e sem isto a roda passaria direto para a lista de mensagens.
    /// </summary>
    private void Trilha_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TrilhaScroll.ScrollableWidth <= 0) return;

        TrilhaScroll.ScrollToHorizontalOffset(TrilhaScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spinner
    // ─────────────────────────────────────────────────────────────────────

    private void IniciarSpinner()
    {
        if (_giro != null) return;

        var volta = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        Storyboard.SetTarget(volta, Spinner);
        Storyboard.SetTargetProperty(volta,
            new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));

        _giro = new Storyboard();
        _giro.Children.Add(volta);
        _giro.Begin();
    }

    /// <summary>
    /// Para a animação de verdade, e não só esconde o chip.
    /// <para>
    /// Um Storyboard em RepeatBehavior.Forever continua acordando o motor de composição mesmo
    /// com o elemento invisível. Numa conversa com dezenas de ferramentas isso vira dezenas de
    /// animações eternas girando atrás de chips que ninguém vê.
    /// </para>
    /// </summary>
    private void PararSpinner()
    {
        if (_giro == null) return;

        _giro.Stop();
        _giro = null;
        SpinnerGiro.Angle = 0;
    }
}
