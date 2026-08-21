using System.Collections.Generic;
using OpenAI.Chat;

namespace AIB.Services.Agent;

/// <summary>
/// Dono de uma lista de mensagens. Toda leitura devolve CÓPIA; toda escrita é atômica.
/// É a costura que permite ao AgentLoop rodar tanto sobre a conversa viva quanto sobre
/// uma lista descartável — ele nunca segura um List&lt;ChatMessage&gt;.
/// Implementações: ConversationService (viva, sincronizada) e EphemeralMessageStore (aquecimento).
/// </summary>
public interface IMessageStore
{
    /// <summary>Cópia coerente do histórico. O que sai daqui nunca é mutado por terceiros.</summary>
    IReadOnlyList<ChatMessage> Snapshot();

    /// <summary>Anexa a mensagem assistant que carrega as tool_calls da iteração.</summary>
    void AppendAssistantToolCalls(IReadOnlyList<ChatToolCall> calls);

    /// <summary>Anexa o ToolChatMessage correspondente a uma tool_call já anexada.</summary>
    void AppendToolResult(string toolCallId, string result);

    /// <summary>Anexa a resposta final em texto (blocos &lt;think&gt; preservados de propósito).</summary>
    void AppendAssistantText(string text);

    /// <summary>Poda por orçamento de tokens do nível. Nunca remove a mensagem de sistema.</summary>
    void Trim(int userLevel);

    /// <summary>Contagem atual de tokens do histórico.</summary>
    int CountTokens();

    /// <summary>Publica o contador para a UI. No store efêmero é no-op.</summary>
    void NotifyTokenCount(int userLevel, int? cachedTokens = null);
}
