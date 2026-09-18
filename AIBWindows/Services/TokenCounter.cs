using System.Collections.Generic;
using Microsoft.ML.Tokenizers;
using OpenAI.Chat;

namespace AIB.Services;

/// <summary>
/// Contagem de tokens do histórico. Instância única por processo — criar o tokenizer
/// é caro, e por isso o App cria um só e o repassa a quem conta.
/// Os métodos Count* são seguros para chamadas concorrentes (o tokenizer não tem estado);
/// quem enumera a lista viva ainda precisa segurar o lock do dono dela.
/// </summary>
public sealed class TokenCounter
{
    private readonly Tokenizer _tokenizer;

    public TokenCounter()
    {
        _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
    }

    public int CountText(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return _tokenizer.CountTokens(text);
    }

    public int CountMessages(IEnumerable<ChatMessage> messages)
    {
        if (messages == null) return 0;

        int tokens = 0;
        foreach (var msg in messages)
        {
            if (msg == null) continue;

            if (msg.Content != null)
            {
                foreach (var part in msg.Content)
                {
                    if (part != null && part.Text != null)
                        tokens += _tokenizer.CountTokens(part.Text);
                }
            }

            if (msg is AssistantChatMessage acm && acm.ToolCalls != null && acm.ToolCalls.Count > 0)
            {
                try
                {
                    string toolCallsJson = System.Text.Json.JsonSerializer.Serialize(acm.ToolCalls);
                    tokens += _tokenizer.CountTokens(toolCallsJson);
                }
                catch { }
            }
        }
        return tokens;
    }
}
