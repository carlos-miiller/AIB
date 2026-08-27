using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIB.Services.Memory;

/// <summary>
/// Monta o bloco de memória que entra no prompt, já dentro da cota.
/// <para>
/// Fica DEPOIS do prompt base e ANTES das mensagens vivas. Dentro dele a ordem segue a
/// frequência de mudança — fatos, atos, capítulos — porque o cache de prefixo só reaproveita o
/// começo: medido nesta máquina, turno que só acrescenta ao final custa 2.672 ms, turno que
/// mexe no começo custa 50.536 ms. Fato muda a cada promoção (raro), ato a cada N capítulos,
/// capítulo a cada compactação. Inverter essa ordem invalidaria o prefixo inteiro a cada
/// capítulo novo.
/// </para>
/// </summary>
public sealed class MemoryLayer
{
    private readonly List<Chapter> _chapters = new();
    private readonly List<Act> _acts = new();
    private IReadOnlyList<string> _facts = Array.Empty<string>();

    /// <summary>Capítulos conhecidos, do mais antigo para o mais recente.</summary>
    public IReadOnlyList<Chapter> Chapters => _chapters;

    /// <summary>Atos conhecidos, do mais antigo para o mais recente.</summary>
    public IReadOnlyList<Act> Acts => _acts;

    /// <summary>Linhas de fato carregadas do facts.md.</summary>
    public IReadOnlyList<string> Facts => _facts;

    public void Add(Chapter chapter)
    {
        if (chapter != null) _chapters.Add(chapter);
    }

    public void AddRange(IEnumerable<Chapter>? chapters)
    {
        if (chapters == null) return;
        foreach (var c in chapters) Add(c);
    }

    public void Add(Act act)
    {
        if (act != null) _acts.Add(act);
    }

    /// <summary>
    /// Troca as linhas de fato. Vêm do disco a cada renderização: o facts.md é do usuário e
    /// ele pode tê-lo editado no meio da conversa.
    /// </summary>
    public void SetFacts(IReadOnlyList<string>? facts) => _facts = facts ?? Array.Empty<string>();

    /// <summary>
    /// Esquece capítulos e atos. Os FATOS ficam: eles são a única faixa que atravessa sessões,
    /// e apagá-los aqui transformaria "durável" em "dura até o próximo reset".
    /// </summary>
    public void Clear()
    {
        _chapters.Clear();
        _acts.Clear();
    }

    /// <summary>
    /// Bloco pronto para o prompt, ou string vazia quando não há memória a mostrar.
    /// </summary>
    public string Render(MemoryQuota quota, TokenCounter counter)
    {
        string fatos = FactStore.Render(_facts, quota, counter);
        string atos = RenderActs(quota, counter);
        string capitulos = RenderChapters(quota, counter);

        if (fatos.Length == 0 && atos.Length == 0 && capitulos.Length == 0) return "";

        var texto = new StringBuilder();

        if (fatos.Length > 0) texto.Append(fatos).Append('\n');

        if (atos.Length > 0 || capitulos.Length > 0)
        {
            texto.Append("## Memória da conversa\n");
            texto.Append("Trechos anteriores já compactados. Os artefatos são literais e podem ser usados como estão.\n\n");
            texto.Append(atos).Append(capitulos);
        }

        return texto.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Atos, do mais recente para o mais antigo até esgotar a cota, renderizados em ordem
    /// cronológica. Cota própria: sobra de ato não vira espaço de capítulo, senão o tamanho do
    /// bloco de capítulos mudaria toda vez que um ato nascesse — e mudar o meio do prefixo
    /// custa o mesmo que mudar o começo.
    /// </summary>
    private string RenderActs(MemoryQuota quota, TokenCounter counter)
    {
        if (quota.Acts <= 0 || _acts.Count == 0) return "";

        return Juntar(Fit(
            _acts,
            a => a.Render(),
            (a, cota) => a.Render(cota, counter),
            quota.Acts,
            counter));
    }

    /// <summary>
    /// Capítulos ainda NÃO cobertos por um ato. Renderizar os dois seria contar a mesma coisa
    /// duas vezes, gastando a cota para repetir o que o ato já disse.
    /// </summary>
    private string RenderChapters(MemoryQuota quota, TokenCounter counter)
    {
        if (quota.Chapters <= 0) return "";

        var soltos = _chapters.Where(c => c.Index > LastCoveredChapter).ToList();
        if (soltos.Count == 0) return "";

        return Juntar(Fit(
            soltos,
            c => c.Render(),
            (c, cota) => c.Render(cota, counter),
            quota.Chapters,
            counter));
    }

    private static string Juntar(List<string> pedacos)
    {
        if (pedacos.Count == 0) return "";

        var texto = new StringBuilder();
        foreach (var pedaco in pedacos) texto.Append(pedaco).Append('\n');
        return texto.ToString();
    }

    /// <summary>
    /// Escolhe do mais RECENTE para o mais antigo até a cota acabar, e devolve em ordem
    /// cronológica.
    /// <para>
    /// Quando não cabe tudo, o que se perde é o passado distante. A saída, porém, é cronológica:
    /// trechos fora de ordem fariam o modelo ler a conversa de trás para frente. Um item que
    /// sozinho não cabe é pulado em vez de encerrar a seleção — ele não deve bloquear os
    /// anteriores, que talvez caibam.
    /// </para>
    /// <para>
    /// Quando NADA cabe, o mais recente entra aparado em vez de a faixa sair vazia. Isto não é
    /// zelo: um ato maior que a própria cota era pulado aqui, e os capítulos que ele resumiu já
    /// não são renderizados — <see cref="RenderChapters"/> filtra por
    /// <see cref="LastCoveredChapter"/>. O resultado era o ato engolir quatro capítulos e não
    /// aparecer, um buraco silencioso. Medido: um ato real de 330 tokens contra a cota de 321
    /// da alma da Ayano no nível 1. Meio resumo vale mais que nenhum.
    /// </para>
    /// </summary>
    /// <param name="renderAparado">Render do item limitado a N tokens. Encolhe só a narrativa.</param>
    private static List<string> Fit<T>(
        IReadOnlyList<T> itens,
        Func<T, string> render,
        Func<T, int, string> renderAparado,
        int cota,
        TokenCounter counter)
    {
        var escolhidos = new List<string>();
        int gasto = 0;

        for (int i = itens.Count - 1; i >= 0; i--)
        {
            string texto = render(itens[i]);
            int custo = counter.CountText(texto);
            if (gasto + custo > cota) continue;

            gasto += custo;
            escolhidos.Add(texto);
        }

        if (escolhidos.Count > 0)
        {
            escolhidos.Reverse();
            return escolhidos;
        }

        if (itens.Count == 0) return escolhidos;

        string aparado = renderAparado(itens[^1], cota);
        if (aparado.Length > 0) escolhidos.Add(aparado);
        return escolhidos;
    }

    /// <summary>Índice do próximo capítulo a nascer.</summary>
    public int NextChapterIndex => _chapters.Count == 0 ? 0 : _chapters[^1].Index + 1;

    /// <summary>Índice do próximo ato a nascer.</summary>
    public int NextActIndex => _acts.Count == 0 ? 0 : _acts[^1].Index + 1;

    /// <summary>Índice do último turno já coberto por algum capítulo. -1 se nenhum.</summary>
    public int LastCoveredTurn => _chapters.Count == 0 ? -1 : _chapters.Max(c => c.LastTurn);

    /// <summary>Índice do último capítulo já absorvido por um ato. -1 se nenhum.</summary>
    public int LastCoveredChapter => _acts.Count == 0 ? -1 : _acts.Max(a => a.LastChapter);

    /// <summary>Capítulos que ainda não viraram ato, em ordem.</summary>
    public IReadOnlyList<Chapter> UncoveredChapters =>
        _chapters.Where(c => c.Index > LastCoveredChapter).ToList();
}
