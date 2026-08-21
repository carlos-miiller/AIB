using System.Threading.Tasks;
using AIB.Views;

namespace AIB.Services;

/// <summary>
/// Implementação real do portão: abre a <see cref="CommandConfirmationWindow"/>.
/// Fina de propósito — a janela já cuida do modal lock, do marshaling para o Dispatcher e do
/// guard de "sem UI". Aqui só mora a ponte, para que o registry permaneça livre de WPF.
/// </summary>
public sealed class WpfConfirmationPrompt : IConfirmationPrompt
{
    public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
        => CommandConfirmationWindow.ShowAsync(context);
}
