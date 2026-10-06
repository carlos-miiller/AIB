using System;
using System.Collections.Generic;
using System.Text;
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

    /// <summary>
    /// Consulta de banco que só lê. O cliente (<c>mariadb</c>, <c>mysql</c>, <c>psql</c>) grava
    /// tanto quanto lê, então quem decide é a CONSULTA: só passa quando o <c>-e</c>/<c>-c</c>
    /// começa com SELECT, SHOW, DESCRIBE ou EXPLAIN.
    /// <para>
    /// Uma consulta destas ia para o Pendente como "falhou e não deu certo depois", com a linha
    /// inteira pronta para repetir — e ela só tinha falhado porque o PowerShell tratou a saída da
    /// tabela como erro. Ponto e vírgula dentro do <c>-e</c> é separador de consultas, e não
    /// corrente de shell: várias consultas de leitura continuam leitura.
    /// </para>
    /// </summary>
    private static readonly Regex ConsultaDeLeitura = new(
        @"^\s*(mariadb|mysql|psql)\b[^""']*[-/](?:e|c)\s*[""']\s*(select|show|describe|desc|explain)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Passos que não são o trabalho: entrar na pasta, esperar, limpar a tela. Numa corrente
    /// <c>A; B; C</c> eles são preparo, e o trabalho é o primeiro passo que sobra.
    /// </summary>
    private static readonly Regex Preparo = new(
        @"^\s*(cd|set-location|sl|chdir|pushd|popd|start-sleep|sleep|timeout|cls|clear|echo"
        + @"|write-host|set|export|chcp)\b",
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

        t = PrimeiroPasso(t);

        return t.Trim().Trim('"', '\'', ';').Trim();
    }

    /// <summary>
    /// Numa corrente <c>A; B; C</c>, o primeiro passo que não é preparo.
    /// <para>
    /// A falha é relatada para a corrente inteira, então a corrente que falhou nunca casava com
    /// a tentativa seguinte, mais curta. Visto numa sessão real:
    /// <c>cd X; docker compose up …; Start-Sleep -Seconds 20; docker compose logs …</c> estourou
    /// o prazo, o <c>docker compose up</c> sozinho funcionou logo depois, e a pendência
    /// sobreviveu à conversa inteira.
    /// </para>
    /// <para>
    /// Só corta o que está FORA de aspas: um <c>sh -c "a; b"</c> é um comando só, e cortar dentro
    /// das aspas inventaria um comando que ninguém rodou.
    /// </para>
    /// </summary>
    private static string PrimeiroPasso(string comando)
    {
        foreach (string passo in Passos(comando))
            if (passo.Length > 0 && !Preparo.IsMatch(passo))
                return passo;

        return comando;
    }

    private static IEnumerable<string> Passos(string comando)
    {
        var atual = new StringBuilder();
        char aspa = '\0';

        for (int i = 0; i < comando.Length; i++)
        {
            char c = comando[i];

            if (aspa != '\0')
            {
                if (c == aspa) aspa = '\0';
                atual.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                aspa = c;
                atual.Append(c);
                continue;
            }

            if (c == ';' || (c == '&' && i + 1 < comando.Length && comando[i + 1] == '&'))
            {
                if (c == '&') i++;
                yield return atual.ToString().Trim();
                atual.Clear();
                continue;
            }

            atual.Append(c);
        }

        yield return atual.ToString().Trim();
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

        return LeituraComSubcomando.IsMatch(nucleo)
               || ConsultaDeLeitura.IsMatch(nucleo)
               || Leitura.IsMatch(nucleo);
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
