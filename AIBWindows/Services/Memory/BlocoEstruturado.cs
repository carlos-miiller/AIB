using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIB.Services.Memory;

/// <summary>
/// O texto de um capítulo ou ato no formato por seções (versão 2).
/// <para>
/// Ordem das seções pela confiança, e não pela narrativa. Objetivo primeiro, curto, para situar.
/// Depois o que é LITERAL e montado por código — o que o usuário pediu com as palavras dele e o
/// estado em que os arquivos ficaram. Por último o que o modelo concluiu (Aprendido), que é a
/// parte que pode estar errada. A seção Pendente não mora aqui: ela é uma só, no fim do bloco de
/// memória, já resolvida entre os trechos.
/// </para>
/// </summary>
public static class BlocoEstruturado
{
    /// <summary>A versão dos registros neste formato. Registro sem versão é o de parágrafo + artefatos.</summary>
    public const int Versao = 2;

    public static string Render(
        string titulo, string? objetivo, IReadOnlyList<FalaCombinada>? combinado,
        EstadoDoTrecho? estado, string fimDe, IReadOnlyList<string>? aprendido,
        int tetoDeItens = EstadoDoTrecho.TetoDeItens, bool comConsultados = true,
        string? resumo = null, IReadOnlyList<string>? frases = null, IReadOnlyList<string>? dinamica = null)
    {
        var texto = new StringBuilder();
        texto.Append(titulo).Append('\n');

        // Resumo e frases logo depois do título: situam a conversa antes dos registros literais.
        if (!string.IsNullOrWhiteSpace(resumo))
            texto.Append("Resumo: ").Append(resumo.Trim()).Append('\n');

        if (frases is { Count: > 0 })
        {
            texto.Append("Frases memoráveis:\n");
            foreach (var frase in frases) texto.Append("- ").Append(frase.Trim()).Append('\n');
        }

        // Só no ato: como a relação mudou ao longo do arco.
        if (dinamica is { Count: > 0 })
        {
            texto.Append("Dinâmica entre agente e usuário:\n");
            foreach (var linha in dinamica) texto.Append("- ").Append(linha.Trim()).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(objetivo))
            texto.Append("Objetivo: ").Append(objetivo.Trim()).Append('\n');

        texto.Append(Combinados.Render(combinado));

        if (estado != null)
        {
            var visivel = estado with
            {
                Itens = estado.Itens,
                Consultados = comConsultados ? estado.Consultados : Array.Empty<string>(),
                Consultas = comConsultados ? estado.Consultas : 0
            };
            texto.Append(RenderComTeto(visivel, fimDe, tetoDeItens));
        }

        if (aprendido is { Count: > 0 })
        {
            texto.Append("Aprendido:\n");
            foreach (var linha in aprendido) texto.Append("- ").Append(linha.Trim()).Append('\n');
        }

        return texto.ToString();
    }

    /// <summary>
    /// Cabe na cota cedendo do menos valioso para o mais: a linha Consultados, o começo da lista
    /// de estado, o Aprendido, os pedidos mais antigos do Combinado, e só então o Objetivo é
    /// aparado. Um bloco que não cabe inteiro nunca pode sumir: foi o que aconteceu quando o
    /// Combinado de um ato passou da cota sozinho, e a memória inteira saiu do prompt.
    /// <para>
    /// A primeira tentativa usa o mesmo <paramref name="tetoDeItens"/> do <see cref="Render"/>
    /// inteiro: sem isso, o bloco que cabe na cota sairia menor do que o bloco que foi medido.
    /// </para>
    /// </summary>
    public static string Fit(
        int maxTokens, TokenCounter counter,
        string titulo, string? objetivo, IReadOnlyList<FalaCombinada>? combinado,
        EstadoDoTrecho? estado, string fimDe, IReadOnlyList<string>? aprendido,
        int tetoDeItens = EstadoDoTrecho.TetoDeItens,
        string? resumo = null, IReadOnlyList<string>? frases = null, IReadOnlyList<string>? dinamica = null)
    {
        if (maxTokens <= 0) return "";

        int falas = combinado?.Count ?? 0;

        // As frases saem junto com o Aprendido; o resumo, só quando o Combinado já encolheu:
        // ele é o que conta a conversa, e o Combinado antigo vale menos que ele.
        foreach (var (teto, consultados, comAprendido, pedidos, comResumo) in new[]
                 {
                     (tetoDeItens, true, true, falas, true),
                     (tetoDeItens, false, true, falas, true),
                     (10, false, true, falas, true),
                     (5, false, true, falas, true),
                     (5, false, false, falas, true),
                     (5, false, false, Math.Min(falas, 4), true),
                     (5, false, false, Math.Min(falas, 2), true),
                     (5, false, false, Math.Min(falas, 2), false),
                     (3, false, false, 0, false),
                     (0, false, false, 0, false)
                 })
        {
            string bloco = Render(titulo, objetivo, combinado?.TakeLast(pedidos).ToList(), estado, fimDe,
                comAprendido ? aprendido : null, teto, consultados,
                comResumo ? resumo : null, comAprendido ? frases : null, comAprendido ? dinamica : null);
            if (counter.CountText(bloco) <= maxTokens) return bloco;
        }

        string minimo = Render(titulo, objetivo, null, estado, fimDe, null, 0, false);
        return string.IsNullOrWhiteSpace(objetivo)
            ? ""
            : MemoryRender.Fit(minimo, objetivo.Trim(), maxTokens, counter);
    }
    private static string RenderComTeto(EstadoDoTrecho estado, string fimDe, int teto)
    {
        if (teto >= estado.Itens.Count) return estado.Render(fimDe, teto);
        if (teto <= 0) return "";

        // Os mais recentes ficam: são os que descrevem o fim do trecho.
        var cortado = estado with { Itens = estado.Itens.TakeLast(teto).ToList() };
        int fora = estado.Itens.Count - teto;
        return ReplaceFirst(cortado.Render(fimDe, teto), ":\n", $":\n- ({fora} item(ns) mais antigo(s) omitido(s))\n");
    }

    private static string ReplaceFirst(string texto, string de, string para)
    {
        int i = texto.IndexOf(de, StringComparison.Ordinal);
        return i < 0 ? texto : texto[..i] + para + texto[(i + de.Length)..];
    }
}
