using System;
using System.Text.RegularExpressions;

namespace AIB.Services.Memory;

/// <summary>
/// Duas perguntas sobre um comando de shell, feitas no mesmo lugar: <b>ele só olha?</b> e
/// <b>dois comandos são o mesmo trabalho?</b>
/// <para>
/// As duas nasceram de uma sessão real. O Estado de um capítulo ficou tomado por linhas como
/// <c>docker exec glpi sed -n '400,435p' Plugin.php</c> — leitura pura, tratada como ação no
/// disco porque a lista de verbos não enxergava através do <c>docker exec</c>. E o Pendente
/// listava oito comandos "que falharam e não deram certo depois", todos já resolvidos: a falha só
/// era dada como resolvida quando um comando de texto IDÊNTICO tinha sucesso, e bastava o modelo
/// acrescentar um <c>| Select-Object -Last 10</c> na segunda tentativa para a pendência ficar viva
/// para sempre.
/// </para>
/// </summary>
public static class ComandoDeShell
{
    /// <summary>
    /// Verbos que só leem. Vale para o comando já desembrulhado por <see cref="Nucleo"/>, então
    /// <c>docker exec x grep …</c> chega aqui como <c>grep …</c>.
    /// </summary>
    private static readonly Regex Leitura = new(
        @"^\s*(dir|ls|gci|get-childitem|get-content|gc|cat|type|select-string|sls|test-path"
        + @"|get-item|gi|get-itemproperty|get-location|pwd|resolve-path|get-date|get-process|ps"
        + @"|get-service|where|where\.exe|echo|write-output|write-host|get-command|gcm|hostname"
        + @"|whoami|ipconfig|systeminfo|tree|findstr|measure-object|get-filehash|get-acl"
        + @"|sed|grep|egrep|awk|head|tail|wc|stat|file|du|df|uname|env|printenv|readlink|realpath"
        + @"|diff|md5sum|sha256sum|basename|dirname)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Pares "programa subcomando" que só leem. <c>git status</c> lê; <c>git push</c> não, e um
    /// verbo solto não separa os dois.
    /// </summary>
    private static readonly Regex LeituraComSubcomando = new(
        @"^\s*(git\s+(status|log|diff|show|branch|remote|rev-parse|describe|blame|ls-files)"
        + @"|docker\s+(ps|logs|images|inspect|version|info|port|top|stats)"
        + @"|docker\s+compose\s+(ps|logs|config|top|version)"
        + @"|kubectl\s+(get|describe|logs)"
        + @"|npm\s+(ls|list|view|outdated)"
        + @"|dotnet\s+(--version|--info|--list-sdks|--list-runtimes)"
        + @"|php\s+-l|python\s+--version|node\s+--version)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Embrulhos que não dizem nada sobre o que o comando faz: o que importa vem depois.</summary>
    private static readonly Regex Embrulho = new(
        @"^\s*(?:cd\s+[^;&|]+[;&]+\s*"
        + @"|sudo\s+|&\s*|call\s+"
        + @"|docker\s+exec\s+(?:-\w+\s+)*[^\s]+\s+(?:sh|bash|pwsh|powershell)\s+-c\s+[""']?"
        + @"|docker\s+exec\s+(?:-\w+\s+)*[^\s]+\s+"
        + @"|(?:sh|bash|pwsh|powershell)\s+-c\s+[""']?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>O que sobra do comando depois de tirar o cano, o redirecionamento e os embrulhos.</summary>
    public static string Nucleo(string? comando)
    {
        string t = Regex.Replace(comando ?? "", @"\s+", " ").Trim();
        if (t.Length == 0) return "";

        // Cano e redirecionamento são apresentação do resultado, não trabalho: um comando com
        // "| Select-Object -Last 10" é o mesmo comando da tentativa anterior sem ele. O cano sai
        // primeiro; o redirecionamento depois, e mais de uma vez — "2>&1 > saida.txt" é comum.
        int cano = t.IndexOf('|');
        if (cano > 0) t = t[..cano].Trim();

        for (int i = 0; i < 3; i++)
        {
            string antes = t;
            t = Regex.Replace(t, @"\s*\d*>{1,2}&?\d*(?:\s*[^\s>|]+)?\s*$", "").Trim();
            if (t == antes) break;
        }

        // Vários embrulhos podem estar empilhados: cd X; docker exec c sh -c "grep …".
        for (int i = 0; i < 4; i++)
        {
            var m = Embrulho.Match(t);
            if (!m.Success || m.Length == 0) break;
            t = t[m.Length..].Trim();
        }

        return t.Trim().Trim('"', '\'', ';').Trim();
    }

    /// <summary>
    /// Se o comando só lê o disco ou o estado, sem mudar nada. Um comando assim que falha não
    /// deixa ponta solta: procurar e não achar é rotina, e listá-lo como pendência convida o
    /// modelo a tentar de novo.
    /// </summary>
    public static bool SoLeitura(string? comando)
    {
        string nucleo = Nucleo(comando);
        if (nucleo.Length == 0) return false;

        return LeituraComSubcomando.IsMatch(nucleo) || Leitura.IsMatch(nucleo);
    }

    /// <summary>
    /// A chave de "mesmo trabalho": o núcleo do comando, em caixa baixa. Serve para casar a
    /// tentativa que falhou com a que deu certo depois, mesmo quando o modelo reescreveu o fim.
    /// Núcleo vazio devolve o comando inteiro — chave larga demais apagaria pendência alheia.
    /// </summary>
    public static string Assinatura(string? comando)
    {
        string nucleo = Nucleo(comando);
        return (nucleo.Length > 0 ? nucleo : (comando ?? "").Trim()).ToLowerInvariant();
    }
}
