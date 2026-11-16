using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIB.Services.Tools;

/// <summary>O que o cartão mostra de um alias usado no comando.</summary>
public sealed record UsoDoCofre(string Nome, string Descricao, IReadOnlyList<string> Hosts);

/// <summary>
/// O que um comando de shell faz com o <see cref="Cofre"/>: quais aliases usa e com que
/// endereços fala.
/// <para>
/// É leitura por TEXTO, como a floor list: pega o endereço escrito por extenso e recusa o que
/// é montado na hora (<c>"https://$h/"</c>), mas não entende o PowerShell. Por isso o comando com
/// alias ainda passa pelo cartão, sempre, sem "sempre permitir".
/// </para>
/// </summary>
public static class CofreNoComando
{
    /// <summary>Qualquer menção a uma variável do cofre: <c>$env:COFRE_X</c>, <c>${env:COFRE_X}</c>,
    /// <c>GetEnvironmentVariable('COFRE_X')</c>, <c>%COFRE_X%</c>. Na dúvida, conta como uso.</summary>
    private static readonly Regex Var = new(@"(?<![A-Za-z0-9_])COFRE_[A-Za-z0-9_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Um esquema seguido de <c>://</c>. O que vem depois decide se o host é literal.</summary>
    private static readonly Regex Esquema = new(@"(?<![A-Za-z0-9+.\-])[A-Za-z][A-Za-z0-9+.\-]*://",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HostLiteral = new(@"^(?:[^\s/""'`@$(){}]*@)?([A-Za-z0-9.\-]+)(?=$|[:/\s""'`?#)])",
        RegexOptions.CultureInvariant);

    /// <summary>As variáveis do cofre citadas, em maiúsculas, sem repetir.</summary>
    public static IReadOnlyList<string> Variaveis(string comando) =>
        Var.Matches(comando ?? "").Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();

    /// <summary>
    /// Os hosts escritos por extenso, em minúsculas, e se algum endereço é montado na hora
    /// (o host vem de variável, de concatenação ou de expressão).
    /// </summary>
    public static (IReadOnlyList<string> Hosts, bool Montado) Enderecos(string comando)
    {
        var hosts = new List<string>();
        bool montado = false;
        comando ??= "";

        foreach (Match m in Esquema.Matches(comando))
        {
            string resto = comando[(m.Index + m.Length)..];
            var h = HostLiteral.Match(resto);
            if (h.Success) hosts.Add(h.Groups[1].Value.ToLowerInvariant());
            else montado = true;
        }

        return (hosts.Distinct().ToList(), montado);
    }

    /// <summary>
    /// Os aliases que o comando usa e, quando não pode rodar, o porquê (vai ao modelo como
    /// resultado, antes do cartão). Sem alias nenhum: lista vazia e recusa nula.
    /// </summary>
    public static (IReadOnlyList<Alias> Usados, string? Recusa) Analisar(string comando, Cofre cofre)
    {
        var variaveis = Variaveis(comando);
        if (variaveis.Count == 0) return (Array.Empty<Alias>(), null);

        var todos = cofre.Listar();
        var usados = new List<Alias>();
        var faltam = new List<string>();
        foreach (string v in variaveis)
        {
            var a = todos.FirstOrDefault(x => string.Equals(x.Variavel, v, StringComparison.OrdinalIgnoreCase));
            if (a is null) faltam.Add(v); else usados.Add(a);
        }

        if (faltam.Count > 0)
        {
            string existem = todos.Count == 0
                ? "O cofre está vazio."
                : "Os que existem: " + string.Join("; ", todos.Select(a => $"$env:{a.Variavel} ({a.Descricao})")) + ".";
            return (usados, $"ERRO: não há {string.Join(", ", faltam)} no cofre. {existem} "
                            + "Se falta um, o usuário o guarda em Configurações › Cofre; não peça o valor no chat.");
        }

        // "Permite uso no shell" desligado: o alias é de uma habilidade, que só faz as operações
        // dela. No shell ele daria a API inteira.
        var presos = usados.Where(a => !a.Shell).ToList();
        if (presos.Count > 0)
            return (usados, $"ACESSO NEGADO: {string.Join(", ", presos.Select(a => $"'{a.Nome}'"))} não é liberado para o "
                            + "shell (Configurações › Cofre › \"Permite uso no shell\"). Use a habilidade que faz essa "
                            + "operação; se nenhuma faz, diga ao usuário. Nada foi executado.");

        var restritos = usados.Where(a => a.Hosts.Count > 0).ToList();
        if (restritos.Count == 0) return (usados, null);

        var (hosts, montado) = Enderecos(comando);
        string nomes = string.Join(", ", restritos.Select(a => $"'{a.Nome}'"));

        if (montado)
            return (usados, $"ACESSO NEGADO: o comando usa {nomes} do cofre e monta um endereço na hora "
                            + "(o host vem de variável ou de concatenação). Com alias do cofre, escreva cada "
                            + "endereço por extenso, ou use a própria variável como começo do endereço. Nada foi executado.");

        foreach (var a in restritos)
        {
            var fora = hosts.Where(h => !a.Hosts.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
            if (fora.Count == 0) continue;

            return (usados, $"ACESSO NEGADO: o alias '{a.Nome}' só pode ir para {string.Join(", ", a.Hosts)}; "
                            + $"o comando também acessa {string.Join(", ", fora)}. Nada foi executado. Não tente "
                            + "mandar o valor por outro caminho: se o usuário quiser esse destino, ele inclui o host "
                            + "no alias em Configurações › Cofre.");
        }

        return (usados, null);
    }
}
