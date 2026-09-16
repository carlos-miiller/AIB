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
        string narrativa = RenderNarrative(quota, counter);

        if (fatos.Length == 0 && narrativa.Length == 0) return "";

        var texto = new StringBuilder();

        if (fatos.Length > 0) texto.Append(fatos).Append('\n');
        texto.Append(narrativa);

        return texto.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// So a faixa NARRATIVA: atos e capitulos soltos, com o cabecalho deles. Vazia quando nao
    /// existe nenhum dos dois.
    /// <para>
    /// Separada do <see cref="Render"/> porque e ela, e so ela, que entrou no lugar de
    /// conversa crua. Fatos atravessam sessoes e anexos sao escolha do usuario: nenhum dos
    /// dois substituiu turno nenhum. O contador de tokens compara esta faixa com os turnos que
    /// ela engoliu, e medir o bloco inteiro creditaria a compactacao por texto que ela nunca
    /// resumiu.
    /// </para>
    /// </summary>
    public string RenderNarrative(MemoryQuota quota, TokenCounter counter)
    {
        string atos = RenderActs(quota, counter);
        string capitulos = RenderChapters(quota, counter);

        if (atos.Length == 0 && capitulos.Length == 0) return "";

        var texto = new StringBuilder();
        texto.Append("## Memória da conversa\n");
        texto.Append("Trechos anteriores já compactados. Os artefatos são literais e podem ser usados como estão.\n\n");
        texto.Append(atos).Append(capitulos);

        // No FIM, e uma seção só: é o que um "continue" retoma, e fica colado às mensagens vivas.
        // O começo do bloco não se mexe por causa dela — o cache de prefixo agradece.
        string pendente = Pendencias.Render(PendenciasVivas);
        if (pendente.Length > 0) texto.Append(pendente);

        return texto.ToString();
    }

    /// <summary>
    /// As pendências que ainda valem, sobre o que vai ao prompt: os atos e depois os capítulos
    /// soltos, em ordem. Uma falha que um trecho posterior resolveu sai; interrupção e assunto
    /// só contam do trecho mais recente.
    /// </summary>
    public IReadOnlyList<Pendencia> PendenciasVivas
    {
        get
        {
            var trechos = new List<(IReadOnlyList<Pendencia>? Pendencias, IReadOnlyList<Artifact> Artefatos)>();

            foreach (var ato in _acts) trechos.Add((ato.Pendencias, ato.Artifacts));
            foreach (var capitulo in _chapters.Where(c => c.Index > LastCoveredChapter))
                trechos.Add((capitulo.Pendencias, capitulo.Artifacts));

            return Pendencias.Resolver(trechos);
        }
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

    // ─────────────────────────────────────────────────────────────────────────
    // A conta da economia
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tudo o que já foi engolido, em tokens crus. Somado dos CAPÍTULOS e não dos atos: cada
    /// turno passou por exatamente um capítulo, e somar os dois contaria o mesmo turno duas
    /// vezes.
    /// </summary>
    public int TokensCrus => _chapters.Sum(c => c.TokensDosTurnos);

    /// <summary>
    /// O que a memória pesa hoje: os atos mais os capítulos que nenhum ato absorveu. É a mesma
    /// regra do <see cref="RenderChapters"/> — capítulo coberto por ato não vai ao prompt, e
    /// contá-lo aqui inventaria um peso que ninguém paga.
    /// </summary>
    public int TokensDaMemoria =>
        _acts.Sum(a => a.TokensDoAto)
        + _chapters.Where(c => c.Index > LastCoveredChapter).Sum(c => c.TokensDoCapitulo);

    /// <summary>
    /// Quanto a memória está poupando agora. Nunca negativo — ver <see cref="Chapter.Economia"/>.
    /// </summary>
    public int Economia => TokensCrus > TokensDaMemoria ? TokensCrus - TokensDaMemoria : 0;

    /// <summary>
    /// Se a conta é confiável. Um capítulo gravado antes da medição traz zero, e somar zero
    /// com medidas reais produziria uma economia menor que a verdadeira — sem nenhum sinal de
    /// que faltava informação.
    /// </summary>
    public bool MedidaCompleta =>
        _chapters.Count > 0
        && _chapters.TrueForAll(c => c.TemMedida)
        && _acts.TrueForAll(a => a.TemMedida);

    /// <summary>Índice do próximo capítulo a nascer.</summary>
    public int NextChapterIndex => _chapters.Count == 0 ? 0 : _chapters[^1].Index + 1;

    /// <summary>Índice do próximo ato a nascer.</summary>
    public int NextActIndex => _acts.Count == 0 ? 0 : _acts[^1].Index + 1;

    /// <summary>
    /// Índice, no <c>raw.jsonl</c>, do último turno já coberto por algum capítulo. -1 se nenhum.
    /// <para>
    /// Não é só o maior <c>LastTurn</c>. Até 16/09 o capítulo gravava a posição do turno no
    /// histórico VIVO, e o vivo recomeça do zero a cada compactação: o segundo capítulo de
    /// uma conversa também dizia "turnos 0–0". Ao reabrir, o maior deles apontava para o
    /// começo da conversa, e turnos já resumidos voltavam crus ao lado do resumo deles.
    /// </para>
    /// <para>
    /// Gravado assim, um capítulo começa em índice que não passa do já coberto — o absoluto
    /// sempre começa DEPOIS. É essa a marca: o relativo soma quantos turnos cobriu, o absoluto
    /// diz onde parou. Uma conversa com capítulos dos dois tipos se resolve na mesma passada.
    /// </para>
    /// </summary>
    public int LastCoveredTurn
    {
        get
        {
            int coberto = -1;

            foreach (var capitulo in _chapters)
            {
                coberto = capitulo.FirstTurn > coberto
                    ? capitulo.LastTurn
                    : coberto + (capitulo.LastTurn - capitulo.FirstTurn + 1);
            }

            return coberto;
        }
    }

    /// <summary>Índice do último capítulo já absorvido por um ato. -1 se nenhum.</summary>
    public int LastCoveredChapter => _acts.Count == 0 ? -1 : _acts.Max(a => a.LastChapter);

    /// <summary>Capítulos que ainda não viraram ato, em ordem.</summary>
    public IReadOnlyList<Chapter> UncoveredChapters =>
        _chapters.Where(c => c.Index > LastCoveredChapter).ToList();
}
