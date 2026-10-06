using System;

namespace AIB.Services.Mail;

/// <summary>
/// Para onde leva o botão "Abrir no &lt;provedor&gt;", e com que nome — §3.10 e §3.11.
/// <para>
/// Existe por um botão que não fazia nada. O vigia montava toda linha da caixa com a URL VAZIA,
/// e <c>AbrirNoNavegador</c> desiste em silêncio diante de endereço vazio: o botão dizia "Abrir
/// no Gmail" e não abria coisa nenhuma. O rótulo saía da conta e o endereço não saía de lugar
/// nenhum. Agora os dois saem daqui, pela mesma regra — rótulo que promete Gmail e link que leva
/// ao Gmail não podem discordar.
/// </para>
/// </summary>
public static class LinkDoEmail
{
    public enum Provedor { Desconhecido, Gmail, Outlook }

    /// <summary>
    /// O provedor da caixa.
    /// <para>
    /// O DOMÍNIO da conta decide primeiro. Sem ele, a THREAD decide: <c>X-GM-THRID</c> é extensão
    /// do Gmail e só existe lá — é o que reconhece uma conta Google Workspace com domínio próprio
    /// (<c>voce@empresa.com.br</c>), que pelo endereço pareceria "cliente desconhecido".
    /// </para>
    /// </summary>
    public static Provedor ProvedorDe(string? conta, string? threadId)
    {
        string c = (conta ?? "").ToLowerInvariant();

        if (c.Contains("outlook") || c.Contains("hotmail") || c.Contains("live")) return Provedor.Outlook;
        if (c.Contains("gmail") || c.Contains("googlemail")) return Provedor.Gmail;
        if (!string.IsNullOrWhiteSpace(threadId)) return Provedor.Gmail;

        return Provedor.Desconhecido;
    }

    /// <summary>"Abrir no Gmail", "Abrir no Outlook" ou "Abrir no cliente".</summary>
    public static string Rotulo(string? conta, string? threadId) => ProvedorDe(conta, threadId) switch
    {
        Provedor.Gmail => "Abrir no Gmail",
        Provedor.Outlook => "Abrir no Outlook",
        _ => "Abrir no cliente"
    };

    /// <summary>
    /// O endereço que abre a conversa. Vazio quando não há para onde ir — e aí o botão some.
    /// <para>
    /// GMAIL: <c>authuser</c> escolhe a CONTA, e sem ele o navegador abriria a primeira conta
    /// logada, que pode ser outra caixa. A conversa vai em <c>#all/</c> com o <c>X-GM-THRID</c>
    /// em hexadecimal, que é o identificador de conversa da interface web; <c>#all</c>, e não
    /// <c>#inbox</c>, porque a conversa pode já ter sido arquivada no Gmail. Sem thread, abre a
    /// caixa de entrada da conta.
    /// </para>
    /// <para>
    /// OUTLOOK: o IMAP não dá identificador que a interface web entenda, então vai para a caixa.
    /// </para>
    /// </summary>
    public static string Para(string? conta, string? threadId)
    {
        switch (ProvedorDe(conta, threadId))
        {
            case Provedor.Gmail:
            {
                string usuario = (conta ?? "").Trim();
                string baseDaUrl = usuario.Length == 0
                    ? "https://mail.google.com/mail/u/0/"
                    : $"https://mail.google.com/mail/u/?authuser={Uri.EscapeDataString(usuario)}";

                return ulong.TryParse(threadId, out ulong thrid)
                    ? $"{baseDaUrl}#all/{thrid:x}"
                    : $"{baseDaUrl}#inbox";
            }

            case Provedor.Outlook:
                return "https://outlook.live.com/mail/0/inbox";

            default:
                return "";
        }
    }
}
