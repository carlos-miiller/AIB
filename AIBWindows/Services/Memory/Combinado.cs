using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIB.Services.Memory;

/// <summary>Um pedido do usuário copiado como ele escreveu.</summary>
/// <param name="Turno">Índice do turno (numeração do registro).</param>
/// <param name="Texto">O trecho literal, sem paráfrase.</param>
public sealed record FalaCombinada(int Turno, string Texto);

/// <summary>
/// "Combinado com o usuário": os pedidos que carregam VALOR — horário, número, caminho, nome
/// entre aspas —, copiados por código.
/// <para>
/// Numa conversa real, o usuário pediu "Sábados: 12:00 ~ 14:00" e o ato virou "modificações nos
/// horários". O valor, que era o pedido, sumiu na segunda camada de resumo. Medido no
/// LongMemEval: trocar o texto original por resumo piora as respostas; o Codex reinsere as falas
/// do usuário literalmente, por código, pelo mesmo motivo.
/// </para>
/// <para>
/// A regra é deliberadamente simples, e erra para o lado de copiar: fala curta com algum valor
/// entra inteira; fala longa contribui só com as linhas que têm valor. "leia os arquivos" e
/// "continue" não entram — não há o que perder num resumo delas.
/// </para>
/// </summary>
public static class Combinados
{
    /// <summary>Até este tamanho a fala entra inteira.</summary>
    public const int FalaCurta = 280;

    /// <summary>Teto de cada linha copiada de uma fala longa.</summary>
    public const int TetoDaLinha = 240;

    /// <summary>Linhas de uma mesma fala longa.</summary>
    public const int LinhasPorFala = 3;

    /// <summary>Teto por capítulo.</summary>
    public const int TetoNoCapitulo = 8;

    /// <summary>
    /// Teto por ato. Ficam os MAIS RECENTES: pedido novo sobre a mesma coisa costuma substituir o
    /// antigo, e a ordem cronológica deixa o modelo ver a evolução.
    /// </summary>
    public const int TetoNoAto = 16;

    private static readonly Regex Valor = new(
        @"\d|""[^""]{2,}""|“[^”]{2,}”|'[^']{3,}'|[A-Za-z]:\\|\\\\|https?://|\S+@\S+\.\w+|\.\w{2,4}\b",
        RegexOptions.Compiled);

    /// <summary>Se o texto carrega algum valor que um resumo perderia.</summary>
    public static bool TemValor(string texto) => Valor.IsMatch(texto ?? "");

    public static IReadOnlyList<FalaCombinada> Extrair(IReadOnlyList<Turn> turnos)
    {
        var falas = new List<FalaCombinada>();

        foreach (var turno in turnos ?? Array.Empty<Turn>())
        {
            string texto = (turno.UserText ?? "").Trim();
            if (texto.Length == 0 || !TemValor(texto)) continue;

            if (texto.Length <= FalaCurta)
            {
                falas.Add(new FalaCombinada(turno.Index, Compactar(texto)));
                continue;
            }

            foreach (string linha in texto.Replace("\r", "").Split('\n')
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0 && TemValor(l))
                         .Take(LinhasPorFala))
            {
                falas.Add(new FalaCombinada(turno.Index,
                    linha.Length > TetoDaLinha ? linha[..TetoDaLinha] + "…" : linha));
            }
        }

        return falas.TakeLast(TetoNoCapitulo).ToList();
    }

    /// <summary>Os de vários trechos, em ordem, sem repetir, com os mais recentes ficando.</summary>
    public static IReadOnlyList<FalaCombinada> Juntar(IEnumerable<IReadOnlyList<FalaCombinada>?> trechos) =>
        trechos.Where(t => t != null)
               .SelectMany(t => t!)
               .GroupBy(f => (f.Turno, f.Texto))
               .Select(g => g.First())
               .OrderBy(f => f.Turno)
               .TakeLast(TetoNoAto)
               .ToList();

    public static string Render(IReadOnlyList<FalaCombinada>? falas)
    {
        if (falas == null || falas.Count == 0) return "";

        var texto = new StringBuilder("Combinado com o usuário (palavras dele):\n");
        foreach (var f in falas)
            texto.Append("- (turno ").Append(f.Turno + 1).Append(") \"").Append(f.Texto).Append("\"\n");
        return texto.ToString();
    }

    /// <summary>Quebras de linha viram " / ": a fala continua legível numa linha só.</summary>
    private static string Compactar(string texto) =>
        Regex.Replace(texto.Replace("\r", ""), @"\s*\n\s*", " / ").Trim();
}
