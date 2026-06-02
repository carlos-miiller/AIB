using System;
using System.Windows;
using AIB.Services;

namespace AIB.Views;

public partial class CommandConfirmationWindow : Window
{
    public bool IsAllowed { get; private set; } = false;
    public bool AlwaysAllow { get; private set; } = false;

    public CommandConfirmationWindow(CommandConfirmationContext ctx)
    {
        InitializeComponent();
        CommandText.Text = ctx.Command;
        ToolText.Text = $"Tool: {ctx.Tool}";
        LevelText.Text = $"Nível: {ctx.Level}/9";
        CwdText.Text = ctx.Cwd;

        // D-04 (Phase 3): renderiza o banner AVISO amber quando o floor list pré-modal
        // identifica que o comando será refutado após a aprovação. O texto e a visibilidade
        // são controlados pelo ctx; o gradiente WarningAccent é reusado do XAML.
        if (ctx.DenylistHit && !string.IsNullOrEmpty(ctx.DenylistReason))
        {
            DenylistText.Text = $"AVISO: este comando será recusado pelo floor list após aprovação (nível atual = {ctx.Level}). Razão: {ctx.DenylistReason}";
            DenylistBanner.Visibility = Visibility.Visible;
        }
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        IsAllowed = true;
        AlwaysAllow = AlwaysAllowCheckBox.IsChecked ?? false;
        DialogResult = true;
        Close();
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        IsAllowed = false;
        DialogResult = false;
        Close();
    }
}
