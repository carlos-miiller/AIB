using System;
using System.Text;

namespace AIB.Services.Memory;

/// <summary>
/// Remove blocos <c>&lt;think&gt;…&lt;/think&gt;</c> INTEIROS, conteúdo incluído.
/// <para>
/// Diferente do <see cref="Ai.ChatTemplateSanitizer"/>, que apaga só as tags órfãs para não
/// engolir texto. Aqui o alvo é outro: o histórico guarda o raciocínio de propósito, mas o
/// resumidor não deve vê-lo. Raciocínio é rascunho — muitas vezes contém hipóteses que o
/// próprio modelo descartou na linha seguinte, e resumir rascunho descartado produz capítulo
/// que afirma o contrário do que aconteceu.
/// </para>
/// </summary>
public static class ThinkBlockStripper
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOf(Open, StringComparison.OrdinalIgnoreCase) < 0) return text;

        var saida = new StringBuilder(text.Length);
        int cursor = 0;

        while (cursor < text.Length)
        {
            int abre = text.IndexOf(Open, cursor, StringComparison.OrdinalIgnoreCase);
            if (abre < 0)
            {
                saida.Append(text, cursor, text.Length - cursor);
                break;
            }

            saida.Append(text, cursor, abre - cursor);

            int fecha = text.IndexOf(Close, abre + Open.Length, StringComparison.OrdinalIgnoreCase);

            // Bloco aberto e nunca fechado: descarta até o fim. O modelo que abre <think> e não
            // fecha está raciocinando até o último token — não há resposta ali para preservar.
            if (fecha < 0) break;

            cursor = fecha + Close.Length;
        }

        return saida.ToString().Trim();
    }
}
