using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIB.Services;

// WinForms entra junto com o WPF em net8.0-windows e traz homonimos.
using UserControl = System.Windows.Controls.UserControl;

namespace AIB.Views;

/// <summary>
/// O item de e-mail — o MESMO nas duas telas: a pilha do orbe (shadow-assistant §4.8) e a aba
/// de e-mails do painel (tela-chat-v3 §6.2).
/// <para>
/// PROVISÓRIO: §6.6 E3 avisa que este layout vai mudar. Ele mora num controle próprio
/// justamente para que a mudança seja em um lugar só.
/// </para>
/// <para>
/// O clique mora AQUI, e não em cada tela que hospeda a lista. As duas specs pedem a mesma
/// coisa — abrir a mensagem no navegador — e deixar isso com o hospedeiro significaria duas
/// cópias da mesma regra, incluindo o cuidado de não derrubar a janela quando o navegador
/// falha.
/// </para>
/// </summary>
public partial class MailListItem : UserControl
{
    public MailListItem()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raio dos cantos: 12 no orbe, 14 no painel, para casar com os outros itens de lá.
    /// </summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius), typeof(CornerRadius), typeof(MailListItem),
            new PropertyMetadata(new CornerRadius(12)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>
    /// Liga o hover lilás do painel. Fica desligado no orbe de propósito: lá a pilha é leitura
    /// de passagem sobre o desktop, e um realce a cada item sob o cursor viraria ruído.
    /// </summary>
    public static readonly DependencyProperty RealceLilasProperty =
        DependencyProperty.Register(
            nameof(RealceLilas), typeof(bool), typeof(MailListItem),
            new PropertyMetadata(false));

    public bool RealceLilas
    {
        get => (bool)GetValue(RealceLilasProperty);
        set => SetValue(RealceLilasProperty, value);
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MailSummary email) return;

        AbrirNoNavegador(email.Url);
    }

    /// <summary>
    /// Abre a mensagem. URL vazia simplesmente não faz nada — as caixas do usuário são webmail,
    /// e um e-mail sem endereço de thread não tem para onde levar.
    /// </summary>
    /// <remarks>
    /// Público para os ensaios: sem uma janela na tela não há clique de mouse para simular, e o
    /// que precisa ser garantido é que URL vazia não vira chamada ao shell.
    /// </remarks>
    public static bool AbrirNoNavegador(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            // Abrir o navegador é conveniência. Falhar aqui não pode derrubar a janela, que
            // continua sendo a única coisa entre o usuário e a lista que ele acabou de ler.
            Console.WriteLine($"[EMAIL] Não abriu '{url}': {ex.Message}");
            return false;
        }
    }
}
