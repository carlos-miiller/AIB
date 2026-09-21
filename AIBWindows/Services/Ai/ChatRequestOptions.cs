using AIB.Services;

namespace AIB.Services.Ai;

/// <summary>Parâmetros de requisição independentes de provider.</summary>
/// <param name="Temperature">Temperatura. Default 0.1f — igual ao valor atual em produção.</param>
/// <param name="NumCtx">
/// Janela de contexto pedida ao Ollama (ignorada pelo OpenRouter, onde a janela é a do modelo).
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
/// <param name="KeepAliveSeconds">
/// keep_alive do Ollama; -1 trava na memória. null = o provider manda o <see cref="KeepAliveAtual"/>
/// — omitir o campo faria o Ollama voltar aos 5 minutos dele e desfazer a escolha da tela.
/// </param>
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
/// <param name="Raciocinio">
/// Só OpenRouter: "off", "low", "medium", "high" ou "model" (não mandar). Nulo segue
/// <paramref name="Think"/> — <c>false</c> ali desliga aqui também.
/// </param>
public sealed record ChatRequestOptions(
    float Temperature = 0.1f,
    int NumCtx = 32768,
    int? KeepAliveSeconds = null,
    bool? Think = null,
    int? NumPredict = null,
    string? Raciocinio = null)
{
    /// <summary>
    /// A janela de contexto em vigor, do provedor da conversa. Configurada pelo
    /// <see cref="SettingsService"/> ao carregar e ao salvar, como as pastas sem confirmação.
    /// <para>
    /// Era a constante 32768 deste record, lida de onde fosse preciso: os orçamentos por nível e
    /// o teto da poda. Com a janela virando configuração por provedor, uma constante aqui faria
    /// a tela dizer 65536 e o programa continuar contando 32768.
    /// </para>
    /// </summary>
    public static int JanelaAtual { get; set; } = PerfilDeProvedor.JanelaPadrao;

    /// <summary>
    /// A janela do PERFIL DO OLLAMA — o <c>num_ctx</c> das chamadas de serviço (resumo, título,
    /// triagem). Configurada pelo <see cref="SettingsService"/> junto com <see cref="JanelaAtual"/>.
    /// <para>
    /// Existe porque o Ollama RECARREGA o modelo quando o <c>num_ctx</c> muda. O compactador e o
    /// título mandavam os 32.768 do padrão deste record: com a janela configurada em 16k ou 64k,
    /// cada capítulo e cada título descarregava o modelo e o carregava de novo — e o turno
    /// seguinte, de volta na janela da tela, recarregava outra vez.
    /// </para>
    /// <para>
    /// Não é a <see cref="JanelaAtual"/> porque aquela é a do provedor da CONVERSA. Com a conversa
    /// no OpenRouter e a triagem no Ollama, ela pode ser 128k ou mais, e pedir isso ao Ollama numa
    /// máquina sem GPU é paginar a RAM. Com a conversa no Ollama as duas são o mesmo número.
    /// </para>
    /// </summary>
    public static int JanelaDoOllama { get; set; } = PerfilDeProvedor.JanelaPadrao;

    /// <summary>
    /// O keep-alive em vigor, em segundos (-1 = sempre carregado), do perfil do Ollama.
    /// Configurado pelo <see cref="SettingsService"/> ao carregar e ao salvar, como a janela.
    /// <para>
    /// Um lugar só porque eram quatro: o turno lia o da tela, o aquecimento forçava -1, e o
    /// provider trocava o nulo do compactador, do título e da triagem por -1. Escolher "5
    /// minutos" valia até a próxima compactação, que voltava a travar o modelo na memória.
    /// Agora quem não diz nada recebe este, e o aquecimento também.
    /// </para>
    /// </summary>
    public static int KeepAliveAtual { get; set; } = SempreCarregado;

    /// <summary>keep_alive do Ollama que trava o modelo na memória.</summary>
    public const int SempreCarregado = -1;

    /// <summary>"1m", "5m", "30m" ou "-1" em segundos. Desconhecido trava na memória, como sempre foi.</summary>
    public static int SegundosDeKeepAlive(string? valor) => valor switch
    {
        "1m" => 60,
        "5m" => 300,
        "30m" => 1800,
        _ => SempreCarregado
    };

    /// <summary>As opções de fábrica, com a janela e o keep-alive em vigor.</summary>
    public static ChatRequestOptions Default => new(NumCtx: JanelaAtual, KeepAliveSeconds: KeepAliveAtual);

    /// <summary>
    /// As opções das chamadas de SERVIÇO — resumo de capítulo e de ato, título, triagem —: sem
    /// raciocínio por padrão, com teto de resposta, e com a janela do Ollama em vigor.
    /// <para>
    /// É PROPRIEDADE lida a cada chamada, e não campo estático: um <c>static readonly</c> com
    /// estas opções congelaria a janela do momento em que a classe foi carregada, e mudar a
    /// janela na tela voltaria a recarregar o modelo a cada resumo. O keep-alive vai nulo e o
    /// provider aplica o <see cref="KeepAliveAtual"/> no instante do envio, pelo mesmo motivo.
    /// </para>
    /// </summary>
    public static ChatRequestOptions DeServico(int? numPredict, bool? think = false, float temperature = 0.0f) =>
        new(Temperature: temperature, NumCtx: JanelaDoOllama, Think: think, NumPredict: numPredict);
}
