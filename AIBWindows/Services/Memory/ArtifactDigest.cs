using System;
using System.Collections.Generic;
using System.Linq;

namespace AIB.Services.Memory;

/// <summary>
/// Condensa artefatos de vários capítulos. Código puro, sem modelo — pela mesma razão que a
/// extração é pura: o valor de um artefato está no literal, e o único jeito de garantir que o
/// literal sobrevive é nunca deixá-lo passar por um resumidor.
/// <para>
/// A promoção aqui é por VOLUME, não por julgamento. Nada precisa decidir "isto é importante":
/// o que apareceu em mais capítulos sobe, o que apareceu uma vez fica no capítulo. É a regra
/// que faz a memória funcionar sem depender do modelo ter bom senso.
/// </para>
/// </summary>
public static class ArtifactDigest
{
    /// <summary>
    /// Quantos capítulos distintos um artefato precisa atravessar para virar fato durável.
    /// Três porque duas ocorrências ainda são coincidência plausível numa conversa longa.
    /// </summary>
    public const int FactThreshold = 3;

    /// <summary>Teto de artefatos que um ato carrega. Acima disso o ato deixaria de ser resumo.</summary>
    public const int MaxArtifactsPerAct = 12;

    /// <summary>
    /// Chave de agrupamento. Ler e gravar o MESMO arquivo é o mesmo fato — o que importa é
    /// que o caminho é recorrente, não por qual porta ele passou. Comando e recusa ficam em
    /// baldes próprios porque significam coisas diferentes.
    /// </summary>
    public static string KeyOf(Artifact artifact)
    {
        string balde = artifact.Kind switch
        {
            ArtifactKind.FileWritten or ArtifactKind.FileRead => "arquivo",
            ArtifactKind.CommandRun => "comando",
            ArtifactKind.Denied => "negado",
            _ => "outro"
        };

        return $"{balde}|{artifact.Value}";
    }

    /// <summary>
    /// Artefatos de um ato: um por chave, no máximo <paramref name="max"/>.
    /// <para>
    /// A ordem de corte coloca o que FALHOU na frente. Um caminho que o agente já leu com
    /// sucesso ele reencontra; um comando que já quebrou, se esquecido, ele repete — e repetir
    /// um comando que falha é o desperdício que a memória existe para evitar.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Artifact> Condense(IEnumerable<Artifact>? artifacts, int max = MaxArtifactsPerAct)
    {
        if (artifacts == null || max <= 0) return Array.Empty<Artifact>();

        var grupos = artifacts
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Value))
            .GroupBy(KeyOf)
            .Select(g => new
            {
                // O representante é o mais informativo do grupo: prefere o que falhou, e entre
                // iguais prefere o que traz Detail (tamanho gravado, mensagem de erro literal).
                Artefato = g.OrderByDescending(a => a.Failed)
                            .ThenByDescending(a => a.Detail != null)
                            .First(),
                Vezes = g.Count(),
                Falhou = g.Any(a => a.Failed || a.Kind == ArtifactKind.Denied)
            })
            .OrderByDescending(x => x.Falhou)
            .ThenByDescending(x => x.Vezes)
            .Take(max)
            .Select(x => x.Artefato)
            .ToList();

        return grupos;
    }

    /// <summary>
    /// Candidatos a fato durável: artefatos que atravessaram <see cref="FactThreshold"/>
    /// capítulos DISTINTOS.
    /// <para>
    /// A contagem é por capítulo, não por ocorrência: gravar o mesmo arquivo cinco vezes dentro
    /// de um único capítulo é um trabalho só, e não um padrão que valha memória permanente.
    /// </para>
    /// <para>
    /// Recusa é a exceção e promove na primeira: quando o usuário nega uma ação ele tomou uma
    /// decisão, e uma decisão não precisa se repetir para valer. Esquecê-la faz o agente
    /// perguntar de novo a mesma coisa que já ouviu não.
    /// </para>
    /// </summary>
    public static IReadOnlyList<FactCandidate> Distill(
        IEnumerable<Chapter>? chapters,
        int threshold = FactThreshold)
    {
        if (chapters == null) return Array.Empty<FactCandidate>();

        var contagem = new Dictionary<string, (Artifact Exemplo, HashSet<int> Capitulos, bool Falhou)>();

        foreach (var capitulo in chapters)
        {
            if (capitulo?.Artifacts == null) continue;

            foreach (var artefato in capitulo.Artifacts)
            {
                if (artefato == null || string.IsNullOrWhiteSpace(artefato.Value)) continue;

                string chave = KeyOf(artefato);

                if (!contagem.TryGetValue(chave, out var atual))
                    atual = (artefato, new HashSet<int>(), false);

                atual.Capitulos.Add(capitulo.Index);
                contagem[chave] = (
                    atual.Exemplo,
                    atual.Capitulos,
                    atual.Falhou || artefato.Failed);
            }
        }

        var fatos = new List<FactCandidate>();

        foreach (var (chave, dados) in contagem)
        {
            int minimo = dados.Exemplo.Kind == ArtifactKind.Denied ? 1 : threshold;
            if (dados.Capitulos.Count < minimo) continue;

            fatos.Add(new FactCandidate(chave, Describe(dados.Exemplo, dados.Capitulos.Count, dados.Falhou)));
        }

        // Ordem estável: sem ela, dois processos gravariam o mesmo conjunto em ordens
        // diferentes e o facts.md ficaria impossível de comparar entre execuções.
        return fatos.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Linha em português que vai para o facts.md — o usuário lê e corrige à mão.</summary>
    private static string Describe(Artifact exemplo, int capitulos, bool falhou)
    {
        string vezes = capitulos == 1 ? "" : $" (recorrente em {capitulos} capítulos)";

        return exemplo.Kind switch
        {
            ArtifactKind.Denied =>
                $"- o usuário NEGOU esta ação: {exemplo.Value}",

            ArtifactKind.CommandRun when falhou =>
                $"- comando que já falhou aqui: {exemplo.Value}{vezes}",

            ArtifactKind.CommandRun =>
                $"- comando usado neste ambiente: {exemplo.Value}{vezes}",

            _ =>
                $"- arquivo relevante deste trabalho: {exemplo.Value}{vezes}"
        };
    }
}

/// <summary>
/// Fato pronto para o <c>facts.md</c>.
/// </summary>
/// <param name="Key">Identidade estável do fato. É o que impede regravar o mesmo duas vezes.</param>
/// <param name="Line">Linha em markdown, como aparece no arquivo e no prompt.</param>
public sealed record FactCandidate(string Key, string Line);
