using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using AIB.Services.Memory;

namespace AIB.Services;

/// <summary>Como a ação terminou.</summary>
public enum ActionStatus
{
    Done = 0,
    Failed = 1
}

/// <summary>
/// O trecho que uma edição trocou: o que estava e o que entrou no lugar. Só para a tela —
/// a seção ANTES E DEPOIS do tooltip. Os dois lados já chegam com teto.
/// </summary>
public sealed record TrocaDeTexto(string Antes, string Depois)
{
    /// <summary>Largura de uma tabulação na exibição.</summary>
    public const int EspacosPorTab = 4;

    /// <summary>
    /// As linhas de cada lado prontas para a tela: tabulação vira espaço e sai o recuo COMUM
    /// aos dois lados.
    /// <para>
    /// Trecho de HTML ou código chega recuado — três, quatro tabulações antes do texto. Numa
    /// caixa de 340px sem quebra de linha, esse recuo empurrava o conteúdo inteiro para fora da
    /// área visível e a linha mostrava só o sinal de − ou +. O recuo comum não diz nada sobre a
    /// troca; o recuo RELATIVO diz, e é preservado: a linha que entrou mais funda continua mais
    /// funda que a vizinha.
    /// </para>
    /// </summary>
    public (IReadOnlyList<string> Antes, IReadOnlyList<string> Depois) LinhasParaExibir()
    {
        var antes = Linhas(Antes);
        var depois = Linhas(Depois);

        int recuo = antes.Concat(depois)
            .Where(l => l.Trim().Length > 0)
            .Select(l => l.Length - l.TrimStart(' ').Length)
            .DefaultIfEmpty(0)
            .Min();

        return (antes.Select(l => SemRecuo(l, recuo)).ToList(),
                depois.Select(l => SemRecuo(l, recuo)).ToList());
    }

    private static List<string> Linhas(string? trecho) =>
        string.IsNullOrEmpty(trecho)
            ? new List<string>()
            : trecho.Replace("\r", "")
                    .Replace("\t", new string(' ', EspacosPorTab))
                    .TrimEnd('\n')
                    .Split('\n')
                    .ToList();

    // Linha em branco mais curta que o recuo vira vazia; as outras perdem só o recuo comum.
    private static string SemRecuo(string linha, int recuo) =>
        linha.Length >= recuo ? linha[recuo..] : linha.TrimStart(' ');
}

/// <summary>
/// Uma linha do histórico de ações — §6.3 da spec de chat.
/// <para>
/// Guarda o LITERAL: caminho completo, linha de comando exata, saída bruta. É o mesmo princípio
/// dos artefatos da memória, e pelo mesmo motivo — quem lê esta lista vai clicar nela para abrir
/// o Explorer, ou copiar o comando para rodar de novo. Uma paráfrase manda a pessoa para o lugar
/// errado.
/// </para>
/// </summary>
public sealed class ActionLogEntry
{
    public required string Tool { get; init; }

    /// <summary>Caminho ou comando, como será exibido — pode vir encurtado.</summary>
    public required string Target { get; init; }

    /// <summary>O literal inteiro, sem abreviação. É o que o tooltip mostra.</summary>
    public required string FullTarget { get; init; }

    /// <summary>Linha de comando, quando a ferramenta foi <c>shell</c>.</summary>
    public string? Command { get; init; }

    /// <summary>Saída do console como saiu, para o tooltip em bloco.</summary>
    public string? RawOutput { get; init; }

    /// <summary>Resumo curto: "12 arquivos", "+8 linhas". Em falha, a mensagem do erro.</summary>
    public string? Result { get; init; }

    /// <summary>Antes e depois de uma edição; nulo nas outras ferramentas.</summary>
    public TrocaDeTexto? Troca { get; init; }

    public ArtifactKind Kind { get; init; } = ArtifactKind.CommandRun;

    public ActionStatus Status { get; init; } = ActionStatus.Done;

    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>Hora no formato do item (§6.3 b).</summary>
    public string Hora => Timestamp.ToString("HH:mm");

    /// <summary>
    /// Rótulo do separador de dia (§6.3): "HOJE", "ONTEM", ou a data.
    /// </summary>
    public string DiaRotulo
    {
        get
        {
            var hoje = DateTime.Today;
            var dia = Timestamp.Date;

            if (dia == hoje) return "HOJE";
            if (dia == hoje.AddDays(-1)) return "ONTEM";
            return Timestamp.ToString("dd/MM");
        }
    }

    /// <summary>
    /// Se a ação é de leitura pura. §6.3 pinta o marcador dessas em cinza para elas não
    /// competirem visualmente com escrita e destruição.
    /// </summary>
    public bool SoLeitura => Status == ActionStatus.Done && Kind == ArtifactKind.FileRead;

    /// <summary>
    /// Rótulo da primeira seção do tooltip em bloco (§4.6 ii). "CAMINHO COMPLETO" sobre
    /// "*.cs em C:\projeto" mentiria sobre o que o texto é.
    /// </summary>
    public string RotuloDoAlvo => Tool switch
    {
        _ when Command != null => "COMANDO",
        Ferramentas.Procurar or Ferramentas.Buscar => "BUSCA",
        Ferramentas.Email or Ferramentas.LerEmail => "CONSULTA",
        _ => "CAMINHO COMPLETO"
    };
}

/// <summary>
/// Registro cronológico do que a IA fez na conversa — §6.3.
/// <para>
/// Alimentado pelos eventos tipados de ferramenta, os mesmos que desenham a cadeia de ações. É
/// a segunda vida deles: a cadeia mostra o turno corrente e some com ele; esta lista atravessa a
/// conversa inteira.
/// </para>
/// <para>
/// Em memória, e de propósito. O que precisa sobreviver ao fechamento do app já está no
/// <c>raw.jsonl</c> da memória, que é a fonte de verdade; duplicar isso num segundo arquivo
/// criaria duas versões da mesma história para discordarem entre si. A consequência é que
/// REABRIR uma conversa tem de remontar a lista a partir do raw — ver <see cref="Reconstruir"/>.
/// Sem isso, a aba abria vazia para toda conversa que não fosse a da sessão corrente.
/// </para>
/// </summary>
public static class ActionLogService
{
    private static readonly object Trava = new();

    /// <summary>
    /// Teto do registro. Uma conversa muito longa não pode virar consumo de memória sem fim, e
    /// as entradas mais antigas já estão no raw.jsonl.
    /// </summary>
    public const int MaxEntradas = 500;

    private static readonly Colecao Itens = new();

    /// <summary>Mais recente no TOPO — §6.3.</summary>
    public static ObservableCollection<ActionLogEntry> Entries => Itens;

    /// <summary>Quantas ações falharam, para o rodapé da aba.</summary>
    public static int Falhas
    {
        get
        {
            lock (Trava)
            {
                int n = 0;
                foreach (var item in Itens) if (item.Status == ActionStatus.Failed) n++;
                return n;
            }
        }
    }

    public static void Add(ActionLogEntry? entrada)
    {
        if (entrada == null) return;

        lock (Trava)
        {
            Itens.Insert(0, entrada);
            while (Itens.Count > MaxEntradas) Itens.RemoveAt(Itens.Count - 1);
        }
    }

    /// <summary>
    /// Monta a entrada a partir do que o evento de ferramenta trouxe.
    /// <para>
    /// O artefato é a fonte do literal; quando ele não existe — ferramenta sem extrator próprio
    /// — a entrada ainda é registrada, com o nome da ferramenta no lugar do alvo. Sumir com a
    /// ação porque não se sabe o literal dela esconderia justamente o que é incomum.
    /// </para>
    /// </summary>
    /// <param name="argumento">
    /// O alvo descrito a partir dos argumentos da chamada — caminho, busca, consulta. Entra
    /// quando não há artefato: sem ele, o alvo virava o nome da ferramenta e a linha lia
    /// "edit edit", sem dizer em que arquivo.
    /// </param>
    /// <param name="resumo">Resultado resumido, quando o artefato não traz um.</param>
    /// <param name="quando">Hora da ação. Vazio é agora; a reconstrução passa a hora gravada.</param>
    /// <param name="troca">O antes e depois, quando a ação foi uma edição.</param>
    public static ActionLogEntry Construir(
        string ferramenta,
        Artifact? artefato,
        bool falhou,
        string? detalhe,
        string? saidaBruta,
        string? argumento = null,
        string? resumo = null,
        DateTime? quando = null,
        TrocaDeTexto? troca = null)
    {
        string literal = artefato?.Value
                         ?? (string.IsNullOrWhiteSpace(argumento) ? ferramenta : argumento);

        var tipo = artefato?.Kind ?? TipoPeloNome(ferramenta);

        return new ActionLogEntry
        {
            Tool = ferramenta,
            Target = Encurtar(literal),
            FullTarget = literal,
            Command = tipo == ArtifactKind.CommandRun ? literal : null,
            RawOutput = saidaBruta,
            Result = falhou ? (detalhe ?? artefato?.Detail ?? "falhou") : (artefato?.Detail ?? resumo),
            Troca = troca,
            Kind = tipo,
            Status = falhou ? ActionStatus.Failed : ActionStatus.Done,
            Timestamp = quando ?? DateTime.Now
        };
    }

    /// <summary>
    /// Remonta as ações de uma conversa a partir dos turnos gravados no <c>raw.jsonl</c>, em
    /// ordem de execução.
    /// <para>
    /// Passa pelas MESMAS funções que a tela usa ao vivo — extrator, resumo de argumento, resumo
    /// de resultado, saída bruta. Uma conversa reaberta precisa mostrar a mesma linha que mostrou
    /// quando a ação aconteceu; um segundo caminho de montagem divergiria no primeiro ajuste.
    /// </para>
    /// <para>
    /// A chamada é pareada com o resultado pelo id, como no extrator da memória. Chamada sem
    /// resultado fica de fora: sem ele não se sabe se algo aconteceu.
    /// </para>
    /// <para>
    /// A hora é a do turno gravado, e não a de cada ferramenta — o raw guarda uma por turno. As
    /// ações de um turno saem com a mesma hora, na ordem em que os resultados chegaram.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ActionLogEntry> Reconstruir(IReadOnlyList<TurnRecord>? turnos)
    {
        var entradas = new List<ActionLogEntry>();
        if (turnos == null) return entradas;

        foreach (var turno in turnos)
        {
            if (turno?.Messages == null) continue;

            DateTime? quando = DateTime.TryParse(
                turno.AtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var gravado)
                ? gravado.ToLocalTime()
                : null;

            var chamadas = new Dictionary<string, ToolCallRecord>(StringComparer.Ordinal);

            foreach (var mensagem in turno.Messages)
            {
                if (mensagem.ToolCalls is { Count: > 0 })
                {
                    foreach (var chamada in mensagem.ToolCalls)
                        if (chamada?.Id != null) chamadas[chamada.Id] = chamada;
                    continue;
                }

                if (mensagem.Role != "tool" || mensagem.ToolCallId == null) continue;
                if (!chamadas.Remove(mensagem.ToolCallId, out var feita)) continue;

                string resultado = mensagem.Text ?? "";
                bool falhou = ArtifactExtractor.Falhou(resultado);

                entradas.Add(Construir(
                    feita.Name,
                    ArtifactExtractor.Construir(feita.Name, feita.Arguments, resultado),
                    falhou,
                    falhou ? ArtifactExtractor.PrimeiraLinhaDoErro(resultado) : null,
                    ArtifactExtractor.SaidaBruta(feita.Name, resultado),
                    ArtifactExtractor.ResumirParaTela(feita.Name, feita.Arguments),
                    falhou ? null : ArtifactExtractor.ResumirResultado(feita.Name, resultado),
                    quando,
                    ArtifactExtractor.TrocaDaEdicao(feita.Name, feita.Arguments)));
            }
        }

        return entradas;
    }

    /// <summary>
    /// Troca o registro inteiro pelas ações de outra conversa, em ordem de execução.
    /// <para>
    /// UMA notificação só. O painel remonta a aba a cada mudança da lista, e restaurar
    /// quinhentas entradas com <see cref="Add"/> seria quinhentas remontagens seguidas.
    /// </para>
    /// </summary>
    public static void Restaurar(IEnumerable<ActionLogEntry>? entradasEmOrdem)
    {
        var lista = new List<ActionLogEntry>(entradasEmOrdem ?? Array.Empty<ActionLogEntry>());

        // Mais recente no topo, e o teto corta as mais ANTIGAS — as mesmas que o Add cortaria.
        lista.Reverse();
        if (lista.Count > MaxEntradas) lista.RemoveRange(MaxEntradas, lista.Count - MaxEntradas);

        lock (Trava) Itens.Substituir(lista);
    }

    /// <summary>
    /// A natureza da ação quando não há artefato. Busca e consulta são leitura pura, e §6.4
    /// pinta leitura de cinza — antes elas caíam em "comando" e saíam lilás, competindo com
    /// o que de fato mudou a máquina.
    /// </summary>
    private static ArtifactKind TipoPeloNome(string ferramenta) => ferramenta switch
    {
        Ferramentas.Ler or Ferramentas.Procurar or Ferramentas.Buscar
            or Ferramentas.Email or Ferramentas.LerEmail => ArtifactKind.FileRead,
        Ferramentas.Gravar or Ferramentas.Editar => ArtifactKind.FileWritten,
        _ => ArtifactKind.CommandRun
    };

    /// <summary>
    /// Encurta preservando o FIM, que é a parte informativa de um caminho — §6.2(c) usa o mesmo
    /// princípio com <c>direction:rtl</c>. "C:\Users\...\Services\Memory\Compactor.cs" diz mais
    /// que "C:\Users\Carlo\CPAPS\AIB\AIBWi…".
    /// </summary>
    public static string Encurtar(string? texto, int limite = 34)
    {
        if (string.IsNullOrEmpty(texto) || texto.Length <= limite) return texto ?? "";
        return "…" + texto[^(limite - 1)..];
    }

    public static void Clear()
    {
        lock (Trava) Itens.Clear();
    }

    /// <summary>Lista observável que sabe se substituir inteira com um único aviso.</summary>
    private sealed class Colecao : ObservableCollection<ActionLogEntry>
    {
        public void Substituir(IReadOnlyList<ActionLogEntry> novas)
        {
            CheckReentrancy();

            Items.Clear();
            foreach (var entrada in novas) Items.Add(entrada);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
