namespace AIB.Services.Ai;

/// <summary>Parâmetros de requisição independentes de provider.</summary>
/// <param name="Temperature">Temperatura. Default 0.1f — igual ao valor atual em produção.</param>
/// <param name="NumCtx">Janela de contexto pedida ao Ollama (ignorada pelo OpenAI).</param>
/// <param name="KeepAliveSeconds">keep_alive do Ollama; -1 trava na VRAM. null = não enviar.</param>
/// <param name="Think">
/// Raciocínio do modelo. <c>false</c> desliga o bloco de pensamento em modelos que o têm;
/// null = não enviar o campo e deixar o modelo no padrão dele.
/// <para>
/// Existe por uma medição, não por gosto. Resumir cinco turnos no qwen3.5:4b custava 286,6s,
/// dos quais 226,7s eram 1.738 tokens de raciocínio para um resumo de 117 — texto que o
/// <c>ThinkBlockStripper</c> jogava fora em seguida. Com o raciocínio desligado, 14,7s.
/// </para>
/// </param>
/// <param name="NumPredict">
/// Teto de tokens gerados. null = sem teto. É a rede contra um modelo que ignore o limite de
/// palavras do prompt: sem ela, um único resumo desgovernado consome a janela inteira.
/// </param>
public sealed record ChatRequestOptions(
    float Temperature = 0.1f,
    int NumCtx = 16384,
    int? KeepAliveSeconds = null,
    bool? Think = null,
    int? NumPredict = null)
{
    public static ChatRequestOptions Default { get; } = new();
}
