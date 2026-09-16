using System.Collections.Generic;
using System.Text;

namespace AIB.Services.Memory;

/// <summary>
/// Um bloco de turnos destilado. Duas metades com naturezas opostas:
/// <list type="bullet">
/// <item><description><see cref="Summary"/> — narrativo, gerado pelo modelo, perde detalhe por design.</description></item>
/// <item><description><see cref="Artifacts"/> — literal, extraído por código, nunca passou pelo modelo.</description></item>
/// </list>
/// </summary>
/// <param name="Index">Posição do capítulo na sessão, começando em 0.</param>
/// <param name="AtUtc">Quando foi fechado.</param>
/// <param name="FirstTurn">Índice do primeiro turno resumido.</param>
/// <param name="LastTurn">Índice do último turno resumido.</param>
/// <param name="Summary">Resumo narrativo.</param>
/// <param name="Artifacts">Fatos literais dos turnos resumidos.</param>
/// <param name="TokensDosTurnos">
/// O que os turnos CRUS custavam no prompt, medido antes de saírem dele. É a metade esquerda
/// da conta da economia.
/// </param>
/// <param name="TokensDoCapitulo">
/// O que este capítulo custa no prompt — <see cref="Render"/> medido. É a metade direita.
/// </param>
/// <param name="Pendencias">
/// Pontas soltas do trecho — ver <see cref="Pendencia"/>. NÃO entram no <see cref="Render"/>: o
/// bloco de memória as mostra numa seção só, no fim, já sem as que um trecho posterior resolveu.
/// Nulo nos capítulos gravados antes de existirem.
/// </param>
public sealed record Chapter(
    int Index,
    string AtUtc,
    int FirstTurn,
    int LastTurn,
    string Summary,
    IReadOnlyList<Artifact> Artifacts,
    int TokensDosTurnos = 0,
    int TokensDoCapitulo = 0,
    IReadOnlyList<Pendencia>? Pendencias = null)
{
    /// <summary>
    /// Quanto este capítulo tirou do prompt. Nunca negativo: um resumo que saiu maior que o
    /// material não economizou -30 tokens, economizou zero.
    /// <para>
    /// Os dois números moram no REGISTRO, e não num campo somado à mão em memória. O campo
    /// antigo zerava ao reabrir uma conversa do histórico, e a economia inteira da sessão
    /// sumia da tela sem nenhum sinal. Persistido em chapters.jsonl, o número sobrevive a
    /// fechar o app — e é conferível depois, que é o ponto de medir.
    /// </para>
    /// <para>
    /// Capítulos gravados antes desta mudança trazem zero nos dois campos: a economia deles é
    /// desconhecida, não é zero, e a interface diz isso em vez de inventar um número.
    /// </para>
    /// </summary>
    public int Economia => TokensDosTurnos > TokensDoCapitulo
        ? TokensDosTurnos - TokensDoCapitulo
        : 0;

    /// <summary>Se este capítulo sabe quanto custou. Falso nos gravados antes da medição.</summary>
    public bool TemMedida => TokensDosTurnos > 0;

    /// <summary>Bloco pronto para o prompt.</summary>
    public string Render()
    {
        var texto = new StringBuilder();
        texto.Append("### Capítulo ").Append(Index + 1);

        int turnos = LastTurn - FirstTurn + 1;
        texto.Append(turnos == 1 ? " (1 turno)" : $" ({turnos} turnos)").Append('\n');

        if (!string.IsNullOrWhiteSpace(Summary))
            texto.Append(Summary.Trim()).Append('\n');

        if (Artifacts.Count > 0)
        {
            texto.Append("Artefatos:\n");
            foreach (var artefato in Artifacts)
                texto.Append(artefato.Render()).Append('\n');
        }

        return texto.ToString();
    }

    /// <summary>
    /// Bloco aparado para caber em <paramref name="maxTokens"/>. Só o resumo encolhe; os
    /// artefatos ficam inteiros. Devolve vazio quando nem eles cabem sozinhos.
    /// </summary>
    public string Render(int maxTokens, TokenCounter counter) =>
        MemoryRender.Fit(Render(), Summary, maxTokens, counter);
}
