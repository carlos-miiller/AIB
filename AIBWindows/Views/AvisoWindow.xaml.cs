using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIB.Services;
using AIB.Ui;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace AIB.Views;

/// <summary>
/// Um aviso do AIB fora da janela principal — ver o XAML.
/// <para>
/// Montado por <see cref="Montar"/> a partir de uma <see cref="Receita"/>: quem decide o que o
/// aviso diz são os construtores de <see cref="Avisos"/>; aqui só se desenha.
/// </para>
/// </summary>
public partial class AvisoWindow : Window
{
    /// <summary>A cor do aviso — a da pill e a do botão principal.</summary>
    public enum Tom { Lilas, Azul, Perigo, Ouro, Verde, Cuidado }

    /// <summary>Um botão do rodapé. O principal vem primeiro e leva a cor do aviso.</summary>
    public sealed record Botao(string Texto, Action Ao, bool Principal = false, bool Fecha = true);

    /// <summary>Tudo o que o aviso mostra. Campos vazios somem da janela.</summary>
    public sealed record Receita(
        Tom Tom,
        string Pill,
        string Icone,
        string Titulo = "",
        string Sub = "",
        string Fala = "",
        IReadOnlyList<MailSummary>? Emails = null,
        IReadOnlyList<Botao>? Botoes = null,
        bool Shadow = false,
        DateTime? Hora = null,
        TimeSpan? FechaSozinho = null,
        string Dica = "");

    /// <summary>Margem transparente abaixo da casca (ver o XAML).</summary>
    private const double SombraEmbaixo = 30;

    public AvisoWindow()
    {
        InitializeComponent();
    }

    /// <summary>A receita desenhada — para o ensaio conferir.</summary>
    public Receita? Desenhada { get; private set; }

    /// <summary>Uma linha de e-mail foi clicada.</summary>
    public event Action<MailSummary>? EmailClicado;

    // ─────────────────────────────────────────────────────────────────────
    // Ícones da pill, no mesmo traço dos do mockup
    // ─────────────────────────────────────────────────────────────────────

    public const string IconeEnvelope = "M3,5 H15 V13 H3 Z M3,5 L9,10 L15,5";
    public const string IconeAlerta = "M9,2 L16.5,15.5 H1.5 Z M9,7 V10.5 M9,12.8 V13.2";
    public const string IconeSino = "M5,12 V8 A4,4 0 0 1 13,8 V12 L14.5,14 H3.5 Z M7.5,16 H10.5";
    public const string IconeSaida = "M3,9 L9,3 M4.5,3 H9 V7.5";
    public const string IconeOk = "M4,9.5 L7.2,12.5 L14,5.5";

    /// <summary>Monta a janela a partir da receita. Não mostra: quem mostra é a fila.</summary>
    public static AvisoWindow Montar(Receita receita)
    {
        var janela = new AvisoWindow();
        janela.Desenhar(receita);

        PertoDoOrbe.PosicionarOuNaBandeja(janela, SombraEmbaixo);
        return janela;
    }

    private void Desenhar(Receita r)
    {
        Desenhada = r;

        var (texto, fundo, borda, botao) = Cores(r.Tom);
        Pill.SetResourceReference(Border.BackgroundProperty, fundo);
        Pill.SetResourceReference(Border.BorderBrushProperty, borda);
        PillTexto.SetResourceReference(TextBlock.ForegroundProperty, texto);
        PillIcone.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, texto);
        PillIcone.Data = Geometry.Parse(r.Icone);
        PillTexto.Text = r.Pill;

        SeloShadow.Visibility = r.Shadow ? Visibility.Visible : Visibility.Collapsed;
        HoraTexto.Text = (r.Hora ?? DateTime.Now).ToString("HH:mm");

        Mostrar(TituloTexto, r.Titulo);
        Mostrar(SubTexto, r.Sub);
        Mostrar(FalaTexto, r.Fala);
        DicaTexto.Text = r.Dica;

        var emails = r.Emails ?? Array.Empty<MailSummary>();
        CaixaDeLinhas.Visibility = emails.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var email in emails) Linhas.Children.Add(Linha(email));

        foreach (var b in r.Botoes ?? Array.Empty<Botao>())
        {
            var botaoNovo = new System.Windows.Controls.Button
            {
                Content = b.Texto,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(14, 7, 14, 7)
            };
            botaoNovo.SetResourceReference(StyleProperty, b.Principal ? botao : "OutlineButton");

            var acao = b;
            botaoNovo.Click += (_, _) =>
            {
                acao.Ao?.Invoke();
                if (acao.Fecha) Close();
            };

            Botoes.Children.Add(botaoNovo);
        }

        if (r.FechaSozinho is TimeSpan prazo && prazo > TimeSpan.Zero)
            ArmarContagem(prazo);

        // O "+N" da fila: os outros avisos que esperam atrás deste.
        var fila = Flutuantes.Fila;
        void Atualizar()
        {
            int n = ReferenceEquals(fila.Atual, this) ? fila.Esperando : 0;
            SeloFila.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            SeloFilaTexto.Text = $"+{n}";
        }

        fila.Mudou += Atualizar;
        Closed += (_, _) => fila.Mudou -= Atualizar;
        Loaded += (_, _) => Atualizar();
    }

    private static void Mostrar(TextBlock bloco, string texto)
    {
        bloco.Text = texto ?? "";
        bloco.Visibility = string.IsNullOrWhiteSpace(texto) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Texto, fundo e borda da pill, e o estilo do botão principal, por tom.</summary>
    public static (string Texto, string Fundo, string Borda, string Botao) Cores(Tom tom) => tom switch
    {
        Tom.Perigo => ("DangerTextBrush", "DangerFill14Brush", "DangerEdge32Brush", "DangerFilledButton"),
        Tom.Cuidado => ("WarnBrush", "WarnFill10Brush", "WarnBorderBrush", "WarnFilledButton"),
        Tom.Ouro => ("GoldBrush", "GoldFill12Brush", "GoldEdge32Brush", "AccentFilledButton"),
        Tom.Azul => ("AccentBlueBrush", "AccentBlueFill14Brush", "AccentBlueEdge38Brush", "AccentFilledButton"),
        Tom.Verde => ("SuccessBrush", "SuccessFill10Brush", "SuccessEdge30Brush", "AccentFilledButton"),
        _ => ("AccentLilacBrush", "AccentFill12Brush", "AccentBorderStrongBrush", "AccentFilledButton")
    };

    /// <summary>Uma linha de e-mail: barra de urgência, assunto, remetente e o selo de urgência.</summary>
    private FrameworkElement Linha(MailSummary email)
    {
        var grade = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var barra = new Border { CornerRadius = new CornerRadius(2), Background = UrgenciaConverter.CorDe(email.Urgency) };
        Grid.SetColumn(barra, 0);

        var textos = new StackPanel { Margin = new Thickness(10, 0, 8, 0) };
        var assunto = new TextBlock
        {
            Text = email.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        assunto.SetResourceReference(TextBlock.ForegroundProperty, "TextStrongBrush");

        string quem = NomeDe(email.De);
        var detalhe = new TextBlock
        {
            Text = string.Join(" · ", new[] { quem, email.Description }.Where(s => !string.IsNullOrWhiteSpace(s))),
            FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0)
        };
        detalhe.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        textos.Children.Add(assunto);
        textos.Children.Add(detalhe);
        Grid.SetColumn(textos, 1);

        var selo = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)new UrgenciaConverter { Modo = UrgenciaConverter.Saida.Fundo }
                .Convert(email.Urgency, typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture)
        };
        selo.Child = new TextBlock
        {
            Text = UrgenciaConverter.RotuloDe(email.Urgency), FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = UrgenciaConverter.CorDe(email.Urgency)
        };
        Grid.SetColumn(selo, 2);

        grade.Children.Add(barra);
        grade.Children.Add(textos);
        grade.Children.Add(selo);

        var casca = new Border
        {
            Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = grade,
            ToolTip = "Abrir este e-mail"
        };
        casca.MouseEnter += (_, _) => casca.SetResourceReference(Border.BackgroundProperty, "SurfaceHoverSoftBrush");
        casca.MouseLeave += (_, _) => casca.Background = Brushes.Transparent;
        casca.MouseLeftButtonUp += (_, _) =>
        {
            EmailClicado?.Invoke(email);
            Close();
        };

        return casca;
    }

    /// <summary>"Giulia Mesquita &lt;g@x.com&gt;" vira "Giulia Mesquita"; só o endereço fica como está.</summary>
    public static string NomeDe(string? de)
    {
        string t = (de ?? "").Trim();
        int abre = t.IndexOf('<');
        return abre > 0 ? t[..abre].Trim().Trim('"') : t;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fechar sozinho
    // ─────────────────────────────────────────────────────────────────────

    private DispatcherTimer? _relogio;
    private TimeSpan _prazo;
    private TimeSpan _passado;

    /// <summary>
    /// Fecha depois de <paramref name="prazo"/>. Para enquanto o mouse está em cima (quem está
    /// lendo não perde o aviso no meio) e enquanto a janela está escondida pela fila.
    /// </summary>
    private void ArmarContagem(TimeSpan prazo)
    {
        _prazo = prazo;
        Contagem.Visibility = Visibility.Visible;
        DicaTexto.Text = $"fecha em {(int)prazo.TotalSeconds} s";

        _relogio = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _relogio.Tick += (_, _) =>
        {
            if (IsMouseOver || !IsVisible) return;

            _passado += _relogio.Interval;
            double resta = Math.Max(0, 1 - _passado.TotalMilliseconds / _prazo.TotalMilliseconds);
            Restante.Width = new GridLength(resta, GridUnitType.Star);
            Passado.Width = new GridLength(1 - resta, GridUnitType.Star);

            if (_passado >= _prazo) Close();
        };

        Loaded += (_, _) => _relogio.Start();
        Closed += (_, _) => _relogio.Stop();
    }

    private void Fechar_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        try { DragMove(); } catch { }
    }
}
