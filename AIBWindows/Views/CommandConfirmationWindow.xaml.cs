using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AIB.Services;

namespace AIB.Views;

public partial class CommandConfirmationWindow : Window
{
    public bool IsAllowed { get; private set; } = false;
    public bool AlwaysAllow { get; private set; } = false;

    // D-08 (Phase 3): hoisted from RunCommandTool — single semaphore across run_command +
    // execute_skill + materialize_skill so the ReAct loop can dispatch tools in parallel
    // without two ShowDialog calls racing for the UI thread. All three modal-bearing tools
    // route through ShowAsync below, which acquires this lock before the Dispatcher hop.
    private static readonly SemaphoreSlim _modalLock = new(1, 1);

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

        // D-08 (Phase 3): script body preview + header label flip.
        // Quando ScriptBody está populado (execute_skill / materialize_skill), o modal
        // renderiza a janela rolável com o corpo do script (cap 50KB no produtor) e o
        // header passa a indicar "SCRIPT" com o interpretador. Para run_command, ambos
        // permanecem Collapsed e o header mantém "CONFIRMAÇÃO DE COMANDO".
        if (!string.IsNullOrEmpty(ctx.ScriptBody))
        {
            ScriptBodyText.Text = ctx.ScriptBody;
            ScriptBodyLabel.Visibility = Visibility.Visible;
            ScriptBodyScroller.Visibility = Visibility.Visible;
            HeaderText.Text = $"CONFIRMAÇÃO DE SCRIPT ({ctx.Interpreter ?? "?"})";
        }
    }

    /// <summary>
    /// Helper compartilhado (D-08) que faz o hop para a UI thread, mostra o modal,
    /// e retorna a decisão do usuário. Usado por RunCommandTool, ExecuteSkillTool
    /// e MaterializeSkillTool. Reentrancy-guarded pelo <see cref="_modalLock"/>.
    ///
    /// O caller é responsável pelo No-UI guard (Application.Current == null não
    /// entra aqui; o caller emite o audit "deny_no_ui") e pela atualização do
    /// <see cref="AlwaysAllowSession"/> + audit log.
    ///
    /// Pitfall 6 (RESEARCH.md): WaitAsync().ConfigureAwait(false) evita re-entrar
    /// no SynchronizationContext da UI antes do Dispatcher.InvokeAsync.
    /// </summary>
    public static async Task<(bool Allowed, bool AlwaysAllow)> ShowAsync(CommandConfirmationContext ctx)
    {
        if (System.Windows.Application.Current == null) return (false, false);

        await _modalLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync<(bool, bool)>(() =>
            {
                var win = new CommandConfirmationWindow(ctx) { Owner = System.Windows.Application.Current.MainWindow };
                bool result = win.ShowDialog() == true;
                return (result && win.IsAllowed, win.AlwaysAllow);
            }).Task;
        }
        finally
        {
            _modalLock.Release();
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
