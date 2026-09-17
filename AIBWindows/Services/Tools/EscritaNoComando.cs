using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIB.Services.Tools;

/// <summary>
/// Olha um comando de shell e avisa quando ele parece gravar FORA das pastas dispensadas de
/// confirmação.
/// <para>
/// Existe por um caso real: o modelo teve a gravação de um arquivo barrada, e um minuto depois
/// criou a pasta que queria com <c>New-Item</c> pelo shell. A lista de pastas nunca alcançou o
/// shell, e nem tem como alcançar — um comando não declara alvo, ele descobre enquanto roda.
/// </para>
/// <para>
/// O que dá para fazer honestamente é AVISAR: o card diz, em destaque, quais caminhos citados
/// no comando estão fora das pastas que o usuário marcou como de confiança. Quem decide continua
/// sendo ele, com um dado a mais na frente.
/// </para>
/// <para>
/// É um detector BEST-EFFORT, e a ajuda na tela diz isso. Ele lê texto: caminho montado em
/// variável, vindo de um pipe ou escrito com barra invertida escapada passa batido. Por isso o
/// silêncio dele NUNCA é promessa de que nada será tocado — é só ausência de sinal.
/// </para>
/// </summary>
public static class EscritaNoComando
{
    /// <summary>
    /// Verbos que criam, mudam ou apagam. Alias curto entra junto do nome completo: quem digita
    /// <c>ni</c> e quem digita <c>New-Item</c> faz a mesma coisa.
    /// </summary>
    private static readonly Regex Verbos = new(
        @"(?<![\w-])(new-item|set-content|add-content|out-file|remove-item|move-item|copy-item"
        + @"|rename-item|new-itemproperty|set-itemproperty|clear-content|export-csv|export-clixml"
        + @"|compress-archive|expand-archive|mkdir|rmdir|rd|md|del|erase|copy|xcopy|robocopy|move"
        + @"|\bni\b|\bsc\b|\bac\b|\bri\b|\bmi\b|\bcpi\b|\brni\b|\brm\b|\bcp\b|\bmv\b|\btee\b)(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Redirecionamento também grava, e não tem verbo nenhum: <c>... > x.txt</c>.</summary>
    private static readonly Regex Redirecionamento = new(@"(?<![0-9>])>{1,2}(?!&)", RegexOptions.Compiled);

    /// <summary>
    /// Caminho absoluto citado no texto: <c>C:\algo</c> ou <c>\\servidor\pasta</c>. Para no que
    /// costuma terminar um caminho em linha de comando — aspa, pipe, ponto e vírgula, espaço.
    /// </summary>
    private static readonly Regex Caminhos = new(
        @"(?:[A-Za-z]:\\|\\\\)[^""'|;,)\r\n]*",
        RegexOptions.Compiled);

    /// <summary>
    /// O aviso para o card, ou <c>null</c> quando não há o que avisar: comando que não parece
    /// gravar, nenhum caminho absoluto no texto, ou todos os caminhos dentro das pastas
    /// dispensadas.
    /// <para>
    /// Com a lista VAZIA não avisa nada. Sem pasta de confiança escolhida, todo caminho estaria
    /// "fora" e o aviso apareceria em cima de qualquer comando — um alerta que aparece sempre é
    /// um alerta que ninguém lê.
    /// </para>
    /// </summary>
    public static string? Aviso(string? comando) => Aviso(comando, PastasSemConfirmacao.Configuradas);

    public static string? Aviso(string? comando, string? pastas)
    {
        if (string.IsNullOrWhiteSpace(comando)) return null;
        if (PastasSemConfirmacao.Analisar(pastas).Count == 0) return null;

        if (!Verbos.IsMatch(comando) && !Redirecionamento.IsMatch(comando)) return null;

        var fora = Fora(comando, pastas);
        if (fora.Count == 0) return null;

        return "Este comando cita caminho fora das pastas sem confirmação: "
               + string.Join(" | ", fora)
               + ". O shell não é limitado por essa lista — confira o alvo antes de permitir.";
    }

    /// <summary>Os caminhos absolutos citados que estão fora das pastas dispensadas, sem repetir.</summary>
    public static IReadOnlyList<string> Fora(string? comando, string? pastas)
    {
        var saida = new List<string>();

        foreach (Match m in Caminhos.Matches(comando ?? ""))
        {
            string bruto = m.Value.Trim().TrimEnd('\\', '"', '\'', '.', ',', ')');
            if (bruto.Length < 4) continue;

            if (PastasSemConfirmacao.Dispensa(bruto, pastas)) continue;

            if (!saida.Any(x => string.Equals(x, bruto, StringComparison.OrdinalIgnoreCase)))
                saida.Add(bruto);
        }

        return saida;
    }
}
