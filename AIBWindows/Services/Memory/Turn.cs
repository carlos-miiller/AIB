using System;
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

    /// <summary>
    /// O turno com <see cref="Segredos.Redigir(string?)"/> aplicado às falas, aos resultados e aos
    /// ARGUMENTOS das ferramentas. A redação do <see cref="ToolRegistry"/> só limpa resultados; o
    /// que o próprio agente escreveu num comando passava cru. Caso real: um webhook do Bitrix, com
    /// o token no endereço, testado pela agente num <c>Invoke-RestMethod</c>, foi para o Estado da
    /// memória e para uma lição do ato. Sem nada a redigir, devolve o próprio turno.
    /// </summary>
    public Turn SemSegredos()
    {
        var mensagens = new List<ChatMessage>(Messages.Count);
        bool mudou = false;

        foreach (var msg in Messages)
        {
            string texto = TextOf(msg);
            string limpo = Segredos.Redigir(texto);

            switch (msg)
            {
                case UserChatMessage when limpo != texto:
                    mensagens.Add(ChatMessage.CreateUserMessage(limpo));
                    mudou = true;
                    break;

                case ToolChatMessage t when limpo != texto:
                    mensagens.Add(ChatMessage.CreateToolMessage(t.ToolCallId, limpo));
                    mudou = true;
                    break;

                case AssistantChatMessage a when a.ToolCalls is { Count: > 0 }:
                {
                    bool trocou = limpo != texto;
                    var chamadas = a.ToolCalls.Select(c =>
                    {
                        string args = c.FunctionArguments?.ToString() ?? "";
                        string semSegredo = Segredos.Redigir(args);
                        if (semSegredo == args) return c;
                        trocou = true;
                        return ChatToolCall.CreateFunctionToolCall(c.Id, c.FunctionName, BinaryData.FromString(semSegredo));
                    }).ToList();

                    if (!trocou) { mensagens.Add(msg); break; }

                    var nova = new AssistantChatMessage(chamadas);
                    if (limpo.Length > 0) nova.Content.Add(ChatMessageContentPart.CreateTextPart(limpo));
                    mensagens.Add(nova);
                    mudou = true;
                    break;
                }

                case AssistantChatMessage when limpo != texto:
                    mensagens.Add(ChatMessage.CreateAssistantMessage(limpo));
                    mudou = true;
                    break;

                default:
                    mensagens.Add(msg);
                    break;
            }
        }

        return mudou ? this with { Messages = mensagens } : this;
    }

    /// <summary>Concatena as partes de texto de uma mensagem. Partes não textuais são ignoradas.</summary>
    internal static string TextOf(ChatMessage message)
    {
        if (message?.Content == null) return "";
        return string.Concat(message.Content.Where(p => p?.Text != null).Select(p => p.Text));
    }
}
