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
public readonly record struct TokenReport(int Total, int Contexto, int Max)
{
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
