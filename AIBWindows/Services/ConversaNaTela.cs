using System;
using System.Collections.Generic;
using AIB.Services.Memory;

namespace AIB.Services;

/// <summary>
/// O que a tela desenha ao reabrir uma conversa, montado do <c>raw.jsonl</c>: cada fala no seu
/// balão e, entre elas, as ferramentas que o agente usou, na ordem em que aconteceram.
/// <para>
/// Antes a reabertura desenhava só o arquivo do histórico, que guarda a fala do usuário e a
/// ÚLTIMA fala do assistente de cada turno. Um turno de 60 ferramentas que parou no teto, ou
/// que o usuário cancelou, voltava como uma linha só — "[turno encerrado sem resposta: …]" —, sem
/// nada do que foi feito; e as falas do meio da cadeia sumiam de todos os turnos.
/// </para>
/// <para>
/// Função pura, sem WPF: a janela só percorre os itens. Chamada sem resultado fica de fora, como
/// no registro de ações (<see cref="ActionLogService.Reconstruir"/>): sem ele não se sabe se algo
/// aconteceu.
/// </para>
/// </summary>
public static class ConversaNaTela
{
    public abstract record Item;

    /// <summary>Um balão. A do assistente vem sem os blocos de raciocínio.</summary>
    public sealed record Fala(bool DoUsuario, string Texto) : Item;

    /// <summary>Uma ferramenta que rodou: vira chip na cadeia, como ao vivo.</summary>
    public sealed record Acao(
        string Id, string Ferramenta, string Argumento, bool Falhou, bool Recusada,
        Artifact? Artefato, string? Detalhe) : Item;

    /// <summary>Um fato guardado com <c>remember</c>: ao vivo é o aviso "X lembrará disso…", não chip.</summary>
    public sealed record Lembrou(string Fato) : Item;

    public static IReadOnlyList<Item> Montar(IReadOnlyList<TurnRecord>? turnos)
    {
        var itens = new List<Item>();
        if (turnos == null) return itens;

        foreach (var turno in turnos)
        {
            if (turno?.Messages == null) continue;

            var chamadas = new Dictionary<string, ToolCallRecord>(StringComparer.Ordinal);

            foreach (var m in turno.Messages)
            {
                switch (m.Role)
                {
                    case "user":
                        if (!string.IsNullOrWhiteSpace(m.Text)) itens.Add(new Fala(true, m.Text));
                        break;

                    case "assistant":
                        string texto = ThinkBlockStripper.Strip(m.Text ?? "").Trim();
                        if (texto.Length > 0) itens.Add(new Fala(false, texto));

                        if (m.ToolCalls != null)
                            foreach (var c in m.ToolCalls)
                                if (c?.Id != null) chamadas[c.Id] = c;
                        break;

                    case "tool":
                        if (m.ToolCallId == null || !chamadas.Remove(m.ToolCallId, out var feita)) break;
                        if (DaChamada(feita, m.Text ?? "") is { } item) itens.Add(item);
                        break;
                }
            }
        }

        return itens;
    }

    /// <summary>Nulo para o <c>remember</c> que falhou: ao vivo ele também não aparece.</summary>
    private static Item? DaChamada(ToolCallRecord feita, string resultado)
    {
        // Uma operação do 'file' volta à tela como a ferramenta antiga dela, igual ao vivo.
        var (nome, args) = Tools.FileTool.Simples(feita.Name, feita.Arguments);
        feita = feita with { Name = nome, Arguments = args };

        bool falhou = ArtifactExtractor.Falhou(resultado);
        string argumento = ArtifactExtractor.ResumirParaTela(feita.Name, feita.Arguments);

        if (feita.Name == Ferramentas.Lembrar) return falhou ? null : new Lembrou(argumento);

        return new Acao(
            feita.Id, feita.Name, argumento, falhou,
            ArtifactExtractor.Recusado(resultado),
            ArtifactExtractor.Construir(feita.Name, feita.Arguments, resultado),
            falhou ? ArtifactExtractor.PrimeiraLinhaDoErro(resultado) : null);
    }
}
