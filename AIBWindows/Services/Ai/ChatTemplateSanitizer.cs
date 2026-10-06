using System;
using System.Collections.Generic;

namespace AIB.Services.Ai;

/// <summary>
/// Remove tokens de chat-template que vazam crus de alguns modelos via Ollama
/// (especialmente gemma4 e variantes que usam estilo Harmony/channels). Esses
/// tokens NUNCA devem aparecer para o usuário final.
///
/// Inclui também tags &lt;think&gt;/&lt;/think&gt; ÓRFÃS (sem par correspondente). O
/// <see cref="ChannelSplitter"/> já cuida dos pares válidos; este filtro pega o caso em
/// que o modelo emite uma tag solta como artefato (típico do gemma4 quando "muda de
/// canal" sem fechar adequadamente).
/// </summary>
public static class ChatTemplateSanitizer
{
    private static readonly string[] TokenList = new[]
    {
        // Gemma4 / Harmony-style channels
        "<channel|>", "<|channel|>",
        "<message|>", "<|message|>",
        "<|return|>",
        // Qwen / generic ChatML
        "<|im_start|>", "<|im_end|>",
        // Llama 3+
        "<|begin_of_text|>", "<|end_of_text|>",
        "<|start_header_id|>", "<|end_header_id|>",
        "<|eot_id|>",
        // GPT
        "<|endoftext|>",
        // Role markers genéricos
        "<|user|>", "<|assistant|>", "<|system|>",
        "<user|>", "<assistant|>", "<system|>",
        // Think tags órfãs (pares válidos são tratados pela state machine antes)
        "<think>", "</think>",
    };

    /// <summary>Lista de tokens filtrados, na ordem em que são aplicados.</summary>
    public static IReadOnlyList<string> Tokens => TokenList;

    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var token in TokenList)
        {
            text = text.Replace(token, "", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
