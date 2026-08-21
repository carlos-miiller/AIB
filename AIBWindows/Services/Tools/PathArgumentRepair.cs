using System.Text;

namespace AIB.Services.Tools;

/// <summary>
/// Conserta caminhos que chegaram do modelo com escapes JSON mal emitidos.
/// <para>
/// Modelos pequenos escrevem <c>"C:\temp\x.txt"</c> no JSON de argumentos em vez de
/// <c>"C:\\temp\\x.txt"</c>. O parser então interpreta <c>\t</c> como TAB e a ferramenta recebe
/// <c>C:&lt;TAB&gt;emp\x.txt</c> — medido com qwen3.5:4b, que devolveu <c>C:\emp\ola.txt</c>.
/// Atinge todo caminho cuja pasta comece com t, n, r, b, f ou v: temp, novo, reports, backup…
/// </para>
/// <para>
/// Reparar é seguro porque caractere de controle é ILEGAL em nome de arquivo no Windows: se um
/// apareceu, ele só pode ter vindo de um escape que deveria ser literal. A conversão é
/// determinística — TAB só pode ter nascido de <c>\t</c>.
/// </para>
/// <para>
/// Vale só para CAMINHO. Não use no conteúdo de um arquivo nem em comando de shell, onde uma
/// tabulação ou quebra de linha de verdade é legítima e reescrevê-la corromperia o dado.
/// </para>
/// </summary>
public static class PathArgumentRepair
{
    /// <summary>
    /// Devolve o caminho com os caracteres de controle revertidos para a sequência literal que
    /// o modelo quis escrever. <paramref name="repaired"/> diz se algo mudou — o chamador usa
    /// isso para avisar o modelo, que assim corrige o JSON na próxima chamada.
    /// </summary>
    public static string Normalize(string? path, out bool repaired)
    {
        repaired = false;
        if (string.IsNullOrEmpty(path)) return path ?? string.Empty;

        var sb = new StringBuilder(path.Length);

        foreach (char c in path)
        {
            char? letra = c switch
            {
                '\t' => 't',
                '\n' => 'n',
                '\r' => 'r',
                '\b' => 'b',
                '\f' => 'f',
                '\v' => 'v',
                '\0' => '0',
                '\a' => 'a',
                _ => null
            };

            if (letra == null)
            {
                sb.Append(c);
                continue;
            }

            // O modelo escreveu "\t" querendo dizer barra invertida + t.
            sb.Append('\\').Append(letra.Value);
            repaired = true;
        }

        return sb.ToString();
    }

    /// <summary>Atalho para quando o chamador não se importa se houve reparo.</summary>
    public static string Normalize(string? path) => Normalize(path, out _);
}
