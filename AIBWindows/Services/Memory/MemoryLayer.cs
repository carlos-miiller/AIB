using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIB.Services.Memory;

/// <summary>
/// Monta o bloco de memória que entra no prompt, já dentro da cota.
/// <para>
/// Fica DEPOIS do prompt base e ANTES das mensagens vivas. A ordem do prompt segue a
/// frequência de mudança — o que muda menos vem primeiro — porque o cache de prefixo só
/// reaproveita o começo: medido nesta máquina, turno que só acrescenta ao final custa
/// 2.672 ms, turno que mexe no começo custa 50.536 ms.
/// </para>
/// </summary>
public sealed class MemoryLayer
{
    private readonly List<Chapter> _chapters = new();

    /// <summary>Capítulos conhecidos, do mais antigo para o mais recente.</summary>
    public IReadOnlyList<Chapter> Chapters => _chapters;

    public void Add(Chapter chapter)
    {
        if (chapter != null) _chapters.Add(chapter);
    }

    public void AddRange(IEnumerable<Chapter>? chapters)
    {
        if (chapters == null) return;
        foreach (var c in chapters) Add(c);
    }

    public void Clear() => _chapters.Clear();

    /// <summary>
    /// Bloco pronto para o prompt, ou string vazia quando não há memória a mostrar.
    /// <para>
    /// A seleção é do mais RECENTE para o mais antigo — quando não cabe tudo, o que se perde
    /// é o passado distante. A renderização, porém, sai em ordem cronológica: capítulos fora
    /// de ordem fariam o modelo ler a conversa de trás para frente.
    /// </para>
    /// </summary>
    public string Render(MemoryQuota quota, TokenCounter counter)
    {
        if (quota.Chapters <= 0 || _chapters.Count == 0) return "";

        var escolhidos = new List<Chapter>();
        int gasto = 0;

        for (int i = _chapters.Count - 1; i >= 0; i--)
        {
            string bloco = _chapters[i].Render();
            int custo = counter.CountText(bloco);

            // Capítulo que sozinho não cabe na cota não bloqueia os anteriores: pula e segue.
            if (gasto + custo > quota.Chapters) continue;

            gasto += custo;
            escolhidos.Add(_chapters[i]);
        }

        if (escolhidos.Count == 0) return "";

        escolhidos.Reverse();

        var texto = new StringBuilder();
        texto.Append("## Memória da conversa\n");
        texto.Append("Trechos anteriores já compactados. Os artefatos são literais e podem ser usados como estão.\n\n");
        foreach (var capitulo in escolhidos)
            texto.Append(capitulo.Render()).Append('\n');

        return texto.ToString().TrimEnd() + "\n";
    }

    /// <summary>Índice do próximo capítulo a nascer.</summary>
    public int NextChapterIndex => _chapters.Count == 0 ? 0 : _chapters[^1].Index + 1;

    /// <summary>Índice do último turno já coberto por algum capítulo. -1 se nenhum.</summary>
    public int LastCoveredTurn => _chapters.Count == 0 ? -1 : _chapters.Max(c => c.LastTurn);
}
