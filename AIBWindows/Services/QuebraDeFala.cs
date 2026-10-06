using System;
using System.Collections.Generic;
using System.Linq;

namespace AIB.Services;

/// <summary>
/// Uma resposta do modelo dita em mais de um balão.
/// <para>
/// A persona separa falas de natureza diferente (a reação a uma piada, depois o resultado) com
/// <see cref="Marca"/>; a conversa abre um balão para cada parte. Sem a marca, a reação e a
/// resposta saíam grudadas no mesmo balão, como uma carta, e não como conversa.
/// </para>
/// <para>
/// A marca fica no histórico (o modelo vê o próprio padrão) e sai de tudo o que é mostrado:
/// balão, orbe, notificação. Um símbolo raro, e não <c>---</c> ou linha em branco, que aparecem
/// em Markdown comum e partiriam uma explicação no meio.
/// </para>
/// </summary>
public static class QuebraDeFala
{
    public const string Marca = "⁂";

    /// <summary>As falas do texto, sem a marca e sem as vazias.</summary>
    public static IReadOnlyList<string> Dividir(string texto) =>
        texto.Split(Marca)
             .Select(p => p.Trim())
             .Where(p => p.Length > 0)
             .ToList();

    /// <summary>O texto inteiro, com as falas em parágrafos, para quem mostra tudo junto.</summary>
    public static string Limpar(string texto) =>
        texto.Contains(Marca) ? string.Join("\n\n", Dividir(texto)) : texto;

    /// <summary>
    /// Durante o streaming: as falas já fechadas por uma marca e o resto, que ainda está sendo
    /// escrito.
    /// </summary>
    public static (IReadOnlyList<string> Prontas, string Resto) Separar(string acumulado)
    {
        int ultima = acumulado.LastIndexOf(Marca, StringComparison.Ordinal);
        if (ultima < 0) return (Array.Empty<string>(), acumulado);

        return (Dividir(acumulado[..ultima]), acumulado[(ultima + Marca.Length)..]);
    }
}
