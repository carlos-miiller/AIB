namespace AIB.Services.Ai;

/// <summary>Resultado de uma chamada não-streaming (warmup e AskStateless).</summary>
/// <param name="CustoUsd">O que a chamada custou, quando o provedor relata (OpenRouter).</param>
public sealed record ChatCompletionResult(string Text, int? PromptEvalCount, int? EvalCount, decimal? CustoUsd = null);
