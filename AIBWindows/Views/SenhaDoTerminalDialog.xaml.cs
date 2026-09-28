using System.IO;
using System.Windows;
using System.Windows.Input;
using AIB.Services.Terminal;

namespace AIB.Views;

/// <summary>
/// A janela onde o usuário responde ao prompt de um programa do shell — senha do ssh,
/// fingerprint de host novo, usuário do git. Ver <see cref="AskpassServidor"/>.
/// <para>
/// Fechar pelo Esc ou pelo "Cancelar" devolve null: o ssh recebe senha recusada.
/// </para>
/// </summary>
public partial class SenhaDoTerminalDialog : Window
{
    private PedidoDeSenha? _pedido;
    private RespostaDoUsuario? _resposta;

    public SenhaDoTerminalDialog()
    {
        InitializeComponent();
    }

    /// <summary>Os textos da janela para um pedido. Separado para o ensaio conferir sem abrir.</summary>
    public static (string Titulo, string Explicacao, string? Aviso) Textos(PedidoDeSenha pedido)
    {
        string programa = string.IsNullOrEmpty(pedido.Solicitante)
            ? "um programa"
            : Path.GetFileName(pedido.Solicitante);

        var (titulo, explicacao) = pedido.Tipo switch
        {
            TipoDePedido.SimNao => (
                "Confiar neste servidor?",
                $"É a primeira conexão a este host. O {programa} mostra a impressão digital da chave; "
                + "confiar grava o host como conhecido. Recusar cancela a conexão."),
            TipoDePedido.Texto => (
                "O comando pede um dado",
                $"O {programa} está pedindo o texto abaixo para continuar o comando."),
            _ => (
                "O comando pede uma senha",
                $"O {programa} está pedindo a senha para continuar o comando. Ela vai direto para "
                + "ele: não entra na conversa, no histórico nem no disco.")
        };

        string? aviso = pedido.SolicitanteConfiavel
            ? null
            : "Quem pede não é um ssh ou git instalado no sistema. O que você digitar aqui pode "
              + "voltar para a IA como saída do comando. Na dúvida, cancele.";

        return (titulo, explicacao, aviso);
    }

    /// <summary>Abre a janela e devolve a resposta, ou null se o usuário cancelou.</summary>
    public static RespostaDoUsuario? Perguntar(PedidoDeSenha pedido)
    {
        var janela = new SenhaDoTerminalDialog { _pedido = pedido };
        var (titulo, explicacao, aviso) = Textos(pedido);

        janela.TituloText.Text = titulo;
        janela.ExplicacaoText.Text = explicacao;
        janela.PromptText.Text = pedido.Prompt.Trim();
        janela.OrigemText.Text =
            $"{pedido.Solicitante ?? "(programa desconhecido)"}\n$ {pedido.Comando}";
        janela.DicaText.Text = pedido.SolicitanteConfiavel ? "a IA não vê o que você digita" : "";

        if (aviso is not null)
        {
            janela.AvisoText.Text = aviso;
            janela.AvisoBox.Visibility = Visibility.Visible;
        }

        switch (pedido.Tipo)
        {
            case TipoDePedido.SimNao:
                janela.EnviarButton.Content = "Confiar";
                janela.CancelarButton.Content = "Recusar";
                // Sem campo: o foco fica no "Recusar". Enter sem ler não confia em host.
                janela.Loaded += (_, _) => janela.CancelarButton.Focus();
                break;

            case TipoDePedido.Texto:
                janela.TextoBox.Visibility = Visibility.Visible;
                janela.EnviarButton.IsDefault = true;
                janela.Loaded += (_, _) => janela.TextoBox.Focus();
                break;

            default:
                janela.SenhaBox.Visibility = Visibility.Visible;
                janela.LembrarCheck.Visibility = pedido.SolicitanteConfiavel
                    ? Visibility.Visible : Visibility.Collapsed;
                janela.EnviarButton.IsDefault = true;
                janela.Loaded += (_, _) => janela.SenhaBox.Focus();
                break;
        }

        // A janela nasce no meio de um comando, com a AIB talvez em segundo plano.
        janela.Loaded += (_, _) => janela.Activate();
        janela.ShowDialog();
        return janela._resposta;
    }

    private void Enviar_Click(object sender, RoutedEventArgs e)
    {
        string texto = _pedido?.Tipo switch
        {
            TipoDePedido.SimNao => "yes",
            TipoDePedido.Texto => TextoBox.Text,
            _ => SenhaBox.Password
        };

        _resposta = new RespostaDoUsuario(texto, LembrarCheck.IsChecked == true);
        SenhaBox.Clear();
        Close();
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e)
    {
        // "no" explícito na fingerprint: o ssh encerra na hora, sem perguntar de novo.
        _resposta = _pedido?.Tipo == TipoDePedido.SimNao ? new RespostaDoUsuario("no", false) : null;
        SenhaBox.Clear();
        Close();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        try { DragMove(); } catch { }
    }
}
