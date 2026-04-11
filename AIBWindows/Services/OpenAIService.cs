using System;
using System.Collections.Generic;
using OpenAI.Chat;

namespace AIB.Services;

public class OpenAIService
{
    private readonly ChatClient _client;
    private readonly string _model = "gpt-4o";
    private readonly List<ChatMessage> _history = new();
    
    private const string SYSTEM_PROMPT = @"Você é um assistente AI inteligente, direto e prestativo integrado ao desktop do usuário.
Quando uma imagem de tela é fornecida, analise-a carefully para dar contexto às perguntas do usuário.
Responda sempre no mesmo idioma que o usuário usar.
Seja conciso e objetivo, mas completo quando necessário.";

    public OpenAIService()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new Exception("OPENAI_API_KEY não definida. Configure o arquivo .env");
        }
        
        _client = new ChatClient(_model, apiKey);
        _history.Add(ChatMessage.CreateSystemMessage(SYSTEM_PROMPT));
    }

    public async IAsyncEnumerable<string> SendMessageStreamAsync(string text, string? imageBase64 = null)
    {
        var contentParts = new List<ChatMessageContentPart>();
        
        if (!string.IsNullOrWhiteSpace(imageBase64))
        {
            contentParts.Add(ChatMessageContentPart.CreateImagePart(
                new Uri($"data:image/jpeg;base64,{imageBase64}"), ChatImageDetailLevel.High));
        }

        contentParts.Add(ChatMessageContentPart.CreateTextPart(text));
        
        var userMessage = ChatMessage.CreateUserMessage(contentParts);
        _history.Add(userMessage);

        string fullResponse = "";
        
        var chatUpdates = _client.CompleteChatStreamingAsync(_history);
        await foreach (var update in chatUpdates)
        {
            if (update.ContentUpdate.Count > 0)
            {
                string chunk = update.ContentUpdate[0].Text;
                fullResponse += chunk;
                yield return chunk;
            }
        }
        
        _history.Add(ChatMessage.CreateAssistantMessage(fullResponse));
    }

    public void ClearHistory()
    {
        _history.Clear();
        _history.Add(ChatMessage.CreateSystemMessage(SYSTEM_PROMPT));
    }

    public int MessageCount => _history.Count - 1;
}
