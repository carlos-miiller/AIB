namespace AIB.Services.Ai;

/// <summary>Parâmetros de requisição independentes de provider.</summary>
/// <param name="Temperature">Temperatura. Default 0.1f — igual ao valor atual em produção.</param>
/// <param name="NumCtx">
/// Janela de contexto pedida ao Ollama (ignorada pelo OpenAI).
/// <para>
/// 32.768, e não os 16.384 anteriores. Medido nesta máquina, sem GPU: o modelo suporta 262.144;
/// o KV cache custa ~0,65 GB a cada 16k (16k → 3,6 GB, 32k → 4,2 GB, 64k → 5,6 GB) contra
/// 15,7 GB de RAM total e 2,6 GB livres com o modelo carregado. 32k cabe; 64k começa a paginar,
/// e KV em swap é o pior caso possível — ele é tocado a cada token gerado.
/// </para>
/// <para>
/// ATENÇÃO ao que isto NÃO é: janela maior não deixa nada mais rápido. O prefill nesta máquina
/// roda a 30 tok/s medidos — 13.317 tokens levaram 445,8s. Encher os 32k custaria dezoito
/// minutos de leitura antes da primeira palavra. A janela é folga para o turno não morrer no
/// meio, não convite para usá-la: quem devolve o prompt ao tamanho pequeno é a compactação, no
/// fim do turno.
/// </para>
/// </param>
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
    int NumCtx = 32768,
    int? KeepAliveSeconds = null,
    bool? Think = null,
    int? NumPredict = null)
{
    public static ChatRequestOptions Default { get; } = new();
}
