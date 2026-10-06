using System;
using System.Collections.Generic;
using System.Linq;
using OpenAI.Chat;

namespace AIB.Services.Memory;

/// <summary>
/// Remonta um turno do <c>raw.jsonl</c> em mensagens que o modelo aceita. Função pura.
/// <para>
/// Existe porque a reabertura trazia só as FALAS: o turno voltava sem nenhuma chamada de
/// ferramenta e sem nenhum resultado. A conversa reaberta perdia o que tinha feito, e a
/// compactação que rodasse depois resumia turnos sem execução — capítulos sem um artefato
/// sequer, numa conversa que tinha lido e gravado arquivos.
/// </para>
/// <para>
/// O cuidado é o PAR. Um <c>tool_calls</c> sem o resultado de cada id, ou um resultado sem a
/// chamada, faz a requisição seguinte ser recusada pela API. O registro pode trazer os dois
/// partidos — um turno encerrado com ferramenta pendente, um arquivo editado à mão —, e o que
/// não fecha par fica de fora em vez de quebrar a conversa inteira.
/// </para>
/// </summary>
public static class TurnoDoRegistro
{
    /// <summary>As mensagens do turno, e o texto do que ficou de fora por não fechar par.</summary>
    /// <param name="registro">O turno como está no disco.</param>
    public static (List<ChatMessage> Mensagens, List<string> Descartados) Remontar(TurnRecord registro)
    {
        var mensagens = new List<ChatMessage>();
        var descartados = new List<string>();
        var lista = registro.Messages ?? Array.Empty<MessageRecord>();

        for (int i = 0; i < lista.Count; i++)
        {
            var atual = lista[i];
            string texto = atual.Text ?? "";

            switch (atual.Role)
            {
                case "user":
                    if (!string.IsNullOrWhiteSpace(texto))
                        mensagens.Add(ChatMessage.CreateUserMessage(texto));
                    break;

                case "assistant" when atual.ToolCalls is { Count: > 0 }:
                {
                    // Os resultados vêm logo depois da chamada, todos juntos — inclusive os de
                    // ferramentas que rodaram em paralelo.
                    var resultados = new List<MessageRecord>();
                    while (i + 1 < lista.Count && lista[i + 1].Role == "tool")
                        resultados.Add(lista[++i]);

                    var respondidos = resultados
                        .Where(r => !string.IsNullOrEmpty(r.ToolCallId))
                        .GroupBy(r => r.ToolCallId!, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                    var chamadas = atual.ToolCalls
                        .Where(c => !string.IsNullOrEmpty(c.Id) && respondidos.ContainsKey(c.Id))
                        .ToList();

                    foreach (var r in resultados)
                        if (r.ToolCallId == null || !chamadas.Any(c => c.Id == r.ToolCallId))
                            descartados.Add(r.Text ?? "");

                    foreach (var c in atual.ToolCalls.Where(c => !chamadas.Contains(c)))
                        descartados.Add(c.Arguments ?? "");

                    if (chamadas.Count == 0)
                    {
                        // Nenhuma chamada fechou par: fica a fala, que é conversa.
                        if (!string.IsNullOrWhiteSpace(texto))
                            mensagens.Add(ChatMessage.CreateAssistantMessage(texto));
                        break;
                    }

                    var comChamadas = new AssistantChatMessage(chamadas
                        .Select(c => ChatToolCall.CreateFunctionToolCall(
                            c.Id, c.Name, BinaryData.FromString(string.IsNullOrEmpty(c.Arguments) ? "{}" : c.Arguments)))
                        .ToList());

                    if (!string.IsNullOrWhiteSpace(texto))
                        comChamadas.Content.Add(ChatMessageContentPart.CreateTextPart(texto));

                    mensagens.Add(comChamadas);

                    // Na ordem das chamadas, um resultado para cada.
                    foreach (var c in chamadas)
                        mensagens.Add(ChatMessage.CreateToolMessage(c.Id, respondidos[c.Id].Text ?? ""));

                    break;
                }

                case "assistant":
                    if (!string.IsNullOrWhiteSpace(texto))
                        mensagens.Add(ChatMessage.CreateAssistantMessage(texto));
                    break;

                case "tool":
                    // Resultado sem a chamada logo antes: órfão.
                    descartados.Add(texto);
                    break;

                // system e desconhecidos não pertencem ao turno.
            }
        }

        return (mensagens, descartados);
    }
}
