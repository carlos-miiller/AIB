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
