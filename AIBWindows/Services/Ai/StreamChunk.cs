namespace AIB.Services.Ai;

/// <summary>Canal do texto emitido pelo provider. Classificar é trabalho do provider.</summary>
public enum TextChannel
{
    /// <summary>Resposta final — vai para o usuário.</summary>
    Final = 0,

    /// <summary>Raciocínio interno (&lt;think&gt;, canal analysis/commentary) — só console.</summary>
    Reasoning = 1
}

/// <summary>Motivo pelo qual o stream do provider terminou.</summary>
public enum StreamFinishReason
{
    /// <summary>O modelo terminou a resposta em texto.</summary>
    Stop = 0,

    /// <summary>O modelo pediu uma ou mais ferramentas.</summary>
    ToolCalls = 1,

    /// <summary>O stream acabou por limite de tokens do provider.</summary>
    Length = 2,

    /// <summary>O stream acabou sem motivo declarado.</summary>
    Unknown = 3
}

/// <summary>
/// Unidade única do stream de um provider. A pergunta "isso é texto ou tool call?"
/// é respondida AQUI, uma vez por provider — nunca no orquestrador.
///
/// Contrato de emissão, obrigatório para todo provider:
/// 1. <see cref="Done"/> é emitido EXATAMENTE uma vez, como último chunk, em toda
///    enumeração que não lançar exceção.
/// 2. <see cref="TextDelta"/> com <see cref="TextChannel.Final"/> carrega texto já
///    passado pelo sanitizador de chat template. O orquestrador nunca remove nada.
/// 3. <see cref="TextDelta"/> com <see cref="TextChannel.Reasoning"/> é cru (não sanitizado)
///    — é diagnóstico de console.
/// 4. Um provider que emitiu algum <see cref="ToolCallDelta"/> deve reportar
///    <see cref="Done"/> com <see cref="StreamFinishReason.ToolCalls"/>.
/// 5. <see cref="TextDelta"/> com texto vazio não pode ser emitido.
/// </summary>
public abstract record StreamChunk
{
    private StreamChunk() { }

    /// <summary>Pedaço de texto já classificado por canal.</summary>
    public sealed record TextDelta(string Text, TextChannel Channel) : StreamChunk;

    /// <summary>
    /// Pedaço de uma tool call. <paramref name="CallKey"/> é a chave de agregação:
    /// deltas com a mesma CallKey pertencem à MESMA chamada e são concatenados na ordem
    /// de chegada; CallKeys distintas são chamadas distintas.
    /// A chave é opaca para o orquestrador e única por chamada lógica dentro de uma
    /// enumeração de <see cref="IChatProvider.StreamAsync"/>.
    /// </summary>
    public sealed record ToolCallDelta(
        string CallKey,
        string? ToolCallId,
        string? FunctionName,
        string? ArgumentsJsonFragment) : StreamChunk;

    /// <summary>
    /// Contadores de uso reportados pelo provider (podem chegar várias vezes).
    /// <para>
    /// <paramref name="PromptEvalCount"/> e <paramref name="EvalCount"/> são os números crus da
    /// API, úteis para log. NÃO derive cache deles no orquestrador: o significado muda por
    /// provider. No Ollama, <c>prompt_eval_count</c> é o tamanho do prompt e não varia com o
    /// cache — medido: 3485 tanto num prefill frio de 204s quanto num cacheado de 343ms.
    /// </para>
    /// <para>
    /// <paramref name="CachedTokens"/> é quantos tokens de prompt o provider afirma ter
    /// reaproveitado. Só o provider sabe calcular isso. Null = "não sei", que é o caso do
    /// Ollama; nunca zero para significar desconhecido.
    /// </para>
    /// </summary>
    /// <param name="PromptEvalMillis">
    /// Tempo gasto avaliando o prompt. É o único sinal honesto de cache no Ollama: para o mesmo
    /// prompt de 3485 tokens, 204.062ms no frio contra 343ms no quente — 595× de separação.
    /// </param>
    /// <param name="CustoUsd">
    /// O que a chamada custou, em US$, como o provedor cobrou. Só o OpenRouter relata; nulo é
    /// "não cobrado ou não relatado", e o Ollama é sempre nulo.
    /// </param>
    public sealed record Usage(
        int? PromptEvalCount,
        int? EvalCount,
        int? CachedTokens = null,
        double? PromptEvalMillis = null,
        decimal? CustoUsd = null) : StreamChunk;

    /// <summary>
    /// Último chunk de um stream. Emitido exatamente uma vez, sempre.
    /// <paramref name="RawAssistantText"/> é o texto cru que o modelo emitiu neste stream,
    /// com os blocos &lt;think&gt; intactos: é o que o orquestrador anexa ao histórico para
    /// o modelo reler o próprio raciocínio. Nulo quando o provider não tem texto cru.
    /// </summary>
    public sealed record Done(
        StreamFinishReason Reason,
        string? RawFinishReason,
        string? RawAssistantText = null) : StreamChunk;
}
