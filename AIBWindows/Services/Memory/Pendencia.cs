using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIB.Services.Memory;

/// <summary>
/// Uma ponta solta de um trecho compactado: algo que ficou por fazer ou que falhou e não foi
/// resolvido depois.
/// <para>
/// Existe para a compactação poder levar também os turnos RECENTES. Eles ficavam sempre crus
/// porque são o contexto de um "continue" — e um turno de 18 ferramentas pesava 19 mil tokens
/// só para isso. A lista de pendências é o que o "continue" precisa, sem o resto.
/// </para>
/// </summary>
/// <param name="Tipo">
/// <c>interrompido</c> (o turno parou sem terminar), <c>falha</c> (gravação ou comando que falhou
/// e não deu certo depois) — os dois tirados por código — ou <c>assunto</c>, o que o resumidor
/// apontou como pedido e não feito.
/// </param>
/// <param name="Texto">A linha que vai ao prompt.</param>
/// <param name="Alvo">Falha: o literal (caminho ou comando). É por ele que um sucesso posterior a resolve.</param>
/// <param name="Kind">Falha: a natureza do artefato que falhou.</param>
public sealed record Pendencia(string Tipo, string Texto, string? Alvo = null, ArtifactKind? Kind = null)
{
    public const string Interrompido = "interrompido";
    public const string Falha = "falha";
    public const string Assunto = "assunto";
}

/// <summary>
/// Tira, resolve e escreve pendências. Código puro, exceto <see cref="LerDoResumo"/>, que só
/// interpreta o texto que o resumidor devolveu.
/// </summary>
public static class Pendencias
{
    /// <summary>Teto de itens na seção do prompt. Pendência demais deixa de ser lista e vira ruído.</summary>
    public const int TetoNoPrompt = 8;

    /// <summary>Teto de caracteres por linha.</summary>
    public const int TetoDaLinha = 240;

    /// <summary>Teto de pendências de assunto que o resumidor pode apontar por trecho.</summary>
    public const int TetoDoResumidor = 3;

    /// <summary>
    /// As pendências por código de um trecho de turnos, em ordem.
    /// <para>
    /// FALHA: gravação, edição ou comando que falhou e não teve sucesso depois, no mesmo alvo,
    /// dentro do trecho. Leitura que falhou não entra — procurar um arquivo que não existe e
    /// seguir em frente é comum e não deixa nada por fazer. Recusa sua também não: é decisão,
    /// não ponta solta, e listá-la convidaria o modelo a tentar de novo.
    /// </para>
    /// <para>
    /// INTERROMPIDO: só o ÚLTIMO turno do trecho. Um turno interrompido seguido de outro já
    /// teve resposta sua — continuar, desistir, mudar de assunto.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Pendencia> Extrair(IReadOnlyList<Turn> turnos)
    {
        var falhas = new Dictionary<string, Pendencia>(StringComparer.OrdinalIgnoreCase);
        var ordem = new List<string>();

        foreach (var turno in turnos)
        {
            foreach (var artefato in ArtifactExtractor.Extract(turno))
            {
                string? chave = ChaveDe(artefato);
                if (chave == null) continue;

                // Comando que só lê nunca vira pendência, pela mesma razão que leitura de arquivo
                // não vira: procurar e não achar é rotina. Eram eles que enchiam a lista — um
                // `docker exec … grep` que não casou nada ficava anos como ponta solta.
                if (artefato.Kind == ArtifactKind.CommandRun && ComandoDeShell.SoLeitura(artefato.Value))
                {
                    if (!artefato.Failed) falhas.Remove(chave);
                    continue;
                }

                if (artefato.Failed)
                {
                    // A ordem guarda cada chave UMA vez. Sem o segundo teste, o comando que
                    // falha, dá certo e falha de novo entrava duas vezes na lista — e saía
                    // duas vezes no Pendente, como duas pontas soltas onde só há uma.
                    if (!falhas.ContainsKey(chave) && !ordem.Contains(chave, StringComparer.OrdinalIgnoreCase))
                        ordem.Add(chave);

                    falhas[chave] = new Pendencia(
                        Pendencia.Falha, TextoDaFalha(artefato), artefato.Value, artefato.Kind);
                }
                else
                {
                    falhas.Remove(chave);
                }
            }
        }

        var lista = ordem.Where(falhas.ContainsKey).Select(k => falhas[k]).ToList();

        if (turnos.Count > 0 && MotivoDaInterrupcao(turnos[^1]) is string motivo)
        {
            var texto = new StringBuilder($"o último turno parou sem terminar ({motivo}).");

            string pedido = Linha(turnos[^1].UserText);
            if (pedido.Length > 0) texto.Append($" Pedido: \"{Cortar(pedido, 120)}\".");

            var doTurno = ArtifactExtractor.Extract(turnos[^1]);
            if (doTurno.Count > 0) texto.Append($" Última ação: {doTurno[^1].Render().TrimStart('-', ' ')}");

            lista.Add(new Pendencia(Pendencia.Interrompido, Cortar(texto.ToString(), TetoDaLinha)));
        }

        return lista;
    }

    /// <summary>
    /// As pendências que continuam valendo, sobre trechos em ordem cronológica.
    /// <para>
    /// Falha some quando um trecho POSTERIOR tem sucesso no mesmo alvo. Interrupção e assunto
    /// só valem do trecho mais recente: o que veio depois deles já é a resposta a eles.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Pendencia> Resolver(
        IReadOnlyList<(IReadOnlyList<Pendencia>? Pendencias, IReadOnlyList<Artifact> Artefatos)> trechos)
    {
        var vivas = new List<Pendencia>();

        for (int i = 0; i < trechos.Count; i++)
        {
            bool ultimo = i == trechos.Count - 1;

            foreach (var p in trechos[i].Pendencias ?? Array.Empty<Pendencia>())
            {
                if (p.Tipo != Pendencia.Falha)
                {
                    if (ultimo) vivas.Add(p);
                    continue;
                }

                bool resolvida = trechos.Skip(i + 1).Any(t => t.Artefatos.Any(a =>
                    !a.Failed && ChaveDe(a) is string chave
                    && string.Equals(chave, ChaveDe(p), StringComparison.OrdinalIgnoreCase)));

                if (!resolvida) vivas.Add(p);
            }
        }

        // A mesma pendência, dita duas vezes por duas bocas, é uma só.
        //
        // O resumidor do ato LÊ o Pendente dos capítulos, e às vezes devolve as mesmas falhas na
        // linha PENDENTE: dele. Aí a pendência entra duas vezes: uma como falha, com alvo, e
        // outra como assunto, sem alvo — e a chave, que é o alvo, não casa as duas. Numa sessão
        // real o Pendente saiu com sete linhas, das quais duas eram cópia.
        var deFalha = vivas
            .Where(p => p.Tipo == Pendencia.Falha && p.Kind == ArtifactKind.CommandRun && p.Alvo != null)
            .Select(p => ComandoDeShell.Assinatura(p.Alvo))
            .Where(a => a.Length >= 12)
            .ToList();

        if (deFalha.Count > 0)
            vivas = vivas
                .Where(p => p.Tipo != Pendencia.Assunto
                            || !deFalha.Any(a => p.Texto.Contains(a, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        // A mesma falha pode vir de dois trechos; a lista não repete.
        return vivas
            .GroupBy(p => p.Tipo == Pendencia.Falha ? "falha|" + ChaveDe(p) : p.Tipo + "|" + p.Texto,
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
    }

    /// <summary>A seção do prompt, ou vazio. Interrupções primeiro: são o que um "continue" retoma.</summary>
    public static string Render(IReadOnlyList<Pendencia> pendencias)
    {
        if (pendencias.Count == 0) return "";

        var texto = new StringBuilder("### Pendente\n");
        foreach (var p in pendencias
                     .OrderBy(p => p.Tipo == Pendencia.Interrompido ? 0 : p.Tipo == Pendencia.Falha ? 1 : 2)
                     .Take(TetoNoPrompt))
            texto.Append("- ").Append(p.Texto).Append('\n');

        return texto.ToString();
    }

    /// <summary>
    /// A linha de pendências que o resumidor devolve. Vale para os dois formatos: no antigo ela
    /// vem depois do parágrafo; no por seções é a seção PENDENTE, depois de OBJETIVO e APRENDIDO.
    /// </summary>
    public const string MarcaDoResumo = "PENDENTE:";

    /// <summary>
    /// Separa do resto a linha <c>PENDENTE:</c> que o resumidor devolve — o parágrafo, no formato
    /// antigo, ou as seções OBJETIVO e APRENDIDO, no formato por seções. Sem a linha, ou com
    /// "nenhuma", não há pendência de assunto — e o resto sai como veio.
    /// </summary>
    public static (string Resumo, IReadOnlyList<Pendencia> Assunto) LerDoResumo(string? texto)
    {
        string t = (texto ?? "").Trim();
        var m = Regex.Match(t, @"(?im)^[\s*_]*PENDENTES?[\s*_]*:[\s*_]*(.*)$");
        if (!m.Success) return (t, Array.Empty<Pendencia>());

        string resumo = (t[..m.Index] + t[(m.Index + m.Length)..]).Trim();
        string itens = m.Groups[1].Value.Trim().TrimEnd('.');

        if (Regex.IsMatch(itens, @"^(nenhum|nenhuma|nada|n/a|-)?$", RegexOptions.IgnoreCase))
            return (resumo, Array.Empty<Pendencia>());

        var assunto = itens
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(i => i.Length > 2)
            .Take(TetoDoResumidor)
            .Select(i => new Pendencia(Pendencia.Assunto, Cortar(i, TetoDaLinha)))
            .ToList();

        return (resumo, assunto);
    }

    /// <summary>"interrompido pelo teto de 18 etapas", ou nulo se o turno terminou.</summary>
    public static string? MotivoDaInterrupcao(Turn turno)
    {
        string fala = turno.AssistantText.Trim();
        const string prefixo = "[turno encerrado sem resposta:";

        if (!fala.StartsWith(prefixo, StringComparison.Ordinal)) return null;

        int fim = fala.IndexOf(']');
        return (fim > prefixo.Length ? fala[prefixo.Length..fim] : fala[prefixo.Length..]).Trim();
    }

    /// <summary>
    /// A chave que casa a falha com o sucesso que veio depois. Comando casa pela ASSINATURA, e
    /// não pelo texto: a segunda tentativa quase nunca é byte a byte a primeira — muda um
    /// <c>| Select-Object -Last 10</c>, some um <c>cd</c> na frente — e pelo texto exato a
    /// pendência ficava viva mesmo depois de o comando funcionar. Uma sessão fechou com oito
    /// pendências assim, todas resolvidas. Ver <see cref="ComandoDeShell.Assinatura"/>.
    /// </summary>
    private static string? ChaveDe(Artifact a) => a.Kind switch
    {
        ArtifactKind.FileWritten => "arquivo|" + a.Value,
        ArtifactKind.CommandRun => "comando|" + ComandoDeShell.Assinatura(a.Value),
        _ => null
    };

    private static string? ChaveDe(Pendencia p) => p.Kind switch
    {
        ArtifactKind.FileWritten => "arquivo|" + p.Alvo,
        ArtifactKind.CommandRun => "comando|" + ComandoDeShell.Assinatura(p.Alvo),
        _ => null
    };

    private static string TextoDaFalha(Artifact a)
    {
        // Pendência é o que o "continue" retoma. Um comando de apagar que falhou não pode virar
        // convite para tentar de novo.
        if (a.Kind == ArtifactKind.CommandRun && ComandoQueApaga.Eh(a.Value))
            return Cortar(
                $"um comando para apagar {ComandoQueApaga.Alvo(a.Value) ?? "arquivos"} falhou — "
                + "não tente de novo sem o usuário pedir", TetoDaLinha);

        string acao = a.Kind == ArtifactKind.FileWritten
            ? (a.Tool == AIB.Services.Ferramentas.Editar ? "editar" : "gravar")
            : "executar";

        string erro = string.IsNullOrWhiteSpace(a.Detail) ? "" : $": {Linha(a.Detail)}";
        return Cortar($"{acao} {a.Value} falhou e não deu certo depois{erro}", TetoDaLinha);
    }

    private static string Linha(string? texto) =>
        Regex.Replace(texto ?? "", @"\s+", " ").Trim();

    private static string Cortar(string texto, int teto) =>
        texto.Length <= teto ? texto : texto[..(teto - 1)] + "…";
}
