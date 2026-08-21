namespace AIB.Services.Ai;

/// <summary>Parâmetros de requisição independentes de provider.</summary>
/// <param name="Temperature">Temperatura. Default 0.1f — igual ao valor atual em produção.</param>
/// <param name="NumCtx">Janela de contexto pedida ao Ollama (ignorada pelo OpenAI).</param>
/// <param name="KeepAliveSeconds">keep_alive do Ollama; -1 trava na VRAM. null = não enviar.</param>
public sealed record ChatRequestOptions(
    float Temperature = 0.1f,
    int NumCtx = 16384,
    int? KeepAliveSeconds = null)
{
    public static ChatRequestOptions Default { get; } = new();
}
