using System.Windows;
using System.Windows.Input;

namespace AIB.Views;

/// <summary>
/// Confirmação de ação destrutiva, na anatomia de §5.3 do tela-chat-v3.
/// <para>
/// Substitui o <see cref="MessageBox"/> do sistema, que ignora a paleta inteira e apresenta a
/// pergunta com a mesma cara de um aviso de rede. Aqui a pergunta é o título, a consequência
/// real vem logo abaixo, e o botão que executa é o vermelho.
/// </para>
/// <para>
/// A resposta é sempre negativa por padrão: fechar pelo X, pelo Esc ou pelo "Recusar" devolve
/// <c>false</c>. Só o clique em "Permitir" devolve <c>true</c>.
/// </para>
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary>
    /// Público só para o WPF e para os ensaios conseguirem montar a janela. O caminho de uso
    /// é <see cref="Perguntar"/>, que é quem preenche os textos e devolve a resposta.
    /// </summary>
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Abre a confirmação e devolve se o usuário permitiu.
    /// </summary>
    /// <param name="dono">Janela sobre a qual centralizar.</param>
    /// <param name="pergunta">Título, em forma de pergunta ("Excluir o arquivo original?").</param>
    /// <param name="consequencia">O que acontece de verdade se ele permitir.</param>
    /// <param name="ferramenta">Nome da ferramenta, quando houver alvo literal.</param>
    /// <param name="alvo">Caminho ou comando exato, quando houver.</param>
    /// <param name="dica">Texto curto à direita do rodapé.</param>
    /// <param name="acao">
    /// O botão diz a ação ("Descartar", "Ignorar", "Apagar tudo"). Era "Permitir/Recusar", as
    /// palavras do portão de ferramentas — que não fazem sentido para uma ação que o próprio
    /// usuário pediu.
    /// </param>
    /// <param name="temVolta">
    /// A ação dá para desfazer (a conversa ignorada volta, a vigia recomeça): pill e botão em
    /// laranja. Sem volta, vermelho.
    /// </param>
    /// <param name="pill">O que a pill diz; sem ele, "Não há desfazer" ou "Dá para voltar".</param>
    /// <param name="palavra">
    /// Uma palavra que o usuário tem de digitar para o botão acender — só no reset de fábrica,
    /// a ação mais destrutiva do app.
    /// </param>
    public static bool Perguntar(
        Window? dono,
        string pergunta,
        string consequencia,
        string? ferramenta = null,
        string? alvo = null,
        string? dica = null,
        string acao = "Confirmar",
        bool temVolta = false,
        string? pill = null,
        string? palavra = null) =>
        Montar(dono, pergunta, consequencia, ferramenta, alvo, dica, acao, temVolta, pill, palavra)
            .ShowDialog() == true;

    /// <summary>A janela preenchida, sem abrir. Público para o ensaio.</summary>
    public static ConfirmDialog Montar(
        Window? dono,
        string pergunta,
        string consequencia,
        string? ferramenta = null,
        string? alvo = null,
        string? dica = null,
        string acao = "Confirmar",
        bool temVolta = false,
        string? pill = null,
        string? palavra = null)
    {
        var janela = new ConfirmDialog
        {
            TituloText = { Text = pergunta },
            ConsequenciaText = { Text = consequencia }
        };

        if (dono != null && !ReferenceEquals(dono, janela))
            janela.Owner = dono;

        if (!string.IsNullOrWhiteSpace(alvo))
        {
            janela.AlvoFerramentaText.Text = ferramenta ?? "";
            janela.AlvoCaminhoText.Text = alvo;
            janela.AlvoBox.Visibility = Visibility.Visible;
        }

        janela.DicaText.Text = dica ?? "";
        janela.PermitirButton.Content = acao;
        janela.PillTexto.Text = pill ?? (temVolta ? "Dá para voltar" : "Não há desfazer");

        if (temVolta)
        {
            janela.Pill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WarnFill10Brush");
            janela.Pill.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "WarnBorderBrush");
            janela.PillTexto.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarnBrush");
            janela.PillIcone.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "WarnBrush");
            janela.AlvoFerramentaText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarnBrush");
            janela.PermitirButton.SetResourceReference(StyleProperty, "WarnFilledButton");
        }

        if (!string.IsNullOrWhiteSpace(palavra))
        {
            janela._palavra = palavra;
            janela.PalavraRotulo.Text = $"Para confirmar, digite {palavra}";
            janela.PalavraBox.Visibility = Visibility.Visible;
            janela.PermitirButton.IsEnabled = false;
        }

        // O foco nasce no "Cancelar" (ou na palavra). Enter sem ler o texto não pode executar a
        // ação.
        janela.Loaded += (_, _) =>
        {
            if (janela._palavra != null) janela.PalavraTexto.Focus();
            else janela.RecusarButton.Focus();
        };

        return janela;
    }

    private string? _palavra;

    /// <summary>O botão só acende com a palavra inteira, maiúsculas e minúsculas como pedidas.</summary>
    private void Palavra_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        PermitirButton.IsEnabled = _palavra == null
                                   || string.Equals(PalavraTexto.Text.Trim(), _palavra, System.StringComparison.Ordinal);

    private void Permitir_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Recusar_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
