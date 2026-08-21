using System.Collections.Generic;
using System.Linq;
using OpenAI.Chat;

namespace AIB.Services.Memory;

/// <summary>
/// Um turno COMPLETO: começa numa mensagem de usuário e termina antes da próxima, incluindo
/// todos os pares <c>assistant(tool_calls)</c> + <c>tool(result)</c> do meio.
/// <para>
/// É a unidade indivisível de compactação. O provider rejeita <c>tool_calls</c> sem o
/// <c>tool(result)</c> de id correspondente — foi o bug do RemoveRange do aquecimento. Cortar
/// por mensagem solta reintroduz a mesma classe de falha; cortar por turno a torna impossível
/// por construção.
/// </para>
/// </summary>
/// <param name="Index">Posição do turno na sessão, começando em 0.</param>
/// <param name="Messages">As mensagens do turno, na ordem original.</param>
public sealed record Turn(int Index, IReadOnlyList<ChatMessage> Messages)
{
    /// <summary>Texto da mensagem que abriu o turno. Vazio se o turno não tiver texto.</summary>
    public string UserText =>
        Messages.Count > 0 && Messages[0] is UserChatMessage user
            ? TextOf(user)
            : "";

    /// <summary>Texto da última fala do assistente no turno, sem os blocos de raciocínio.</summary>
    public string AssistantText
    {
        get
        {
            var ultima = Messages
                .OfType<AssistantChatMessage>()
                .LastOrDefault(m => m.ToolCalls == null || m.ToolCalls.Count == 0);
            return ultima == null ? "" : ThinkBlockStripper.Strip(TextOf(ultima));
        }
    }

    /// <summary>Concatena as partes de texto de uma mensagem. Partes não textuais são ignoradas.</summary>
    internal static string TextOf(ChatMessage message)
    {
        if (message?.Content == null) return "";
        return string.Concat(message.Content.Where(p => p?.Text != null).Select(p => p.Text));
    }
}
