using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIB.Services.Memory;

/// <summary>
/// Confere, por código, se o que o resumidor escreveu tem origem no trecho.
/// <para>
/// Medido no TofuEval (NAACL 2024): 14 a 36% das frases de resumos de diálogo feitos por LLM têm
/// inconsistência factual, com qualquer tamanho de modelo — e nem modelos grandes julgam bem a
/// fidelidade de outro. Uma segunda chamada pedindo ao modelo que revise o próprio resumo, como o
/// Gemini CLI faz, custaria minutos num modelo local. Esta conferência é barata e cega ao
/// sentido: só verifica que cada VALOR citado (número, horário, caminho, nome de arquivo, texto
/// entre aspas) aparece no material. Não pega uma causa inventada em palavras comuns — pega a
/// causa inventada que cita um arquivo ou número que não existe.
/// </para>
/// </summary>
public static class Conferencia
{
    private static readonly Regex Literal = new(
        @"[A-Za-z]:\\[^\s""'`,;)]+"          // caminho absoluto
        + @"|""[^""]{3,80}""|“[^”]{3,80}”"    // texto entre aspas
        + @"|\b[\w\-]+\.[A-Za-z][A-Za-z0-9]{1,4}\b" // nome de arquivo com extensão
        + @"|\b\d{1,2}:\d{2}\b"               // horário
        + @"|\b\d{2,}(?:[.,]\d+)?\b",         // número com dois ou mais dígitos
        RegexOptions.Compiled);

    /// <summary>Os valores citados no texto.</summary>
    public static IReadOnlyList<string> Literais(string? texto) =>
        string.IsNullOrEmpty(texto)
            ? Array.Empty<string>()
            : Literal.Matches(texto).Select(m => m.Value.Trim('"', '“', '”').TrimEnd('.', ':')).Where(v => v.Length > 0).Distinct().ToList();

    /// <summary>Os valores do texto que NÃO aparecem na fonte.</summary>
    public static IReadOnlyList<string> SemOrigem(string? texto, string fonte) =>
        Literais(texto).Where(v => fonte.IndexOf(v, StringComparison.OrdinalIgnoreCase) < 0).ToList();

    /// <summary>Se todos os valores citados existem na fonte.</summary>
    public static bool Confere(string? texto, string fonte) => SemOrigem(texto, fonte).Count == 0;
}
