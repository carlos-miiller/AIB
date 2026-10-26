namespace AIB.Services.Ai;

/// <summary>Resultado de uma chamada não-streaming (warmup e AskStateless).</summary>
/// <param name="CustoUsd">O que a chamada custou, quando o provedor relata (OpenRouter).</param>
/// <param name="Cortada">O modelo parou no teto de tokens (finish "length"): o texto está pela metade.</param>
public sealed record ChatCompletionResult(string Text, int? PromptEvalCount, int? EvalCount, decimal? CustoUsd = null,
    bool Cortada = false);
