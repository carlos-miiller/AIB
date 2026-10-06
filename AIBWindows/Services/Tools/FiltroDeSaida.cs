using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIB.Services.Tools;

/// <summary>
/// Enxuga a saída de comando antes de ela virar resultado: linha repetida vira uma com contagem,
/// e o corte, quando precisa haver, guarda o começo, as linhas de erro e o fim.
/// <para>
/// POR QUE EXISTE, MEDIDO. Nas 49 sessões gravadas, a saída de shell somou 33.800 tokens — e
/// não é paga uma vez: fica no contexto e volta em cada requisição até a compactação. Pesada
/// pelos turnos seguintes, foram 204.849 tokens×turnos. Este filtro, simulado sobre as mesmas
/// saídas, tira 24% disso (14% só com a deduplicação, o resto do corte que preserva erros).
/// </para>
/// <para>
/// ONDE NÃO AJUDA, também medido. No <c>read</c> quase nada: cada linha leva o número dela e
/// nenhuma se repete (6%), e o que sobraria seria cortar a faixa que o modelo PEDIU — isso não é
/// economia, é tirar dele o que ele veio buscar. Por isso o filtro vale só para comando e
/// habilidade; o <c>read</c> já tem <c>offset</c>/<c>limit</c>.
/// </para>
/// <para>
/// O corte antigo era cego: os primeiros 8.000 caracteres e nada mais. Num log de build o erro
/// mora no FIM, e era exatamente o que ficava de fora.
/// </para>
/// <para>
/// Configurável (<see cref="UserAppSettings.FiltrarSaidaDeComandos"/>): desligado, volta o corte
/// antigo, para dar para comparar — com o RTK, por exemplo.
/// </para>
/// </summary>
public static class FiltroDeSaida
{
    /// <summary>Em vigor. Quem liga é o <see cref="SettingsService"/>, ao carregar e ao salvar.</summary>
    public static bool Ligado { get; set; } = true;

    /// <summary>
    /// Teto da saída filtrada. Metade do corte antigo: com o começo, os erros e o fim garantidos,
    /// o que fica de fora é o meio — e no meio de um log está o que se repete.
    /// </summary>
    public const int Teto = 4000;

    /// <summary>Linhas de erro que o corte resgata do meio.</summary>
    public const int TetoDeErros = 12;

    /// <summary>
    /// A saída pronta para o modelo. Com o filtro desligado, é o corte antigo em
    /// <paramref name="tetoBruto"/>. A PRIMEIRA linha nunca muda de lugar: é nela que a memória
    /// e o cabeçalho de erro leem o que aconteceu.
    /// </summary>
    public static string Aplicar(string saida, int tetoBruto)
    {
        saida ??= "";

        if (!Ligado)
            return saida.Length > tetoBruto
                ? saida.Substring(0, tetoBruto) + "\n...[Saída truncada devido ao tamanho máximo]."
                : saida;

        var linhas = saida.Replace("\r\n", "\n").Split('\n');
        var (dobradas, repetidas) = Dobrar(linhas);

        string texto = string.Join("\n", dobradas);
        bool cortou = false;

        if (texto.Length > Teto)
        {
            texto = Cortar(dobradas);
            cortou = true;
        }

        if (repetidas == 0 && !cortou) return saida;

        var nota = new StringBuilder("\n[saída filtrada: ");
        nota.Append(linhas.Length).Append(" linha(s) → ").Append(texto.Split('\n').Length);
        if (repetidas > 0) nota.Append("; ").Append(repetidas).Append(" repetida(s) colapsada(s)");
        if (cortou) nota.Append("; meio cortado, mantidos o começo, as linhas de erro e o fim");
        nota.Append(']');

        return texto + nota;
    }

    /// <summary>
    /// Cada linha não vazia aparece uma vez, na posição da primeira ocorrência, com a contagem
    /// quando se repetiu. Linhas em branco seguidas viram uma.
    /// </summary>
    private static (List<string> Linhas, int Repetidas) Dobrar(string[] linhas)
    {
        var contagem = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string l in linhas)
        {
            string k = l.TrimEnd();
            if (k.Length == 0) continue;
            contagem[k] = contagem.TryGetValue(k, out int n) ? n + 1 : 1;
        }

        var saida = new List<string>();
        var vistas = new HashSet<string>(StringComparer.Ordinal);
        int repetidas = 0;
        bool brancoAnterior = false;

        foreach (string l in linhas)
        {
            string k = l.TrimEnd();

            if (k.Length == 0)
            {
                if (!brancoAnterior && saida.Count > 0) saida.Add("");
                brancoAnterior = true;
                continue;
            }

            brancoAnterior = false;

            if (!vistas.Add(k))
            {
                repetidas++;
                continue;
            }

            int vezes = contagem[k];
            saida.Add(vezes > 1 ? $"{k}  (×{vezes})" : k);
        }

        while (saida.Count > 0 && saida[^1].Length == 0) saida.RemoveAt(saida.Count - 1);
        return (saida, repetidas);
    }

    /// <summary>
    /// Começo, as linhas de erro do meio, e o fim — cada parte dentro da sua fatia do teto.
    /// </summary>
    private static string Cortar(List<string> linhas)
    {
        int orcamentoCabeca = Teto / 2, orcamentoCauda = Teto / 4;

        int fimDaCabeca = 0, usados = 0;
        while (fimDaCabeca < linhas.Count && usados + linhas[fimDaCabeca].Length + 1 <= orcamentoCabeca)
            usados += linhas[fimDaCabeca++].Length + 1;

        // A primeira linha entra sempre, mesmo que sozinha passe do orçamento: é ela que diz o
        // que aconteceu.
        if (fimDaCabeca == 0 && linhas.Count > 0) fimDaCabeca = 1;

        int inicioDaCauda = linhas.Count;
        usados = 0;
        while (inicioDaCauda > fimDaCabeca && usados + linhas[inicioDaCauda - 1].Length + 1 <= orcamentoCauda)
            usados += linhas[--inicioDaCauda].Length + 1;

        var erros = linhas.Skip(fimDaCabeca).Take(inicioDaCauda - fimDaCabeca)
            .Where(l => RunCommandTool.TemMarcaDeErro(l))
            .Take(TetoDeErros)
            .ToList();

        int fora = inicioDaCauda - fimDaCabeca - erros.Count;

        var sb = new StringBuilder();
        sb.AppendJoin('\n', linhas.Take(fimDaCabeca));
        sb.Append("\n…[").Append(fora).Append(" linha(s) do meio omitida(s)");
        if (erros.Count > 0) sb.Append("; as de erro seguem abaixo");
        sb.Append("]…");
        foreach (string e in erros) sb.Append('\n').Append(e);
        if (inicioDaCauda < linhas.Count)
        {
            sb.Append("\n…");
            foreach (string l in linhas.Skip(inicioDaCauda)) sb.Append('\n').Append(l);
        }

        return sb.ToString();
    }
}
