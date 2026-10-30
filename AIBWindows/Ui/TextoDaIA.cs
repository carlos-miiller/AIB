using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Markdig.Wpf;

namespace AIB.Ui;

/// <summary>
/// O visualizador de Markdown das falas da IA, montado num lugar só.
/// <para>
/// Era construído dentro da janela de chat, e o orbe desenhava as falas dele com um
/// <see cref="TextBlock"/>. Visto no uso: a mesma resposta saía formatada na janela e, no orbe,
/// com os asteriscos, as cercas de código e os marcadores de lista crus.
/// </para>
/// </summary>
public static class VisorDeMarkdown
{
    /// <summary>
    /// O visualizador com o interpretador da conversa, as cores e fontes dos tokens e a margem
    /// de página zerada. <paramref name="dono"/> é de onde os recursos são buscados.
    /// </summary>
    public static MarkdownViewer Criar(string? texto, FrameworkElement dono)
    {
        var viewer = new MarkdownViewer
        {
            // Antes do texto: trocar o interpretador depois obrigaria a remontar o documento.
            Pipeline = MarkdownPipelines.Conversa,
            Markdown = texto ?? "",
            Foreground = (System.Windows.Media.Brush)dono.FindResource("TextBodyBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            FontSize = (double)dono.FindResource("FontSizeBubble"),
        };

        // O documento de fluxo nasce com margem própria de página, e ela se somava ao padding
        // da bolha. Zerar na fonte é o certo.
        ZerarMargem(viewer);

        // §5.5 — código inline em lilás sobre surfaceCode; bloco em textBody sobre
        // surfaceCodeBlock. Os dois em mono 12,5.
        var fonteMono = (System.Windows.Media.FontFamily)dono.FindResource("MonoFontFamily");
        var tamanhoCodigo = (double)dono.FindResource("FontSizeCode");

        var inline = new Style();
        inline.Setters.Add(new Setter(TextElement.BackgroundProperty, dono.FindResource("SurfaceCodeBrush")));
        inline.Setters.Add(new Setter(TextElement.ForegroundProperty, dono.FindResource("AccentLilacBrush")));
        inline.Setters.Add(new Setter(TextElement.FontFamilyProperty, fonteMono));
        inline.Setters.Add(new Setter(TextElement.FontSizeProperty, tamanhoCodigo));

        var bloco = new Style();
        bloco.Setters.Add(new Setter(TextElement.BackgroundProperty, dono.FindResource("SurfaceCodeBlockBrush")));
        bloco.Setters.Add(new Setter(TextElement.ForegroundProperty, dono.FindResource("TextBodyBrush")));
        bloco.Setters.Add(new Setter(TextElement.FontFamilyProperty, fonteMono));
        bloco.Setters.Add(new Setter(TextElement.FontSizeProperty, tamanhoCodigo));

        viewer.Resources.Add(Markdig.Wpf.Styles.CodeStyleKey, inline);
        viewer.Resources.Add(Markdig.Wpf.Styles.CodeBlockStyleKey, bloco);

        // O tema do Markdig.Wpf fixa os títulos em preto (#000) e em 42/20/18px, para página
        // branca. Visto no uso: "## Resumo" saía preto sobre o vidro escuro, quase invisível,
        // e o "#" engolia a bolha. Aqui o título usa a cor forte do tema e a escala da conversa.
        var forte = dono.FindResource("TextStrongBrush");
        double corpo = (double)dono.FindResource("FontSizeBubble");
        var titulos = new (object Chave, double Tamanho)[]
        {
            (Markdig.Wpf.Styles.Heading1StyleKey, (double)dono.FindResource("FontSizeHeaderTitle")),
            (Markdig.Wpf.Styles.Heading2StyleKey, corpo + 3),
            (Markdig.Wpf.Styles.Heading3StyleKey, corpo + 1.5),
            (Markdig.Wpf.Styles.Heading4StyleKey, corpo),
            (Markdig.Wpf.Styles.Heading5StyleKey, corpo),
            (Markdig.Wpf.Styles.Heading6StyleKey, corpo),
        };
        foreach (var (chave, tamanho) in titulos)
        {
            var titulo = new Style(typeof(Paragraph));
            titulo.Setters.Add(new Setter(TextElement.ForegroundProperty, forte));
            titulo.Setters.Add(new Setter(TextElement.FontSizeProperty, tamanho));
            titulo.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.SemiBold));
            titulo.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 8, 0, 4)));
            viewer.Resources.Add(chave, titulo);
        }

        Blocos(viewer, dono);

        // Caminho de arquivo citado vira link para o Explorer — nas duas janelas, por ser aqui.
        LinksDeArquivo.Ligar(viewer);

        return viewer;
    }

    /// <summary>
    /// Divisória, tabela, citação e link. O tema do Markdig.Wpf os pinta para página branca:
    /// linha e grade pretas, citação cinza-claro, link azul do sistema. Visto no uso
    /// (21/10/2026): "divisórias e tabelas" com cores mal postas sobre o vidro escuro.
    /// </summary>
    private static void Blocos(MarkdownViewer viewer, FrameworkElement dono)
    {
        object Cor(string chave) => dono.FindResource(chave);
        var grade = Cor("BorderOutlineBtnBrush");

        var divisoria = new Style(typeof(System.Windows.Shapes.Line));
        divisoria.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeProperty, Cor("BorderControlBrush")));
        divisoria.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.0));
        divisoria.Setters.Add(new Setter(System.Windows.Shapes.Shape.StretchProperty, System.Windows.Media.Stretch.Fill));
        divisoria.Setters.Add(new Setter(System.Windows.Shapes.Line.X2Property, 1.0));
        divisoria.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4)));
        viewer.Resources.Add(Markdig.Wpf.Styles.ThematicBreakStyleKey, divisoria);

        var tabela = new Style(typeof(Table));
        tabela.Setters.Add(new Setter(Block.BorderBrushProperty, grade));
        tabela.Setters.Add(new Setter(Block.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        tabela.Setters.Add(new Setter(Table.CellSpacingProperty, 0.0));
        viewer.Resources.Add(Markdig.Wpf.Styles.TableStyleKey, tabela);

        var celula = new Style(typeof(TableCell));
        celula.Setters.Add(new Setter(TableCell.BorderBrushProperty, grade));
        celula.Setters.Add(new Setter(TableCell.BorderThicknessProperty, new Thickness(1, 1, 0, 0)));
        celula.Setters.Add(new Setter(TableCell.PaddingProperty, new Thickness(6, 2, 6, 2)));
        viewer.Resources.Add(Markdig.Wpf.Styles.TableCellStyleKey, celula);

        var cabecalho = new Style(typeof(TableRow));
        cabecalho.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.SemiBold));
        cabecalho.Setters.Add(new Setter(TextElement.ForegroundProperty, Cor("TextStrongBrush")));
        cabecalho.Setters.Add(new Setter(TextElement.BackgroundProperty, Cor("SurfaceHoverSoftBrush")));
        viewer.Resources.Add(Markdig.Wpf.Styles.TableHeaderStyleKey, cabecalho);

        var citacao = new Style(typeof(Section));
        citacao.Setters.Add(new Setter(Block.BorderBrushProperty, Cor("AccentBorderStrongBrush")));
        citacao.Setters.Add(new Setter(Block.BorderThicknessProperty, new Thickness(3, 0, 0, 0)));
        citacao.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(12, 0, 0, 0)));
        citacao.Setters.Add(new Setter(TextElement.ForegroundProperty, Cor("TextSecondaryBrush")));
        viewer.Resources.Add(Markdig.Wpf.Styles.QuoteBlockStyleKey, citacao);

        var link = new Style(typeof(Hyperlink));
        link.Setters.Add(new Setter(TextElement.ForegroundProperty, Cor("AccentBlueBrush")));
        link.Setters.Add(new Setter(Inline.TextDecorationsProperty, TextDecorations.Underline));
        viewer.Resources.Add(Markdig.Wpf.Styles.HyperlinkStyleKey, link);
    }

    /// <summary>
    /// O visualizador encolhido até o texto, como a bolha do usuário. Sem isto o documento de
    /// fluxo aceita toda a largura oferecida — ver <see cref="ShrinkWrap"/>.
    /// </summary>
    public static ShrinkWrap Encolhido(MarkdownViewer viewer) => new()
    {
        Child = viewer,

        // Os 2px de folga cobrem o arredondamento entre a medição do texto e o desenho dele.
        MedirNatural = () => FlowDocumentMeasure.LarguraNatural(viewer.Document) + 2
    };

    /// <summary>
    /// Zera a margem de página do documento, agora e a cada vez que ele for trocado: atribuir
    /// <c>Markdown</c> reconstrói o documento inteiro, e o streaming faz isso a cada pedaço.
    /// </summary>
    public static void ZerarMargem(MarkdownViewer viewer)
    {
        static void Aplicar(MarkdownViewer v)
        {
            if (v.Document == null) return;
            v.Document.PagePadding = new Thickness(0);
            v.Document.PageWidth = double.NaN;
        }

        Aplicar(viewer);

        DependencyPropertyDescriptor
            .FromProperty(MarkdownViewer.DocumentProperty, typeof(MarkdownViewer))
            ?.AddValueChanged(viewer, (_, _) => Aplicar(viewer));
    }
}

/// <summary>
/// Uma fala da IA em Markdown, para usar em XAML: <c>&lt;ui:TextoDaIA Texto="{Binding Texto}"/&gt;</c>.
/// É o que a pilha de falas do orbe usa no lugar do <see cref="TextBlock"/>.
/// </summary>
public sealed class TextoDaIA : ContentControl
{
    public static readonly DependencyProperty TextoProperty = DependencyProperty.Register(
        nameof(Texto), typeof(string), typeof(TextoDaIA),
        new PropertyMetadata("", (d, _) => ((TextoDaIA)d).Montar()));

    public string Texto
    {
        get => (string)GetValue(TextoProperty);
        set => SetValue(TextoProperty, value);
    }

    /// <summary>O visualizador montado. Nulo antes de o controle entrar na árvore.</summary>
    public MarkdownViewer? Visor { get; private set; }

    public TextoDaIA()
    {
        IsTabStop = false;
        Focusable = false;

        // Os recursos (cores, fontes) só são alcançáveis com o controle na árvore.
        Loaded += (_, _) => { if (Visor == null) Montar(); };

        // O documento de fluxo engole a roda do mouse; aqui ela segue para a lista de falas.
        PreviewMouseWheel += (s, e) =>
        {
            if (e.Handled || Parent is not UIElement pai) return;

            e.Handled = true;
            pai.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = s
            });
        };
    }

    private void Montar()
    {
        if (TryFindResource("TextBodyBrush") == null) return; // ainda fora da árvore

        if (Visor != null)
        {
            Visor.Markdown = Texto ?? "";
            return;
        }

        Visor = VisorDeMarkdown.Criar(Texto, this);
        Content = VisorDeMarkdown.Encolhido(Visor);
    }
}
