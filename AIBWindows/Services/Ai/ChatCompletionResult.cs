namespace AIB.Services.Ai;

/// <summary>Resultado de uma chamada não-streaming (warmup e AskStateless).</summary>
public sealed record ChatCompletionResult(string Text, int? PromptEvalCount, int? EvalCount);
