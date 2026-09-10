using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AIB.Services;

/// <summary>
/// Floor list de comandos destrutivos para <c>shell</c>. Roda APÓS o
/// modal (D-04) e refuta apenas quando <c>userLevel &lt; 7</c> e
/// <c>ConfirmDangerousCommands == ON</c>. Em L&gt;=7 ou com a flag OFF,
/// o modal é a autoridade única (D-01, herda Phase 1 D5/D8).
///
/// Pipeline (D-03):
///   1. Lowercase.
///   2. Colapsa concat de strings PowerShell: <c>"x" + "y"</c> -> <c>xy</c>.
///   3. Expande aliases destrutivos: <c>mv</c> -> <c>move-item</c>, etc.
///   4. <c>-EncodedCommand</c> -> recusa imediata (não-decodável aqui).
///   5. Regex com <c>\b</c> word boundaries; first match wins.
///
/// Este é um portão BEST-EFFORT. O modal é o gate canônico per Phase 1 D5
/// (sempre dispara, independente do nível). Documentado em SEGURANCA.MD.
/// </summary>
public static class CommandFloorList
{
    // Quatro categorias D-02 — recursive-delete (3 entries), format (1), shutdown (1),
    // registry-destructive (1). Total: 6 entries. Todas com PT-BR reason prefixada
    // com "ACESSO NEGADO (FLOOR):" (substitui SANDBOX -> FLOOR per CONTEXT.md).
    private static readonly (Regex Pattern, string Category, string Reason)[] _entries = new[]
    {
        // Recursive deletion
        (new Regex(@"\brm\s+(-rf|-r\s+-f|-f\s+-r|-r)\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "recursive-delete",
         "ACESSO NEGADO (FLOOR): deleção recursiva (rm -r/-rf) — requer Nível 7."),
        (new Regex(@"\bdel\s+/s\b|\bdel\s+/f\s+/s\b|\brmdir\s+/s\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "recursive-delete",
         "ACESSO NEGADO (FLOOR): deleção recursiva (del /s, rmdir /s) — requer Nível 7."),
        // \b antes de hifen NUNCA casa: entre um espaco e um '-' os dois lados sao
        // nao-palavra, entao nao existe word boundary ali. Com "\b-recurse\b" esta entrada
        // jamais disparou, e Remove-Item -Recurse e a forma nativa mais provavel no Windows.
        (new Regex(@"\bremove-item\b.*(?<![\w-])-recurse\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "recursive-delete",
         "ACESSO NEGADO (FLOOR): deleção recursiva (Remove-Item -Recurse) — requer Nível 7."),
        // Format / partition
        // O "\bformat\b" sozinho barrava Format-Table, Format-List e Format-Hex: o hifen
        // e caractere nao-palavra, entao existe fronteira logo depois de "format" e a palavra
        // casava dentro do nome do cmdlet. Aconteceu em uso real — uma busca por um nome numa
        // planilha foi recusada com "formatacao/particao de disco", e o modelo passou os
        // turnos seguintes tentando contornar uma permissao que nunca esteve em jogo.
        //
        // Uma recusa que mente sobre o motivo e pior que uma recusa: manda o agente procurar
        // solucao no lugar errado.
        //
        // A negativa (?!\s*-\w) deixa passar os verbos-substantivo de formatacao de texto, e
        // os cmdlets de disco que de fato destroem entram um a um, pelo nome. A cobertura
        // ficou MAIOR que a anterior: Clear-Disk, Initialize-Disk e as operacoes de particao
        // nao eram alcancadas.
        (new Regex(@"\bformat\b(?!\s*-\w)|\bformat-volume\b|\bclear-disk\b"
                   + @"|\binitialize-disk\b|\b(new|set|remove|resize)-partition\b"
                   + @"|\bdiskpart\b|\bwmic\s+logicaldisk\b|\bcipher\s+/w\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "format",
         "ACESSO NEGADO (FLOOR): formatação/partição de disco — requer Nível 7."),
        // Shutdown / reboot / logoff
        (new Regex(@"\bshutdown\b|\brestart-computer\b|\bstop-computer\b|\blogoff\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "shutdown",
         "ACESSO NEGADO (FLOOR): desligamento/reboot/logoff — requer Nível 7."),
        // Registry destructive
        (new Regex(@"\breg\s+delete\b|\bremove-itemproperty\b.*(?<![\w-])-path\s+hk|\bremove-item\b.*(?<![\w-])-path\s+hk",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "registry-destructive",
         "ACESSO NEGADO (FLOOR): operação destrutiva no registro do Windows — requer Nível 7."),
    };

    // Aliases canonicalizados em lowercase porque o pipeline (step 1) lowercase'a
    // o input ANTES da alias expansion. Os regexes D-02 buscam "remove-item" etc.
    // em lowercase para casar pós-expansion.
    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mv"]  = "move-item",
        ["ri"]  = "remove-item",
        ["ni"]  = "new-item",
        ["sc"]  = "set-content",
        ["ac"]  = "add-content",
        ["gci"] = "get-childitem",
    };

    private static readonly Regex _concatPattern =
        new(@"[""']\s*\+\s*[""']", RegexOptions.Compiled);
    private static readonly Regex _aliasPattern =
        new(@"\b(mv|ri|ni|sc|ac|gci)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _encodedCmdPattern =
        new(@"-encodedcommand\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Retorna <c>(Hit: true, Reason)</c> se o comando bate em uma entrada do floor.
    /// Em <c>userLevel &gt;= 7</c> sempre retorna <c>(false, null)</c> (D-01: floor inativo).
    /// </summary>
    public static (bool Hit, string? Reason) Match(string command, int userLevel)
    {
        // D-01: floor inativo em L>=7 — o modal é a autoridade única.
        if (userLevel >= 7) return (false, null);

        // Step 1: lowercase para todo o pipeline.
        string normalized = command.ToLowerInvariant();

        // Step 2: colapsa concat de strings PowerShell ("Remove" + "-Item" -> "Remove-Item").
        normalized = _concatPattern.Replace(normalized, "");

        // Step 3: expande aliases destrutivos (mv, ri, ni, sc, ac, gci) para canonical cmdlets.
        normalized = _aliasPattern.Replace(normalized, m => _aliases[m.Value]);

        // Step 4: -EncodedCommand recusa imediata em L<7 (não-decodável aqui).
        if (_encodedCmdPattern.IsMatch(normalized))
            return (true, "ACESSO NEGADO (FLOOR): powershell -EncodedCommand não é avaliável — requer Nível 7.");

        // Step 5: floor regex match (first match wins).
        foreach (var (pattern, _, reason) in _entries)
            if (pattern.IsMatch(normalized)) return (true, reason);

        return (false, null);
    }
}
