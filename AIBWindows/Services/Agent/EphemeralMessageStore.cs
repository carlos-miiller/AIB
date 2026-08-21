using System.Collections.Generic;
using OpenAI.Chat;

namespace AIB.Services.Agent;

/// <summary>
/// Store descartável do aquecimento: começa como cópia do histórico real, recebe a
/// mensagem fantasma de heartbeat e é jogado fora inteiro. Nunca toca o histórico vivo —
/// é isso que elimina o RemoveRange que apagava mensagens de uma requisição concorrente.
/// </summary>
public sealed class EphemeralMessageStore : IMessageStore
{
    private readonly List<ChatMessage> _messages;
    private readonly TokenCounter _tokenCounter;

    public EphemeralMessageStore(IReadOnlyList<ChatMessage> seed, TokenCounter tokenCounter)
    {
        _messages = seed == null ? new List<ChatMessage>() : new List<ChatMessage>(seed);
        _tokenCounter = tokenCounter;
    }

    /// <summary>Anexa a mensagem de usuário do heartbeat.</summary>
    public void AppendUserMessage(string text) => _messages.Add(ChatMessage.CreateUserMessage(text));

    public IReadOnlyList<ChatMessage> Snapshot() => _messages.ToArray();

    public void AppendAssistantToolCalls(IReadOnlyList<ChatToolCall> calls)
        => _messages.Add(ChatMessage.CreateAssistantMessage(calls));

    public void AppendToolResult(string toolCallId, string result)
        => _messages.Add(ChatMessage.CreateToolMessage(toolCallId, result));

    public void AppendAssistantText(string text)
        => _messages.Add(ChatMessage.CreateAssistantMessage(text));

    /// <summary>No-op: o store inteiro é descartado ao fim do aquecimento.</summary>
    public void Trim(int userLevel) { }

    public int CountTokens() => _tokenCounter?.CountMessages(_messages) ?? 0;

    /// <summary>No-op: o aquecimento publica suas próprias métricas.</summary>
    public void NotifyTokenCount(int userLevel, int? cachedTokens = null) { }
}
