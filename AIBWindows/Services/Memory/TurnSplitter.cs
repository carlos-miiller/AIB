using System.Collections.Generic;
using OpenAI.Chat;

namespace AIB.Services.Memory;

/// <summary>
/// Parte um histórico em turnos completos. Função pura: mesma entrada, mesma saída, sem IO.
/// </summary>
public static class TurnSplitter
{
    /// <summary>
    /// Devolve os turnos do histórico, em ordem.
    /// <para>
    /// A mensagem de sistema não pertence a turno nenhum — ela é o prefixo fixo, e incluí-la
    /// num turno faria o primeiro capítulo carregar a alma do personagem inteira.
    /// Mensagens antes do primeiro <c>user</c> são descartadas: sem abertura não há turno.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Turn> Split(IReadOnlyList<ChatMessage>? history)
    {
        var turnos = new List<Turn>();
        if (history == null || history.Count == 0) return turnos;

        List<ChatMessage>? atual = null;

        foreach (var msg in history)
        {
            if (msg is null or SystemChatMessage) continue;

            if (msg is UserChatMessage)
            {
                if (atual != null) turnos.Add(new Turn(turnos.Count, atual));
                atual = new List<ChatMessage> { msg };
                continue;
            }

            atual?.Add(msg);
        }

        if (atual != null) turnos.Add(new Turn(turnos.Count, atual));
        return turnos;
    }

    /// <summary>
    /// Um turno está FECHADO quando o assistente já deu a palavra final: a última mensagem é
    /// um <c>assistant</c> sem tool_calls pendentes.
    /// <para>
    /// Gravar um turno ainda aberto deixaria em disco um <c>tool_calls</c> sem resultado — o
    /// exato par partido que a unidade-turno existe para evitar.
    /// </para>
    /// </summary>
    public static bool IsClosed(Turn turn)
    {
        if (turn.Messages.Count == 0) return false;
        var ultima = turn.Messages[^1];
        return ultima is AssistantChatMessage a && (a.ToolCalls == null || a.ToolCalls.Count == 0);
    }
}
