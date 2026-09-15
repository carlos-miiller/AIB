using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIB.Services;
using AIB.Ui;

using Brush = System.Windows.Media.Brush;
using Path = System.Windows.Shapes.Path;
using PenLineCap = System.Windows.Media.PenLineCap;
using PenLineJoin = System.Windows.Media.PenLineJoin;
using Stretch = System.Windows.Media.Stretch;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace AIB.Views;

/// <summary>
/// O painel lateral — §6 da spec de chat, como SEGUNDA janela flutuante de 300x520.
/// <para>
/// Janela própria, e não um painel embutido, por causa de A2: como painel, ele dividia a
/// largura com o chat e fazia a janela de conversa encolher ao abrir. Aqui a de chat tem
/// largura fixa e o painel simplesmente aparece ao lado, alinhado pela base.
/// </para>
/// <para>
/// Três abas: Histórico, Arquivos e Ações. A de E-MAILS saiu daqui — virou o modo e-mail da
/// área central (§3.10/§3.11), que tem largura de janela para o acordeão e para a leitura com a
/// IA. As de Lembretes, Memória Recente, Arquivos Recentes e Shadow não constam da spec e
/// ficaram fora desta tela — os serviços delas seguem intactos, para revisão futura.
/// </para>
/// </summary>
public partial class SidePanelWindow : Window
{
    private readonly Action<ChatSession>? _aoRecuperarChat;
    private readonly Action<ChatSession>? _aoAbrirChat;
    private readonly Action<ChatSession>? _aoExcluirChat;

    /// <summary>
    /// Qual conversa está aberta agora. É função e não valor porque o painel é montado uma vez
    /// e recarregado muitas: guardar o id da abertura deixaria a marcação presa na conversa
    /// errada assim que o usuário começasse outra.
    /// </summary>
    private readonly Func<string?>? _sessaoAtiva;

    public SidePanelWindow(
        Action<ChatSession>? aoRecuperarChat = null,
        Action<ChatSession>? aoAbrirChat = null,
        Action<ChatSession>? aoExcluirChat = null,
        Func<string?>? sessaoAtiva = null)
    {
        InitializeComponent();
        _aoRecuperarChat = aoRecuperarChat;
        _aoAbrirChat = aoAbrirChat;
        _aoExcluirChat = aoExcluirChat;
        _sessaoAtiva = sessaoAtiva;

        // As listas são observáveis: o painel acompanha sem consultar. Sem isto, abrir o painel
        // mostraria o estado do momento da abertura e congelaria.
        ContextService.ActiveFiles.CollectionChanged += Arquivos_Mudaram;
        ActionLogService.Entries.CollectionChanged += Acoes_Mudaram;

        Closed += (_, _) =>
        {
            ContextService.ActiveFiles.CollectionChanged -= Arquivos_Mudaram;
            ActionLogService.Entries.CollectionChanged -= Acoes_Mudaram;
        };

        Recarregar();
    }

    private void Arquivos_Mudaram(object? s, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(MontarArquivos));

    private void Acoes_Mudaram(object? s, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(MontarAcoes));

    /// <summary>Remonta as três abas.</summary>
    public void Recarregar()
    {
        MontarHistorico();
        MontarArquivos();
        MontarAcoes();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Janela
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Arrastar pelo cabeçalho do painel move a CONVERSA, não o painel.
    /// <para>
    /// O painel é uma janela separada por causa de A2 — como painel embutido ele encolhia a
    /// conversa ao abrir —, mas separado não quer dizer independente: os dois são uma peça só
    /// na tela. Com o <c>DragMove</c> próprio que havia aqui, dava para descolar o painel e
    /// deixá-lo perdido num canto, sem nada que o trouxesse de volta.
    /// </para>
    /// <para>
    /// Move-se a dona e o painel vem atrás pelo <c>LocationChanged</c> dela. O
    /// <c>DragMove</c> do WPF não serve para isso porque move a janela em que o clique
    /// aconteceu; aqui o arrasto é feito à mão, por diferença entre duas posições do cursor.
    /// </para>
    /// </summary>
    private System.Windows.Point? _arrasteAnterior;

    private void Cabecalho_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // Sem dona não há o que arrastar junto: volta a mover a si mesmo. É o caso dos
        // ensaios, que montam o painel sozinho.
        if (Owner == null)
        {
            DragMove();
            return;
        }

        _arrasteAnterior = PontoNaTela(e);
        ((UIElement)sender).CaptureMouse();
    }

    private void Cabecalho_MouseMove(object sender, MouseEventArgs e)
    {
        if (_arrasteAnterior == null || Owner == null) return;

        var agora = PontoNaTela(e);

        Owner.Left += agora.X - _arrasteAnterior.Value.X;
        Owner.Top += agora.Y - _arrasteAnterior.Value.Y;

        // A dona reposiciona o painel, e o cursor acaba sobre o mesmo ponto do cabeçalho de
        // onde saiu — por isso a referência é a posição NOVA, e não a do começo do arrasto.
        _arrasteAnterior = agora;
    }

    private void Cabecalho_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _arrasteAnterior = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    /// <summary>
    /// Posição do cursor na tela, em unidades independentes de dispositivo.
    /// <para>
    /// O <see cref="Visual.PointToScreen"/> devolve PIXEL FÍSICO, e <c>Left</c>/<c>Top</c> de
    /// uma janela são DIP. Somar um no outro só coincide a 100% de escala; a 125% o painel
    /// andaria um quarto a mais que o cursor.
    /// </para>
    /// </summary>
    private System.Windows.Point PontoNaTela(MouseEventArgs e)
    {
        var fisico = PointToScreen(e.GetPosition(this));
        var transformacao = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;

        return transformacao.HasValue ? transformacao.Value.Transform(fisico) : fisico;
    }

    /// <summary>
    /// Disparado quando o usuário fecha o painel pelo X dele.
    /// <para>
    /// Distinto de simplesmente ficar invisível: a conversa esconde os dois ao perder o foco, e
    /// precisa saber diferenciar "sumiu junto comigo" de "o usuário fechou" para decidir se o
    /// painel volta na próxima vez que a conversa aparecer.
    /// </para>
    /// </summary>
    public event Action? FechadoPeloUsuario;

    private void Fechar_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        FechadoPeloUsuario?.Invoke();
    }

    private void Aba_Checked(object sender, RoutedEventArgs e)
    {
        // Disparado pelo IsChecked do XAML antes de InitializeComponent terminar.
        if (PaneHistorico == null) return;

        PaneHistorico.Visibility = AbaHistorico.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PaneArquivos.Visibility = AbaArquivos.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PaneAcoes.Visibility = AbaAcoes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────
    // §6.1  Histórico de chats
    // ─────────────────────────────────────────────────────────────────────

    private void MontarHistorico()
    {
        // As nascidas de um e-mail (§3.11) ficam de fora — quem decide isso é o serviço, e o
        // porquê está lá. Elas são gravadas normalmente e voltam pelo próprio e-mail.
        var sessoes = ChatHistoryService.ConversasDoUsuario();
        string? ativa = _sessaoAtiva?.Invoke();
        ListaHistorico.Items.Clear();

        foreach (var sessao in sessoes)
        {
            bool emAndamento = ativa != null && sessao.Id == ativa;

            var titulo = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(sessao.Title) ? "(sem título)" : sessao.Title,
                Style = (Style)FindResource("ItemTitulo"),
                TextWrapping = TextWrapping.NoWrap
            };

            var quando = new TextBlock
            {
                Text = Relativo(sessao.Timestamp),
                Style = (Style)FindResource("ItemMeta"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                // Sem quebra e com folga à esquerda: a data é curta e fixa, e o título é que
                // deve ceder espaço. Sem isto "2 h" quebrava em duas linhas e desalinhava a
                // altura do item inteiro.
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var linhaTopo = new Grid();
            linhaTopo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            linhaTopo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(quando, 1);
            linhaTopo.Children.Add(titulo);
            linhaTopo.Children.Add(quando);

            var trecho = new TextBlock
            {
                Text = PrimeiraLinha(sessao.Content),
                FontSize = 11.5,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var corpo = new StackPanel();
            corpo.Children.Add(linhaTopo);
            corpo.Children.Add(trecho);

            if (emAndamento) corpo.Children.Add(PillEmAndamento());

            var card = new Border { Style = (Style)FindResource("ItemCard"), Child = corpo };
            var alvo = sessao;

            // A conversa em andamento aparece na lista, mas não se abre nem se apaga: ela já
            // está na tela. Recuperar o contexto dela dentro dela mesma duplicaria a conversa
            // no próprio prompt, e excluí-la apagaria o registro do que o usuário está lendo,
            // enquanto os turnos seguintes continuariam gravando — o registro voltaria sozinho.
            if (emAndamento)
            {
                card.Cursor = System.Windows.Input.Cursors.Arrow;
                card.Opacity = 0.85;
            }
            else
            {
                // O clique esquerdo continua sendo o que sempre foi: recuperar o contexto
                // dentro da conversa corrente. Mexer nisso quebraria a mão de quem já usa o
                // painel.
                card.MouseLeftButtonUp += (_, _) => _aoRecuperarChat?.Invoke(alvo);
                card.ContextMenu = MenuDaConversa(alvo);
            }

            ListaHistorico.Items.Add(card);
        }

        HistoricoVazio.Visibility = ListaHistorico.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // E10 — o rodapé some junto com a lista: "0 chats • 0 B" ao lado de "Nenhum chat
        // salvo." é a mesma frase dita duas vezes, uma delas em números.
        var (chats, bytes) = ChatHistoryService.Peso();
        HistoricoRodapeCaixa.Visibility = chats == 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoricoRodape.Text = $"{chats} chat{(chats == 1 ? "" : "s")}"
                               + $" • {ContextService.Humanizar(bytes)} em disco";
    }

    /// <summary>
    /// Marca visual da conversa que está aberta. Sem ela, o item que não responde ao clique
    /// pareceria um item quebrado.
    /// </summary>
    private Border PillEmAndamento() => new()
    {
        CornerRadius = new CornerRadius(20),
        Padding = new Thickness(7, 1, 7, 1),
        BorderThickness = new Thickness(1),
        Background = (Brush)FindResource("AccentFill12Brush"),
        BorderBrush = (Brush)FindResource("AccentBorderBrush"),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        Margin = new Thickness(0, 6, 0, 0),
        Child = new TextBlock
        {
            Text = "em andamento",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentLilacBrush")
        }
    };

    /// <summary>
    /// Menu do botão direito de um item do histórico.
    /// <para>
    /// As duas primeiras opções são coisas diferentes, e a diferença importa: RECUPERAR traz a
    /// conversa antiga para dentro da atual, como material de consulta, e o que está na tela
    /// continua lá; ABRIR troca a conversa da tela pela antiga, que volta a ser a conversa
    /// corrente. Uma soma, a outra substitui.
    /// </para>
    /// </summary>
    private ContextMenu MenuDaConversa(ChatSession sessao)
    {
        var menu = new ContextMenu();

        var recuperar = new MenuItem { Header = "Recuperar contexto na conversa atual" };
        recuperar.Click += (_, _) => _aoRecuperarChat?.Invoke(sessao);

        var abrir = new MenuItem { Header = "Abrir conversa" };
        abrir.Click += (_, _) => _aoAbrirChat?.Invoke(sessao);

        var excluir = new MenuItem { Header = "Excluir", Tag = "perigo" };
        excluir.Click += (_, _) => _aoExcluirChat?.Invoke(sessao);

        menu.Items.Add(recuperar);
        menu.Items.Add(abrir);
        menu.Items.Add(new Separator());
        menu.Items.Add(excluir);

        return menu;
    }

    // ─────────────────────────────────────────────────────────────────────
    // §6.2  Arquivos no contexto
    // ─────────────────────────────────────────────────────────────────────

    private void MontarArquivos()
    {
        ListaArquivos.Items.Clear();

        foreach (var arquivo in ContextService.ActiveFiles)
            ListaArquivos.Items.Add(MontarItemDeArquivo(arquivo));

        ArquivosVazio.Visibility = ListaArquivos.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int n = ContextService.ActiveFiles.Count;
        ArquivosRodape.Text = n == 0
            ? "Nada no contexto."
            : $"{n} arquivo{(n == 1 ? "" : "s")} • {ContextService.Humanizar(ContextService.TotalBytes())}";
    }

    private Border MontarItemDeArquivo(ContextFile arquivo)
    {
        var grade = new Grid();
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // (a) Ícone pelo TIPO do arquivo. Aqui NÃO é a operação — ela aparece na pill de
        // origem, logo abaixo. É a distinção deliberada de §6.2 contra §4.5.
        var caixa = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("AccentFill12Brush"),
            BorderBrush = (Brush)FindResource("AccentEdge24Brush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                Data = FileTypeIcons.De(arquivo.Extension),
                Stroke = (Brush)FindResource("AccentLilacBrush"),
                StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var nome = new TextBlock { Text = arquivo.Name, Style = (Style)FindResource("ItemTitulo") };

        // (c) A pasta é cortada pelo COMEÇO: o fim do caminho é a parte informativa.
        // O corte é pelo COMEÇO, e a apara automática do TextBlock fica DESLIGADA aqui.
        // As duas juntas se somavam: Encurtar tirava o começo, o TextTrimming tirava o fim, e
        // sobrava um caminho sem as duas pontas — pior que qualquer uma das aparas sozinha.
        var pasta = new TextBlock
        {
            Text = ActionLogService.Encurtar(arquivo.Folder, 28),
            Style = (Style)FindResource("ItemMeta"),
            TextTrimming = TextTrimming.None,
            ToolTip = arquivo.FilePath,
            Margin = new Thickness(0, 2, 0, 0)
        };

        // (d) Pill de origem: lilás quando foi a IA, cinza quando foi o usuário.
        var pill = new Border
        {
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(7, 1, 7, 1),
            BorderThickness = new Thickness(1),
            Background = (Brush)FindResource(arquivo.ByAi ? "AccentFill12Brush" : "SurfaceHoverSoftBrush"),
            BorderBrush = (Brush)FindResource(arquivo.ByAi ? "AccentBorderBrush" : "BorderCardBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 5, 0, 0),
            Child = new TextBlock
            {
                Text = arquivo.OriginLabel,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource(arquivo.ByAi ? "AccentLilacBrush" : "TextMutedBrush")
            }
        };

        var corpo = new StackPanel();
        corpo.Children.Add(nome);
        corpo.Children.Add(pasta);
        corpo.Children.Add(pill);
        Grid.SetColumn(corpo, 1);

        // (e) X de remover: some por padrão, aparece no hover do ITEM. Remove do CONTEXTO,
        // nunca do disco.
        var remover = new Button
        {
            Width = 22,
            Height = 22,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Opacity = 0,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Tirar do contexto (não apaga o arquivo)",
            Content = new Path
            {
                Data = System.Windows.Media.Geometry.Parse("M4,4 L11,11 M11,4 L4,11"),
                Stroke = (Brush)FindResource("TextMutedBrush"),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 15,
                Height = 15,
                Stretch = Stretch.None
            }
        };

        remover.Template = (ControlTemplate)XamlSemBorda();
        Grid.SetColumn(remover, 2);

        grade.Children.Add(caixa);
        grade.Children.Add(corpo);
        grade.Children.Add(remover);

        var card = new Border { Style = (Style)FindResource("ItemCard"), Child = grade };

        card.MouseEnter += (_, _) => remover.Opacity = 1;
        card.MouseLeave += (_, _) => remover.Opacity = 0;

        // Clicar no item abre o Explorer com o arquivo SELECIONADO.
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            RevelarNoExplorer(arquivo.FilePath);
        };

        // O X precisa marcar o evento como tratado, senão o clique sobe para o card e abre o
        // Explorer junto com a remoção.
        remover.Click += (_, e) =>
        {
            ContextService.RemoveFile(arquivo);
            e.Handled = true;
        };

        return card;
    }

    // ─────────────────────────────────────────────────────────────────────
    // §6.3  Histórico de ações
    // ─────────────────────────────────────────────────────────────────────

    private void MontarAcoes()
    {
        ListaAcoes.Items.Clear();

        string? diaAnterior = null;

        foreach (var acao in ActionLogService.Entries)
        {
            if (acao.DiaRotulo != diaAnterior)
            {
                ListaAcoes.Items.Add(SeparadorDeDia(acao.DiaRotulo));
                diaAnterior = acao.DiaRotulo;
            }

            ListaAcoes.Items.Add(MontarItemDeAcao(acao));
        }

        AcoesVazio.Visibility = ListaAcoes.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int total = ActionLogService.Entries.Count;
        int falhas = ActionLogService.Falhas;
        AcoesRodape.Text = total == 0
            ? "Nada registrado."
            : $"{total} açã{(total == 1 ? "o" : "es")}" + (falhas > 0 ? $" • {falhas} com falha" : "");
    }

    /// <summary>Rótulo do dia seguido de uma régua que ocupa o resto da linha.</summary>
    private Grid SeparadorDeDia(string rotulo)
    {
        var grade = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var texto = new TextBlock
        {
            Text = rotulo,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        LetterSpacing.SetEm(texto, 0.08);

        var regua = new System.Windows.Shapes.Rectangle
        {
            Height = 1,
            Fill = (Brush)FindResource("DividerBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(regua, 1);

        grade.Children.Add(texto);
        grade.Children.Add(regua);
        return grade;
    }

    private Border MontarItemDeAcao(ActionLogEntry acao)
    {
        var grade = new Grid();
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Marcador redondo. Três cores: escrita em lilás, destrutiva/falha em vermelho,
        // leitura em cinza — leitura não deve competir visualmente com escrita.
        string fundo = acao.Status == ActionStatus.Failed ? "DangerFill12Brush"
                     : acao.SoLeitura ? "ReadFill05Brush"
                     : "AccentFill12Brush";

        string traco = acao.Status == ActionStatus.Failed ? "DangerBrush"
                     : acao.SoLeitura ? "TextSecondaryBrush"
                     : "AccentLilacBrush";

        var marcador = new Border
        {
            Width = 25,
            Height = 25,
            CornerRadius = new CornerRadius(13),
            Background = (Brush)FindResource(fundo),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new Path
            {
                Data = ToolIcons.De(acao.Tool, acao.Kind),
                Stroke = (Brush)FindResource(traco),
                StrokeThickness = 1.3,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var topo = new Grid();
        topo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var ferramenta = new TextBlock
        {
            Text = acao.Tool,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextStrongBrush")
        };

        var hora = new TextBlock
        {
            Text = acao.Hora,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        Grid.SetColumn(hora, 1);

        topo.Children.Add(ferramenta);
        topo.Children.Add(hora);

        var corpo = new StackPanel();
        corpo.Children.Add(topo);

        var alvo = new TextBlock
        {
            Text = acao.Target,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily"),
            FontSize = 10.5,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            // Mesmo motivo do item de arquivo: só o corte pelo começo, que preserva o fim.
            TextTrimming = TextTrimming.None,
            Margin = new Thickness(0, 3, 0, 0),
            // Tooltip em BLOCO: o caminho inteiro, sem abreviação (§4.6 ii).
            ToolTip = TooltipEmBloco(acao),
            Cursor = Cursors.Help
        };
        TooltipDeAcao.Configurar(alvo);
        corpo.Children.Add(alvo);

        if (!string.IsNullOrWhiteSpace(acao.Result))
        {
            corpo.Children.Add(new TextBlock
            {
                Text = acao.Result,
                FontSize = 10.5,
                Foreground = (Brush)FindResource(
                    acao.Status == ActionStatus.Failed ? "DangerBrush" : "TextMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        Grid.SetColumn(corpo, 1);
        grade.Children.Add(marcador);
        grade.Children.Add(corpo);

        var card = new Border { Style = (Style)FindResource("ItemCard"), Child = grade };
        card.MouseLeftButtonUp += (_, _) => RevelarNoExplorer(acao.FullTarget);
        return card;
    }

    /// <summary>
    /// Tooltip em bloco de §4.6(ii), no desenho do .action-tooltip.block do mock.
    /// <para>
    /// Largura FIXA de 340 (D9): o conteúdo é monoespaçado, e largura variável fazia cada
    /// entrada abrir um tooltip de tamanho diferente. Cada seção tem padding próprio e a
    /// divisória fica ENTRE elas, na borda de cima da seguinte. O RESULTADO é o único texto
    /// colorido (D11) — é o resumo que se procura primeiro. A SAÍDA BRUTA tem caixa e teto de
    /// altura (D10): sem eles, um comando verboso esticava o tooltip para fora da tela.
    /// </para>
    /// </summary>
    private System.Windows.Controls.ToolTip TooltipEmBloco(ActionLogEntry acao)
    {
        var pilha = new StackPanel { Width = (double)FindResource("TooltipBlockWidth") };
        var mono = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily");

        void Secao(string rotulo, UIElement conteudo)
        {
            var titulo = new TextBlock
            {
                Text = rotulo,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 0, 0, 5)
            };
            LetterSpacing.SetEm(titulo, 0.10);

            var corpo = new StackPanel();
            corpo.Children.Add(titulo);
            corpo.Children.Add(conteudo);

            var secao = new Border { Padding = new Thickness(12, 9, 12, 9), Child = corpo };
            if (pilha.Children.Count > 0)
            {
                secao.BorderBrush = (Brush)FindResource("DividerTooltipBrush");
                secao.BorderThickness = new Thickness(0, 1, 0, 0);
            }

            pilha.Children.Add(secao);
        }

        TextBlock Texto(string conteudo, string cor) => new()
        {
            Text = conteudo,
            FontFamily = mono,
            FontSize = 11,
            Foreground = (Brush)FindResource(cor),
            TextWrapping = TextWrapping.Wrap
        };

        Secao(acao.RotuloDoAlvo, Texto(acao.FullTarget, "TextBodyBrush"));

        if (!string.IsNullOrWhiteSpace(acao.Result))
        {
            bool falhou = acao.Status == ActionStatus.Failed;
            Secao(falhou ? "ERRO" : "RESULTADO",
                  Texto(acao.Result, falhou ? "DangerBrush" : "AccentLilacBrush"));
        }

        if (acao.Troca is { } troca)
            Secao("ANTES E DEPOIS", AntesEDepois(troca, mono));

        if (!string.IsNullOrWhiteSpace(acao.RawOutput))
        {
            Secao("SAÍDA BRUTA", new Border
            {
                Background = (Brush)FindResource("SurfaceCodeBrush"),
                BorderBrush = (Brush)FindResource("DividerBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 8, 9, 8),
                MaxHeight = (double)FindResource("TooltipRawMaxHeight"),
                ClipToBounds = true,
                // Sem quebra, como o console: a saída alinhada em colunas perde o sentido
                // quando cada linha dobra num ponto diferente.
                Child = new TextBlock
                {
                    Text = acao.RawOutput,
                    FontFamily = mono,
                    FontSize = 10.5,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    TextWrapping = TextWrapping.NoWrap
                }
            });
        }

        // Padding zero: a variante em bloco não tem o respiro da simples, cada seção traz o seu.
        return new System.Windows.Controls.ToolTip { Padding = new Thickness(0), Content = pilha };
    }

    /// <summary>Linhas mostradas de cada lado. O resto vira uma linha "… +N linhas".</summary>
    private const int LinhasPorLadoDaTroca = 6;

    /// <summary>
    /// A seção ANTES E DEPOIS: as linhas que saíram em vermelho com "−", as que entraram em verde
    /// com "+", na mesma caixa escura da SAÍDA BRUTA.
    /// <para>
    /// O teto é POR LADO, e não da caixa: com um teto só, um "antes" comprido empurraria o
    /// "depois" para fora da área visível — e o depois é metade do que se veio ver.
    /// </para>
    /// </summary>
    private Border AntesEDepois(TrocaDeTexto troca, System.Windows.Media.FontFamily mono)
    {
        var linhas = new StackPanel();

        TextBlock Linha(string texto, string cor) => new()
        {
            Text = texto,
            FontFamily = mono,
            FontSize = 10.5,
            Foreground = (Brush)FindResource(cor),
            TextWrapping = TextWrapping.NoWrap,
            // Linha que ainda não cabe termina em "…" em vez de ser cortada seca na borda:
            // o corte seco não avisa que há mais.
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        void Lado(IReadOnlyList<string> todas, string sinal, string cor, string fundo)
        {
            if (todas.Count == 0)
            {
                // new_string vazio é apagar o trecho. Sem esta linha a caixa terminaria no
                // vermelho, e pareceria que o "depois" se perdeu.
                linhas.Children.Add(new Border
                {
                    Padding = new Thickness(8, 1, 8, 1),
                    Child = Linha("(trecho apagado)", "TextMutedBrush")
                });
                return;
            }

            foreach (string linha in todas.Take(LinhasPorLadoDaTroca))
            {
                linhas.Children.Add(new Border
                {
                    Background = (Brush)FindResource(fundo),
                    Padding = new Thickness(8, 1, 8, 1),
                    Child = Linha($"{sinal} {linha}", cor)
                });
            }

            if (todas.Count > LinhasPorLadoDaTroca)
            {
                linhas.Children.Add(new Border
                {
                    Padding = new Thickness(8, 1, 8, 1),
                    Child = Linha($"  … +{todas.Count - LinhasPorLadoDaTroca} linhas", "TextMutedBrush")
                });
            }
        }

        var (antes, depois) = troca.LinhasParaExibir();
        Lado(antes, "−", "DangerTextBrush", "DangerFill12Brush");
        Lado(depois, "+", "SuccessBrush", "SuccessFill10Brush");

        return new Border
        {
            Background = (Brush)FindResource("SurfaceCodeBrush"),
            BorderBrush = (Brush)FindResource("DividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(0, 6, 0, 6),
            MaxHeight = (double)FindResource("TooltipDiffMaxHeight"),
            ClipToBounds = true,
            Child = linhas
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Ações
    // ─────────────────────────────────────────────────────────────────────

    private void Adicionar_Click(object sender, RoutedEventArgs e)
    {
        var dialogo = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Adicionar arquivos ao contexto"
        };

        if (dialogo.ShowDialog() != true) return;

        foreach (string caminho in dialogo.FileNames)
            ContextService.AddFile(caminho, ContextOrigin.AttachedByUser);
    }

    /// <summary>
    /// Abre o Explorer com o item SELECIONADO. Falha silenciosa por design: o caminho pode não
    /// existir mais, e um erro modal por causa de um clique de navegação seria pior.
    /// </summary>
    private static void RevelarNoExplorer(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho)) return;

        try
        {
            if (!System.IO.File.Exists(caminho) && !System.IO.Directory.Exists(caminho)) return;
            Process.Start("explorer.exe", $"/select,\"{caminho}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PAINEL] Não foi possível abrir o Explorer: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Utilidades
    // ─────────────────────────────────────────────────────────────────────

    private static string Relativo(DateTime quando)
    {
        var diferenca = DateTime.Now - quando;

        if (diferenca.TotalMinutes < 1) return "agora";
        if (diferenca.TotalMinutes < 60) return $"{(int)diferenca.TotalMinutes} min";
        if (diferenca.TotalHours < 24) return $"{(int)diferenca.TotalHours} h";
        if (diferenca.TotalDays < 7) return $"{(int)diferenca.TotalDays} d";

        return quando.ToString("dd/MM");
    }

    private static string PrimeiraLinha(string? conteudo)
    {
        if (string.IsNullOrWhiteSpace(conteudo)) return "(vazio)";

        foreach (var linha in conteudo.Split('\n'))
        {
            string limpa = linha.Trim();
            if (limpa.Length > 0) return limpa.Length > 90 ? limpa[..90] + "…" : limpa;
        }

        return "(vazio)";
    }

    /// <summary>Template de botão sem casca nenhuma, só o conteúdo.</summary>
    private static object XamlSemBorda()
    {
        var template = new ControlTemplate(typeof(Button));
        var apresentador = new FrameworkElementFactory(typeof(ContentPresenter));
        apresentador.SetValue(HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        apresentador.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = apresentador;
        return template;
    }
}
