using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;

namespace AIB.Views;

/// <summary>
/// A página Cofre das Configurações: lista os aliases (sem valor), guarda novos, troca o valor e
/// apaga. Ver <see cref="Cofre"/>.
/// <para>
/// Grava na hora, como a chave do provedor, e não no "Salvar" da tela: um segredo digitado não
/// deve ficar esperando num formulário que pode ser descartado ou esquecido aberto.
/// </para>
/// </summary>
public partial class PaginaDoCofre : UserControl
{
    /// <summary>O cofre da página. Os ensaios trocam por um temporário antes de <see cref="Atualizar"/>.</summary>
    public Cofre Cofre { get; set; } = new();

    /// <summary>Nome em edição; null quando o formulário cria um alias novo.</summary>
    private string? _editando;

    /// <summary>Nome que pediu "Apagar" e espera o segundo clique.</summary>
    private string? _apagando;

    public PaginaDoCofre()
    {
        InitializeComponent();
        Loaded += (_, _) => Atualizar();
    }

    /// <summary>Redesenha a lista a partir do disco. A tela chama a cada visita.</summary>
    public void Atualizar()
    {
        Lista.Children.Clear();
        var aliases = Cofre.Listar();
        VazioText.Visibility = aliases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var a in aliases) Lista.Children.Add(Linha(a));
    }

    private Border Linha(Alias a)
    {
        var nome = new TextBlock { Text = a.Nome, FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        nome.SetResourceReference(TextBlock.ForegroundProperty, "TextStrongBrush");
        nome.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");

        var variavel = new TextBlock { Text = "$env:" + a.Variavel, FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        variavel.SetResourceReference(TextBlock.ForegroundProperty, "AccentLilacBrush");
        variavel.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");

        var trocar = Botao("Editar", (_, _) => ComecarEdicao(a));
        bool confirmando = _apagando == a.Nome;
        var apagar = Botao(confirmando ? "Confirmar: apagar" : "Apagar", (_, _) => Apagar(a.Nome));
        if (confirmando) apagar.SetResourceReference(StyleProperty, "DangerOutlineButton");

        var acoes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        acoes.Children.Add(trocar);
        acoes.Children.Add(apagar);

        var cab = new DockPanel();
        DockPanel.SetDock(acoes, Dock.Right);
        cab.Children.Add(acoes);
        var nomes = new StackPanel { Orientation = Orientation.Horizontal };
        nomes.Children.Add(nome);
        nomes.Children.Add(variavel);
        cab.Children.Add(nomes);

        var desc = new TextBlock { Text = a.Descricao, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "TextBodyBrush");

        var meta = new TextBlock { Text = Meta(a), FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        meta.SetResourceReference(TextBlock.ForegroundProperty, a.Hosts.Count == 0 ? "DangerTextBrush" : "TextMutedBrush");
        meta.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");

        var corpo = new StackPanel();
        corpo.Children.Add(cab);
        corpo.Children.Add(desc);
        corpo.Children.Add(meta);

        var borda = new Border
        {
            Child = corpo,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Tag = a.Nome
        };
        borda.SetResourceReference(Border.BorderBrushProperty, "BorderCardBrush");
        borda.SetResourceReference(Border.BackgroundProperty, "SurfaceCardBrush");
        return borda;
    }

    /// <summary>"hosts: cpaps.bitrix24.com.br · usado 3× · 19/10 17:40".</summary>
    public static string Meta(Alias a)
    {
        string hosts = a.Hosts.Count == 0 ? "qualquer endereço" : "hosts: " + string.Join(", ", a.Hosts);
        string onde = a.Shell ? "shell e habilidades" : "só habilidades";
        string uso = a.Usos == 0 ? "nunca usado" : $"usado {a.Usos}× · {a.UsadoEm:dd/MM HH:mm}";
        return $"{hosts} · {onde} · {uso}";
    }

    private static Button Botao(string texto, RoutedEventHandler clique)
    {
        var b = new Button { Content = texto, Margin = new Thickness(6, 0, 0, 0) };
        b.SetResourceReference(StyleProperty, "OutlineButton");
        b.Click += clique;
        return b;
    }

    private void Adicionar_Click(object sender, RoutedEventArgs e)
    {
        FecharFormulario();
        AbrirFormulario();
        NomeBox.Focus();
    }

    /// <summary>O formulário no lugar do botão: um segredo por vez, como as contas de e-mail.</summary>
    private void AbrirFormulario()
    {
        Formulario.Visibility = Visibility.Visible;
        AdicionarButton.Visibility = Visibility.Collapsed;
    }

    private void ComecarEdicao(Alias a)
    {
        _editando = a.Nome;
        AbrirFormulario();
        _apagando = null;
        NomeBox.Text = a.Nome;
        NomeBox.IsReadOnly = true;
        DescricaoBox.Text = a.Descricao;
        HostsBox.Text = string.Join(", ", a.Hosts);
        ShellCheck.IsChecked = a.Shell;
        ValorBox.Clear();
        FormTitulo.Text = $"Editar \"{a.Nome}\"";
        ValorDica.Text = "Em branco, fica o valor que já está guardado.";
        GuardarButton.Content = "Salvar alterações";
        Erro(null);
        ValorBox.Focus();
    }

    private void Apagar(string nome)
    {
        // Dois cliques: apagar um segredo que um comando usa não se desfaz pela tela.
        if (_apagando != nome)
        {
            _apagando = nome;
            Atualizar();
            return;
        }

        Cofre.Apagar(nome);
        _apagando = null;
        if (_editando == nome) FecharFormulario();
        Atualizar();
    }

    private void Nome_Mudou(object sender, TextChangedEventArgs e)
    {
        string n = NomeBox.Text.Trim();
        VariavelText.Text = n.Length == 0 ? "" : "vira $env:" + Cofre.Variavel(n);
    }

    /// <summary>Webhook colado: o host dele vira a sugestão de host, se o campo está vazio.</summary>
    private void Valor_Mudou(object sender, RoutedEventArgs e)
    {
        if (HostsBox.Text.Trim().Length > 0) return;
        if (Cofre.HostDe(ValorBox.Password.Trim()) is string host) HostsBox.Text = host;
    }

    private void Guardar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hosts = HostsBox.Text.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            // Valor em branco só vale na edição: num alias novo, o cofre recusa.
            Cofre.Guardar(NomeBox.Text, _editando != null && ValorBox.Password.Length == 0 ? null : ValorBox.Password,
                          DescricaoBox.Text, hosts, ShellCheck.IsChecked == true);
            FecharFormulario();
            Atualizar();
        }
        catch (ArgumentException ex)
        {
            Erro(ex.Message);
        }
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e) => FecharFormulario();

    private void FecharFormulario()
    {
        _editando = null;
        NomeBox.IsReadOnly = false;
        NomeBox.Clear();
        ValorBox.Clear();
        DescricaoBox.Clear();
        HostsBox.Clear();
        ShellCheck.IsChecked = true;
        ValorDica.Text = "Some da tela ao guardar e não volta.";
        FormTitulo.Text = "Guardar um segredo";
        GuardarButton.Content = "Guardar no cofre";
        Erro(null);
        Formulario.Visibility = Visibility.Collapsed;
        AdicionarButton.Visibility = Visibility.Visible;
    }

    private void Erro(string? texto)
    {
        ErroText.Text = texto ?? "";
        ErroText.Visibility = texto == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
