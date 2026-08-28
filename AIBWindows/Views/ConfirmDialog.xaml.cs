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
    public static bool Perguntar(
        Window? dono,
        string pergunta,
        string consequencia,
        string? ferramenta = null,
        string? alvo = null,
        string? dica = null)
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

        // O foco nasce no "Recusar". Enter sem ler o texto não pode executar a ação.
        janela.Loaded += (_, _) => janela.RecusarButton.Focus();

        return janela.ShowDialog() == true;
    }

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
