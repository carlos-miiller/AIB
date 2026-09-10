using System;

namespace AIB.Services;

/// <summary>
/// O custo de contexto da conversa, em duas medidas: o que ela pesaria inteira e o que ela
/// pesa depois do sistema de capítulos e atos.
/// <para>
/// Substitui a medida anterior, que era a economia do CACHE DE PREFIXO do Ollama — quanto do
/// prompt não precisou ser reprocessado. Aquilo media o servidor, não a AIB: variava com a
/// última requisição feita, ia a zero quando outra completação despejava a fatia de KV, e não
/// dizia nada sobre o que a compactação estava fazendo. O que se quer ver aqui é o trabalho da
/// memória: quanta conversa já foi resumida e quanto isso está poupando a cada turno.
/// </para>
/// </summary>
/// <param name="Total">
/// O que a conversa custaria sem compactação: o contexto de hoje mais os turnos crus que
/// viraram capítulo, menos os resumos que os substituíram.
/// </param>
/// <param name="Contexto">O que realmente vai ao modelo agora — prompt, memória e conversa viva.</param>
/// <param name="Max">Teto de tokens do nível do usuário.</param>
/// <param name="Cru">
/// Tokens dos turnos crus já engolidos por capítulos. Vem somado dos REGISTROS em
/// chapters.jsonl, e não de um campo em memória: o campo zerava ao reabrir uma conversa do
/// histórico e a economia inteira sumia da tela.
/// </param>
/// <param name="Memoria">
/// O que a faixa narrativa pesa AGORA no prompt, medida sobre o texto renderizado. É esta que
/// entra na conta do total, e por isso é ela que precisa aparecer na tela: mostrar a soma dos
/// registros ao lado de uma economia calculada sobre outra grandeza dava três números que não
/// fechavam entre si.
/// </param>
/// <param name="MemoriaDosRegistros">
/// A soma do custo próprio dos atos e capítulos soltos. Difere de <paramref name="Memoria"/>
/// pelo cabeçalho da faixa — as duas linhas que abrem o bloco e são pagas uma vez só, existindo
/// um capítulo ou vinte. Medido: 27 tokens.
/// </param>
/// <param name="Capitulos">Quantos capítulos existem.</param>
/// <param name="Atos">Quantos atos existem.</param>
/// <param name="MedidaCompleta">
/// Falso quando algum capítulo foi gravado antes da medição existir. A conta continua sendo
/// mostrada, mas a interface avisa que ela é um piso, não o número.
/// </param>
public readonly record struct TokenReport(
    int Total,
    int Contexto,
    int Max,
    int Cru = 0,
    int Memoria = 0,
    int MemoriaDosRegistros = 0,
    int Capitulos = 0,
    int Atos = 0,
    bool MedidaCompleta = true)
{
    /// <summary>Tokens que a memória tirou do prompt. Nunca negativo.</summary>
    public int Economia => Total > Contexto ? Total - Contexto : 0;

    /// <summary>
    /// O que a faixa cobra além do custo próprio dos capítulos: o cabeçalho do bloco.
    /// <para>
    /// Negativo significa outra coisa — a cota aparou capítulos na renderização, e parte do que
    /// os registros somam não chegou ao prompt. Quem exibe precisa dizer qual dos dois é.
    /// </para>
    /// </summary>
    public int DiferencaDaFaixa => Memoria - MemoriaDosRegistros;

    /// <summary>
    /// Quanto da conversa a memória está poupando, em porcentagem. <c>null</c> enquanto nada
    /// foi compactado — e null é diferente de zero: zero seria afirmar que o sistema rodou e
    /// não economizou nada, quando a verdade é que ele ainda não teve o que fazer.
    /// </summary>
    public int? EconomiaPct =>
        Total > 0 && Total > Contexto
            ? Math.Clamp((int)Math.Round((1.0 - (double)Contexto / Total) * 100), 0, 100)
            : null;
}
