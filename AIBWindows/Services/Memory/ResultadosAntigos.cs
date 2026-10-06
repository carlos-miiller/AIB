using System;
using System.Collections.Generic;
using OpenAI.Chat;

namespace AIB.Services.Memory;

/// <summary>
/// Esconde, na cópia que vai ao modelo, o conteúdo dos resultados de ferramenta de turnos antigos.
/// <para>
/// Medido pela JetBrains ("The Complexity Trap", SWE-bench Verified, 2025): trocar observações
/// antigas por um marcador cortou ~50% do custo e empatou com o resumo por LLM em acerto — sem
/// chamada nenhuma ao modelo. Num modelo local, é prefill que deixa de ser pago a cada volta: um
/// arquivo de quatro mil caracteres lido dez turnos atrás ia inteiro em toda requisição.
/// </para>
/// <para>
/// Só a CÓPIA muda. O histórico, o raw.jsonl e a tela continuam com tudo; o pedido do usuário, as
/// chamadas e as falas do agente ficam — só o corpo do resultado vira uma linha que diz o que era
/// e que dá para pedir de novo.
/// </para>
/// <para>
/// A fronteira anda em DEGRAUS de <see cref="Passo"/> turnos, e não a cada turno. Esconder muda o
/// meio do prompt, e tudo depois do ponto mudado perde o cache do provedor; andando de um em um,
/// isso aconteceria em todo turno.
/// </para>
/// </summary>
public static class ResultadosAntigos
{
    /// <summary>De quantos em quantos turnos a fronteira anda.</summary>
    public const int Passo = 4;

    /// <summary>Resultado menor que isto fica: o marcador não economizaria nada.</summary>
    public const int Minimo = 300;

    /// <param name="manterTurnos">Turnos mais recentes que ficam intactos. Zero ou menos: nada muda.</param>
    public static IReadOnlyList<ChatMessage> Esconder(IReadOnlyList<ChatMessage> historico, int manterTurnos)
    {
        if (manterTurnos <= 0 || historico.Count == 0) return historico;

        int turnos = 0;
        foreach (var m in historico) if (m is UserChatMessage) turnos++;

        int esconder = (turnos - manterTurnos) / Passo * Passo;
        if (esconder <= 0) return historico;

        var nomes = new Dictionary<string, string>(StringComparer.Ordinal);
        var copia = new List<ChatMessage>(historico.Count);
        int turno = -1;
        bool mudou = false;

        foreach (var m in historico)
        {
            if (m is UserChatMessage) turno++;

            if (m is AssistantChatMessage a && a.ToolCalls is { Count: > 0 })
                foreach (var c in a.ToolCalls)
                    if (c?.Id != null) nomes[c.Id] = c.FunctionName ?? "ferramenta";

            if (turno >= 0 && turno < esconder && m is ToolChatMessage t && t.ToolCallId != null)
            {
                string texto = Turn.TextOf(t);
                if (texto.Length >= Minimo)
                {
                    nomes.TryGetValue(t.ToolCallId, out string? nome);
                    bool falhou = ArtifactExtractor.Falhou(texto);
                    string marcador = $"[resultado antigo de {nome ?? "ferramenta"} omitido para poupar contexto: "
                                      + $"{texto.Length} caracteres{(falhou ? ", era uma FALHA: " + ArtifactExtractor.PrimeiraLinhaDoErro(texto) : "")}. "
                                      + "Repita a chamada se precisar do conteúdo.]";
                    copia.Add(ChatMessage.CreateToolMessage(t.ToolCallId, marcador));
                    mudou = true;
                    continue;
                }
            }

            copia.Add(m);
        }

        return mudou ? copia : historico;
    }
}
