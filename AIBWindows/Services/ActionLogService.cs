using System;
using System.Collections.ObjectModel;
using AIB.Services.Memory;

namespace AIB.Services;

/// <summary>Como a ação terminou.</summary>
public enum ActionStatus
{
    Done = 0,
    Failed = 1
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

    /// <summary>Linha de comando, quando a ferramenta foi <c>run_command</c>.</summary>
    public string? Command { get; init; }

    /// <summary>Saída do console como saiu, para o tooltip em bloco.</summary>
    public string? RawOutput { get; init; }

    /// <summary>Resumo curto: "12 arquivos", "+8 linhas". Em falha, a mensagem do erro.</summary>
    public string? Result { get; init; }

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
/// criaria duas versões da mesma história para discordarem entre si.
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

    private static readonly ObservableCollection<ActionLogEntry> Itens = new();

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
    public static ActionLogEntry Construir(
        string ferramenta,
        Artifact? artefato,
        bool falhou,
        string? detalhe,
        string? saidaBruta)
    {
        string literal = artefato?.Value ?? ferramenta;

        return new ActionLogEntry
        {
            Tool = ferramenta,
            Target = Encurtar(literal),
            FullTarget = literal,
            Command = artefato?.Kind == ArtifactKind.CommandRun ? literal : null,
            RawOutput = saidaBruta,
            Result = falhou ? (detalhe ?? artefato?.Detail ?? "falhou") : artefato?.Detail,
            Kind = artefato?.Kind ?? ArtifactKind.CommandRun,
            Status = falhou ? ActionStatus.Failed : ActionStatus.Done
        };
    }

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
}
