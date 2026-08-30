using System;
using System.Collections.Specialized;
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

namespace AIB.Views;

/// <summary>
/// O painel lateral — §6 da spec de chat, como SEGUNDA janela flutuante de 300x520.
/// <para>
/// Janela própria, e não um painel embutido, por causa de A2: como painel, ele dividia a
/// largura com o chat e fazia a janela de conversa encolher ao abrir. Aqui a de chat tem
/// largura fixa e o painel simplesmente aparece ao lado, alinhado pela base.
/// </para>
/// <para>
/// Três abas. As de Lembretes, Memória Recente, Arquivos Recentes e Shadow não constam da spec
/// e ficaram fora desta tela — os serviços delas seguem intactos, para revisão futura.
/// </para>
/// </summary>
public partial class SidePanelWindow : Window
{
    private readonly Action<ChatSession>? _aoRecuperarChat;

    public SidePanelWindow(Action<ChatSession>? aoRecuperarChat = null)
    {
        InitializeComponent();
        _aoRecuperarChat = aoRecuperarChat;

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

    private void Cabecalho_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Fechar_Click(object sender, RoutedEventArgs e) => Hide();

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
        var sessoes = ChatHistoryService.LoadHistory();
        ListaHistorico.Items.Clear();

        foreach (var sessao in sessoes)
        {
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

            var card = new Border { Style = (Style)FindResource("ItemCard"), Child = corpo };
            var alvo = sessao;
            card.MouseLeftButtonUp += (_, _) => _aoRecuperarChat?.Invoke(alvo);

            ListaHistorico.Items.Add(card);
        }

        HistoricoVazio.Visibility = ListaHistorico.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                Data = ToolIcons.De(acao.Kind),
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
            ToolTip = TooltipEmBloco(acao)
        };
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
    /// Tooltip em bloco de §4.6(ii): seções empilhadas com rótulo em caixa alta.
    /// </summary>
    private object TooltipEmBloco(ActionLogEntry acao)
    {
        var pilha = new StackPanel { MaxWidth = 340 };

        void Secao(string rotulo, string? conteudo, bool mono = true)
        {
            if (string.IsNullOrWhiteSpace(conteudo)) return;

            if (pilha.Children.Count > 0)
            {
                pilha.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Height = 1,
                    Fill = (Brush)FindResource("DividerTooltipBrush"),
                    Margin = new Thickness(0, 7, 0, 7)
                });
            }

            var titulo = new TextBlock
            {
                Text = rotulo,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMutedBrush")
            };
            LetterSpacing.SetEm(titulo, 0.10);

            pilha.Children.Add(titulo);
            pilha.Children.Add(new TextBlock
            {
                Text = conteudo,
                FontFamily = mono ? (System.Windows.Media.FontFamily)FindResource("MonoFontFamily") : null,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextBodyBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        Secao(acao.Command != null ? "COMANDO" : "CAMINHO COMPLETO", acao.FullTarget);
        Secao("RESULTADO", acao.Result);
        Secao("SAÍDA BRUTA", acao.RawOutput);

        return pilha;
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
