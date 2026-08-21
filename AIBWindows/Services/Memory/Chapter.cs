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
public sealed record Chapter(
    int Index,
    string AtUtc,
    int FirstTurn,
    int LastTurn,
    string Summary,
    IReadOnlyList<Artifact> Artifacts)
{
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
}
