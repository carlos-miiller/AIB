using System;
using System.IO;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// O que o prompt pede, em português e com o alvo: "Senha de root em 172.16.10.3". O prompt
    /// cru do ssh ("root@172.16.10.3's password:") vem em inglês e em fonte de código; sem esta
    /// linha a janela dizia só "o comando pede uma senha", e quem tinha três servidores abertos
    /// não sabia de qual era.
    /// </summary>
    /// <returns>O título e o rótulo do campo.</returns>
    public static (string Titulo, string Rotulo) OQuePede(string prompt, TipoDePedido tipo)
    {
        string p = prompt.Trim();
        Match m;

        if (tipo == TipoDePedido.SimNao)
        {
            m = Regex.Match(p, @"authenticity of host '([^' ]+)");
            return (m.Success ? $"Confiar no servidor {m.Groups[1].Value}?" : "Confiar neste servidor?", "");
        }

        // Senha do ssh: "user@host's password:" ou, no keyboard-interactive, "(user@host) Password:".
        m = Regex.Match(p, @"^(?:\()?([^@\s()]+)@([^'\s()]+)(?:'s|\))\s*password", RegexOptions.IgnoreCase);
        if (m.Success)
            return ($"Senha de {m.Groups[1].Value} em {m.Groups[2].Value}", $"Senha de {m.Groups[1].Value}");

        m = Regex.Match(p, @"passphrase for key '([^']+)'", RegexOptions.IgnoreCase);
        if (m.Success)
            return ($"Frase-senha da chave {Path.GetFileName(m.Groups[1].Value)}", "Frase-senha da chave");

        // git: "Username for 'https://github.com':" e "Password for 'https://carlo@github.com':".
        m = Regex.Match(p, @"^(Username|Password) for '([^']+)'", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            string alvo = Uri.TryCreate(m.Groups[2].Value, UriKind.Absolute, out var uri) ? uri.Host : m.Groups[2].Value;
            return m.Groups[1].Value.Equals("Username", StringComparison.OrdinalIgnoreCase)
                ? ($"Usuário para {alvo}", "Usuário")
                : ($"Senha para {alvo}", "Senha");
        }

        return tipo == TipoDePedido.Texto
            ? ("O comando pede um dado", "Resposta")
            : ("O comando pede uma senha", "Senha");
    }

    /// <summary>Os textos da janela para um pedido. Separado para o ensaio conferir sem abrir.</summary>
    public static (string Titulo, string Explicacao, string? Aviso, string Rotulo) Textos(PedidoDeSenha pedido)
    {
        string programa = string.IsNullOrEmpty(pedido.Solicitante)
            ? "um programa"
            : Path.GetFileName(pedido.Solicitante);

        var (titulo, rotulo) = OQuePede(pedido.Prompt, pedido.Tipo);

        string explicacao = pedido.Tipo switch
        {
            TipoDePedido.SimNao =>
                $"É a primeira conexão a este host. O {programa} mostra a impressão digital da chave; "
                + "confiar grava o host como conhecido. Recusar cancela a conexão.",
            TipoDePedido.Texto =>
                $"O {programa} precisa deste dado para continuar o comando abaixo.",
            _ =>
                $"O {programa} precisa desta senha para continuar o comando abaixo. Ela vai direto "
                + "para ele: não entra na conversa, no histórico nem no disco."
        };

        string? aviso = pedido.SolicitanteConfiavel
            ? null
            : "Quem pede não é um ssh ou git instalado no sistema. O que você digitar aqui pode "
              + "voltar para a IA como saída do comando. Na dúvida, cancele.";

        return (titulo, explicacao, aviso, rotulo);
    }

    /// <summary>Abre a janela e devolve a resposta, ou null se o usuário cancelou.</summary>
    public static RespostaDoUsuario? Perguntar(PedidoDeSenha pedido)
    {
        var janela = Montar(pedido);

        // Com o orbe na tela, logo acima dele; sem orbe, no centro. 45 é a margem de baixo da
        // casca no XAML, reservada para a sombra.
        PertoDoOrbe.Posicionar(janela, sombraEmbaixo: 45);

        // A janela nasce no meio de um comando, com a AIB talvez em segundo plano.
        janela.Loaded += (_, _) => janela.Activate();
        janela.ShowDialog();
        return janela._resposta;
    }

    /// <summary>A janela preenchida, sem abrir. Público para o ensaio.</summary>
    public static SenhaDoTerminalDialog Montar(PedidoDeSenha pedido)
    {
        var janela = new SenhaDoTerminalDialog { _pedido = pedido };
        var (titulo, explicacao, aviso, rotulo) = Textos(pedido);

        janela.TituloText.Text = titulo;
        janela.ExplicacaoText.Text = explicacao;
        janela.RotuloText.Text = rotulo;
        janela.RotuloText.Visibility = rotulo.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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

        return janela;
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

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        try { DragMove(); } catch { }
    }
}
