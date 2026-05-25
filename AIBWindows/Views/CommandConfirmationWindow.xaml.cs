using System;
using System.Windows;

namespace AIB.Views;

public partial class CommandConfirmationWindow : Window
{
    public bool IsAllowed { get; private set; } = false;
    public bool AlwaysAllow { get; private set; } = false;

    public CommandConfirmationWindow(string commandDescription)
    {
        InitializeComponent();
        CommandText.Text = commandDescription;
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
