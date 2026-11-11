using System;
using System.Linq;
using System.Windows;
using AIB.Services;

namespace AIB.Views;

/// <summary>
/// O alerta de segredo na caixa de mensagem.
/// <para>
/// Pedido do usuário (20/10/2026): detectar o segredo antes de enviar e avisar na tela. Caso que
/// motivou: o webhook do Bitrix colado no chat ia ao provedor do modelo e ficava no histórico.
/// </para>
/// </summary>
public partial class ChatWindow
{
    /// <summary>O cofre que a mensagem consulta. Os ensaios trocam por um temporário.</summary>
    internal Cofre CofreDaMensagem { get; set; } = new();

    /// <summary>O trecho que o alerta aberto está mostrando.</summary>
    private SegredoAchado? _segredo;

    /// <summary>"Enviar mesmo assim": o próximo envio passa sem alerta.</summary>
    private bool _segredoLiberado;

    private bool _alertaLigado;

    /// <summary>
    /// Confere o texto antes de enviar. Troca em <paramref name="text"/> o que já está no cofre e
    /// devolve true quando o envio fica segurado pelo alerta.
    /// <para>
    /// O turno do orbe (janela escondida) e o turno nascido de um e-mail só ganham a troca: o
    /// primeiro não tem onde mostrar o alerta, e o texto do segundo é de terceiros, não algo que
    /// o usuário colou.
    /// </para>
    /// </summary>
    private bool ConferirSegredo(ref string text)
    {
        var (trocado, _, achados) = SegredoNaMensagem.Conferir(text, CofreDaMensagem);
        text = trocado;

        bool semAlerta = _segredoLiberado || _pedidoPeloOrbe || _bolhaDoTurno != null;
        _segredoLiberado = false;
        if (semAlerta || achados.Count == 0)
        {
            EsconderAlertaDeSegredo();
            return false;
        }

        MostrarAlertaDeSegredo(trocado, achados[0]);
        return true;
    }

    private void MostrarAlertaDeSegredo(string texto, SegredoAchado achado)
    {
        if (!_alertaLigado)
        {
            // Editou o trecho para fora da mensagem: o alerta não fala mais de nada que está ali.
            InputBox.TextChanged += (_, _) =>
            {
                if (_segredo != null && !InputBox.Text.Contains(_segredo.Trecho, StringComparison.Ordinal))
                    EsconderAlertaDeSegredo();
            };
            _alertaLigado = true;
        }

        _segredo = achado;
        InputBox.Text = texto;
        InputBox.CaretIndex = InputBox.Text.Length;

        AlertaTitulo.Text = $"Isto parece {achado.Tipo}";
        AlertaNome.Text = NomeLivre(achado.Nome ?? "segredo");
        AlertaHosts.Text = achado.Host ?? "";
        AlertaShell.IsChecked = true;
        AlertaDescricao.Text = achado.Nome switch
        {
            "bitrix" => "Webhook REST do Bitrix",
            "slack" => "Webhook do Slack",
            "discord" => "Webhook do Discord",
            "teams" => "Webhook do Teams",
            _ => $"Colado no chat em {DateTime.Now:dd/MM/yyyy}"
        };
        AlertaErro.Visibility = Visibility.Collapsed;
        AlertaDeSegredo.Visibility = Visibility.Visible;
    }

    /// <summary>"bitrix" já existe com outro valor: sugere "bitrix-2", sem sobrescrever calado.</summary>
    private string NomeLivre(string nome)
    {
        var usados = CofreDaMensagem.Listar().Select(a => a.Nome).ToHashSet();
        if (!usados.Contains(nome)) return nome;
        for (int i = 2; ; i++)
            if (!usados.Contains($"{nome}-{i}")) return $"{nome}-{i}";
    }

    private void EsconderAlertaDeSegredo()
    {
        _segredo = null;
        AlertaDeSegredo.Visibility = Visibility.Collapsed;
    }

    private void AlertaGuardar_Click(object sender, RoutedEventArgs e)
    {
        if (_segredo is not { } achado) return;
        try
        {
            string nome = AlertaNome.Text.Trim().ToLowerInvariant();
            var hosts = AlertaHosts.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            CofreDaMensagem.Guardar(nome, achado.Trecho, AlertaDescricao.Text, hosts, AlertaShell.IsChecked == true);

            // Pela posição, se a mensagem não mudou; senão pelo valor, onde estiver.
            string atual = InputBox.Text;
            InputBox.Text = atual.Length >= achado.Inicio + achado.Trecho.Length
                            && atual.Substring(achado.Inicio, achado.Trecho.Length) == achado.Trecho
                ? SegredoNaMensagem.TrocarPeloNome(atual, achado, nome)
                : atual.Replace(achado.Trecho, Cofre.Marca(nome), StringComparison.Ordinal);

            EsconderAlertaDeSegredo();

            // Envia de novo pelo caminho normal: se houver outro trecho, o alerta volta para ele.
            SendButton_Click(this, new RoutedEventArgs());
        }
        catch (ArgumentException ex)
        {
            AlertaErro.Text = ex.Message;
            AlertaErro.Visibility = Visibility.Visible;
        }
    }

    private void AlertaEditar_Click(object sender, RoutedEventArgs e)
    {
        var achado = _segredo;
        EsconderAlertaDeSegredo();
        InputBox.Focus();
        if (achado != null && achado.Inicio + achado.Trecho.Length <= InputBox.Text.Length)
            InputBox.Select(achado.Inicio, achado.Trecho.Length);
    }

    private void AlertaEnviar_Click(object sender, RoutedEventArgs e)
    {
        EsconderAlertaDeSegredo();
        _segredoLiberado = true;
        SendButton_Click(this, new RoutedEventArgs());
    }
}
