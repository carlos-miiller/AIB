using System.Collections.Generic;
using System.Text;

namespace AIB.Services.Memory;

/// <summary>
/// Um bloco de capítulos destilado — o nível 2 da hierarquia.
/// <para>
/// Mesma natureza dupla do <see cref="Chapter"/>: <see cref="Summary"/> é narrativo e perde
/// detalhe por design; <see cref="Artifacts"/> é literal e nunca passou pelo modelo. A
/// diferença é a escala: um ato cobre vários capítulos, então os artefatos vêm condensados —
/// repetidos viram um só, e o que falhou tem prioridade sobre o que deu certo.
/// </para>
/// </summary>
/// <param name="Index">Posição do ato na sessão, começando em 0.</param>
/// <param name="AtUtc">Quando foi fechado.</param>
/// <param name="FirstChapter">Índice do primeiro capítulo coberto.</param>
/// <param name="LastChapter">Índice do último capítulo coberto.</param>
/// <param name="FirstTurn">Índice do primeiro turno coberto, herdado dos capítulos.</param>
/// <param name="LastTurn">Índice do último turno coberto.</param>
/// <param name="Summary">Resumo narrativo dos capítulos.</param>
/// <param name="Artifacts">Artefatos condensados dos capítulos cobertos.</param>
/// <param name="TokensDosTurnos">
/// O cru lá do fundo: a soma do que os turnos custavam, herdada dos capítulos cobertos. É
/// contra ELE que a economia do ato se mede, e não contra os capítulos — quem promoveu já
/// tinha economizado uma vez.
/// </param>
/// <param name="TokensDosCapitulos">
/// O que os capítulos cobertos custavam no prompt. Serve para separar as duas economias: a do
/// resumo dos turnos e a da promoção a ato.
/// </param>
/// <param name="TokensDoAto">O que este ato custa no prompt — <see cref="Render"/> medido.</param>
public sealed record Act(
    int Index,
    string AtUtc,
    int FirstChapter,
    int LastChapter,
    int FirstTurn,
    int LastTurn,
    string Summary,
    IReadOnlyList<Artifact> Artifacts,
    int TokensDosTurnos = 0,
    int TokensDosCapitulos = 0,
    int TokensDoAto = 0)
{
    /// <summary>Quanto o ato tira do prompt em relação ao CRU. Nunca negativo.</summary>
    public int Economia => TokensDosTurnos > TokensDoAto
        ? TokensDosTurnos - TokensDoAto
        : 0;

    /// <summary>
    /// O que a PROMOÇÃO em si rendeu — capítulos que saíram menos o ato que entrou.
    /// <para>
    /// Separado da economia total de propósito. Promover cedo demais custa um resumo de resumo
    /// por quase nada, e este é o número que mostra se os quatro capítulos por ato estão no
    /// ponto certo. Sem ele, a economia do ato herdaria o crédito do trabalho dos capítulos.
    /// </para>
    /// </summary>
    public int EconomiaDaPromocao => TokensDosCapitulos > TokensDoAto
        ? TokensDosCapitulos - TokensDoAto
        : 0;

    /// <summary>Se este ato sabe quanto custou. Falso nos gravados antes da medição.</summary>
    public bool TemMedida => TokensDosTurnos > 0;

    /// <summary>Bloco pronto para o prompt.</summary>
    public string Render()
    {
        var texto = new StringBuilder();

        // Capítulos numerados a partir de 1 aqui, como no Chapter.Render: o usuário lê "Ato 1,
        // capítulos 1 a 4", não índices de vetor.
        texto.Append("### Ato ").Append(Index + 1);

        int capitulos = LastChapter - FirstChapter + 1;
        texto.Append(capitulos == 1
            ? $" (capítulo {FirstChapter + 1})"
            : $" (capítulos {FirstChapter + 1}–{LastChapter + 1})").Append('\n');

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
