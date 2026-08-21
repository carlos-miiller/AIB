using System;
using System.Collections.Generic;
using System.Text;
using OpenAI.Chat;

namespace AIB.Services.Agent;

/// <summary>
/// Estima quantos tokens de prompt o KV cache do provider consegue reaproveitar.
/// <para>
/// O cache de prefixo reaproveita o maior prefixo comum entre a requisição anterior e a atual.
/// Nós montamos as duas, então esse número é DERIVÁVEL — não precisa ser reportado pela API.
/// Isso importa porque o Ollama simplesmente não reporta: <c>prompt_eval_count</c> devolve o
/// tamanho do prompt e não se move com o cache (medido com 3485 tokens: 204s de prefill frio e
/// 343ms cacheado, contador em 3485 nos dois casos).
/// </para>
/// <para>
/// A contagem sai do mesmo <see cref="TokenCounter"/> usado para o total da UI. O tokenizador é
/// o o200k, que não é o do modelo local — mas como numerador e denominador vêm da MESMA régua, a
/// razão exibida fica correta. Foi exatamente o erro da fórmula antiga, que subtraía
/// <c>prompt_eval_count</c> (régua do modelo) de um total em o200k e produzia número negativo.
/// </para>
/// <para>
/// É previsão, não medição: o provider pode ter descartado o cache por fora (modelo recarregado,
/// requisição com num_ctx diferente, outro cliente na mesma instância). Por isso
/// <see cref="ConfirmOrDiscard"/> confere contra o custo real de prefill antes de a UI acreditar.
/// </para>
/// </summary>
public sealed class PromptPrefixTracker
{
    private readonly TokenCounter _tokenCounter;

    /// <summary>Assinatura de conteúdo de cada mensagem do prompt anterior, em ordem.</summary>
    private readonly List<string> _lastSignatures = new();

    /// <summary>Tokens de cada mensagem do prompt anterior, no mesmo índice.</summary>
    private readonly List<int> _lastTokens = new();

    /// <summary>
    /// Maior custo por token de prompt já visto na sessão — aproxima o prefill FRIO desta
    /// máquina. Relativo de propósito: um limiar fixo em ms não sobrevive à diferença entre
    /// uma CPU e uma GPU. Na máquina medida, frio deu 58,6 ms/token e quente 0,098 ms/token.
    /// </summary>
    private double _coldMillisPerToken;

    /// <summary>Abaixo desta fração do custo frio, consideramos que o prefixo estava quente.</summary>
    private const double WarmFraction = 0.25;

    public PromptPrefixTracker(TokenCounter tokenCounter)
    {
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
    }

    /// <summary>
    /// Compara <paramref name="messages"/> com o prompt anterior, devolve os tokens do prefixo
    /// comum e passa a lembrar deste prompt. Zero na primeira chamada e sempre que o prefixo
    /// quebra (uma poda no meio do histórico invalida do ponto cortado em diante).
    /// </summary>
    public int RecordAndGetReusableTokens(IReadOnlyList<ChatMessage> messages)
    {
        var signatures = new List<string>(messages?.Count ?? 0);
        var tokens = new List<int>(messages?.Count ?? 0);

        if (messages != null)
        {
            foreach (var msg in messages)
            {
                signatures.Add(SignatureOf(msg));
                tokens.Add(_tokenCounter.CountMessages(new[] { msg }));
            }
        }

        int reusable = 0;
        int shared = Math.Min(signatures.Count, _lastSignatures.Count);
        for (int i = 0; i < shared; i++)
        {
            if (!string.Equals(signatures[i], _lastSignatures[i], StringComparison.Ordinal)) break;
            reusable += tokens[i];
        }

        _lastSignatures.Clear();
        _lastSignatures.AddRange(signatures);
        _lastTokens.Clear();
        _lastTokens.AddRange(tokens);

        return reusable;
    }

    /// <summary>
    /// Confere a previsão contra o custo real do prefill. Devolve <paramref name="predicted"/>
    /// quando o prefill saiu barato o bastante para ter havido reaproveitamento, e 0 quando o
    /// provider claramente reavaliou o prompt inteiro. Sem dados de duração, confia na previsão.
    /// </summary>
    public int ConfirmOrDiscard(int predicted, int? promptEvalCount, double? promptEvalMillis)
    {
        // A calibração vem ANTES de qualquer saída antecipada: o turno mais informativo é
        // justamente o primeiro, que tem predicted = 0 por não haver prompt anterior — e é
        // exatamente ele que mede o custo de prefill frio desta máquina.
        bool temMedida = promptEvalCount.HasValue
                         && promptEvalMillis.HasValue
                         && promptEvalCount.Value > 0;

        double rate = 0;
        bool primeiraMedida = _coldMillisPerToken <= 0;

        if (temMedida)
        {
            rate = promptEvalMillis!.Value / promptEvalCount!.Value;
            if (rate > _coldMillisPerToken) _coldMillisPerToken = rate;
        }

        if (predicted <= 0) return 0;
        if (!temMedida) return predicted;

        // Sem uma referência de frio anterior não há com o que comparar. Confia na previsão:
        // errar para menos aqui esconderia economia real logo no primeiro turno reaproveitável.
        if (primeiraMedida) return predicted;

        return rate <= _coldMillisPerToken * WarmFraction ? predicted : 0;
    }

    /// <summary>
    /// Assinatura estável do conteúdo de uma mensagem. Precisa ser por CONTEÚDO: o histórico é
    /// copiado a cada Snapshot e podado pela frente, então comparar referências não serve.
    /// </summary>
    private static string SignatureOf(ChatMessage? msg)
    {
        if (msg == null) return "null";

        var sb = new StringBuilder();
        sb.Append(msg.GetType().Name).Append('');

        if (msg.Content != null)
        {
            foreach (var part in msg.Content)
            {
                if (part?.Text != null) sb.Append(part.Text);
                sb.Append('');
            }
        }

        if (msg is AssistantChatMessage acm && acm.ToolCalls != null)
        {
            foreach (var call in acm.ToolCalls)
            {
                if (call == null) continue;
                sb.Append(call.Id).Append('')
                  .Append(call.FunctionName).Append('')
                  .Append(call.FunctionArguments?.ToString()).Append('');
            }
        }

        if (msg is ToolChatMessage tcm) sb.Append('').Append(tcm.ToolCallId);

        return sb.ToString();
    }
}
