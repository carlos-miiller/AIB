using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace AIB.Views;

/// <summary>
/// A confirmação do portão no modo orbe: o mesmo <see cref="ConfirmCardView"/> da conversa,
/// numa janela pequena logo acima do orbe.
/// <para>
/// Antes, pedir confirmação abria a janela principal inteira para mostrar um card — o usuário
/// estava no orbe, pediu uma coisa rápida, e a conversa tomava a tela.
/// </para>
/// <para>
/// Fechar a janela sem responder (Alt+F4, Esc) é recusa: o portão é fail-closed.
/// </para>
/// </summary>
public partial class ConfirmacaoDoOrbeWindow : Window
{
    /// <summary>Margem transparente abaixo do card (ver o XAML).</summary>
    private const double SombraEmbaixo = 45;

    private readonly ConfirmCardView _card;

    public ConfirmacaoDoOrbeWindow(ConfirmCardView card)
    {
        InitializeComponent();
        _card = card;
        _card.Margin = new Thickness(0);
        Lugar.Content = _card;

        PertoDoOrbe.Posicionar(this, SombraEmbaixo);
        Loaded += (_, _) => Activate();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _card.Descartar();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        try { DragMove(); } catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Sem resposta, é recusa. Com resposta, Descartar não muda nada (TrySetResult).
        _card.Descartar();
        base.OnClosing(e);
    }
}
