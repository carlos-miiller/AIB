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
public sealed record Act(
    int Index,
    string AtUtc,
    int FirstChapter,
    int LastChapter,
    int FirstTurn,
    int LastTurn,
    string Summary,
    IReadOnlyList<Artifact> Artifacts)
{
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
