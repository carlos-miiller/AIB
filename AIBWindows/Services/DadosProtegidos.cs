using System;
using System.IO;
using System.Text.Json;

namespace AIB.Services;

/// <summary>O que uma ferramenta de leitura pode fazer com um caminho.</summary>
public enum Acesso
{
    /// <summary>Lê sem perguntar.</summary>
    Livre,

    /// <summary>Lê só com o cartão de confirmação.</summary>
    Pergunta,

    /// <summary>Não lê.</summary>
    Negado
}

/// <summary>
/// A pasta de dados do AIB (<c>~/.AIB</c>) vista pelas ferramentas de LEITURA.
/// <para>
/// <c>read</c>, <c>grep</c> e <c>glob</c> não pedem confirmação, e nada as impedia de entrar
/// aqui: o modelo — ou um texto de terceiros que o convencesse — lia conversas antigas, a
/// auditoria e os arquivos de e-mail sem ninguém ver. Só a <c>fs</c> tinha barreira, e para
/// apagar.
/// </para>
/// <list type="bullet">
/// <item><description><b>Negado</b>: os cofres (<c>credentials/</c>), as configurações
/// (<c>profile.dat</c>) e o perfil do navegador, que guarda os logins. Não há pedido legítimo
/// do usuário que passe por ler isso.</description></item>
/// <item><description><b>Livre</b>: o que o modelo precisa para trabalhar — skills,
/// personagens e as anotações dos sites.</description></item>
/// <item><description><b>Pergunta</b>: todo o resto (memória, logs, e-mail).</description></item>
/// </list>
/// <para>
/// O <c>shell</c> continua alcançando tudo, e não há como impedir: ele já passa pelo cartão.
/// </para>
/// </summary>
public static class DadosProtegidos
{
    private static readonly string[] Negados = { "credentials", "profile.dat", @"navegador\perfil" };
    private static readonly string[] Livres = { "skills", ".default_skills", "character", @"navegador\notas" };

    /// <param name="raiz">A pasta de dados. Vazio é a do usuário — os ensaios passam uma temporária.</param>
    /// <param name="seguirAtalho">
    /// Resolve o destino quando o caminho é um link. Desligado nas varreduras, que conferem
    /// milhares de arquivos e não podem ir ao disco por cada um.
    /// </param>
    public static Acesso Leitura(string? caminho, string? raiz = null, bool seguirAtalho = true)
    {
        if (string.IsNullOrWhiteSpace(caminho)) return Acesso.Livre;

        try
        {
            string dados = Normal(string.IsNullOrWhiteSpace(raiz) ? DirectoryService.DataDir : raiz!);
            string alvo = Normal(caminho);

            Acesso acesso = Classificar(alvo, dados);
            if (acesso != Acesso.Livre || !seguirAtalho) return acesso;

            // Um atalho fora da pasta apontando para dentro dela leria o cofre pelo outro nome.
            FileSystemInfo info = Directory.Exists(alvo) ? new DirectoryInfo(alvo) : new FileInfo(alvo);
            string? destino = info.Exists ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName : null;

            return destino == null ? Acesso.Livre : Classificar(Normal(destino), dados);
        }
        catch
        {
            // Caminho que nem se consegue interpretar: a ferramenta vai falhar ao abrir.
            return Acesso.Livre;
        }
    }

    /// <summary>
    /// Se um arquivo achado por uma varredura (<c>grep</c>, <c>glob</c>) entra no resultado. O
    /// que é negado nunca entra. O que pede cartão só entra quando a própria raiz da busca
    /// pedia — aí o usuário viu o cartão e autorizou aquela pasta.
    /// </summary>
    public static bool NaVarredura(string arquivo, string raizDaBusca, string? raiz = null) =>
        Leitura(arquivo, raiz, seguirAtalho: false) switch
        {
            Acesso.Negado => false,
            Acesso.Pergunta => Leitura(raizDaBusca, raiz, seguirAtalho: false) == Acesso.Pergunta,
            _ => true
        };

    private static Acesso Classificar(string alvo, string dados)
    {
        if (string.Equals(alvo, dados, StringComparison.OrdinalIgnoreCase)) return Acesso.Pergunta;
        if (!alvo.StartsWith(dados + "\\", StringComparison.OrdinalIgnoreCase)) return Acesso.Livre;

        string dentro = alvo.Substring(dados.Length + 1);

        if (Array.Exists(Negados, n => Sob(dentro, n))) return Acesso.Negado;
        if (Array.Exists(Livres, l => Sob(dentro, l))) return Acesso.Livre;

        return Acesso.Pergunta;
    }

    private static bool Sob(string dentro, string pasta) =>
        string.Equals(dentro, pasta, StringComparison.OrdinalIgnoreCase)
        || dentro.StartsWith(pasta + "\\", StringComparison.OrdinalIgnoreCase);

    private static string Normal(string caminho) =>
        Path.GetFullPath(caminho).Replace('/', '\\').TrimEnd('\\');

    /// <summary>O campo <c>path</c> dos argumentos, ou o padrão da ferramenta quando ele não veio.</summary>
    public static string Caminho(string argumentsJson, string padrao = "")
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            string lido = Tools.PathArgumentRepair.Normalize(
                args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null);

            return lido.Length == 0 ? padrao : lido;
        }
        catch
        {
            return padrao;
        }
    }

    /// <summary>A recusa que vai ao modelo. Começa com ACESSO NEGADO, como toda negação do portão.</summary>
    public static string Recusa(string caminho) =>
        $"ACESSO NEGADO: '{caminho}' guarda os cofres, as configurações ou os logins do AIB, e não é "
        + "lido por ferramenta. Nada foi lido.";

    /// <summary>A recusa de quando a leitura pedia cartão e chegou sem ele.</summary>
    public static string SemCartao(string caminho) =>
        $"ACESSO NEGADO: ler '{caminho}', na pasta de dados do AIB, exige confirmação do usuário. Nada foi lido.";

    /// <summary>O que o cartão mostra. Nunca entra no "sempre permitir".</summary>
    public static CommandConfirmationContext Cartao(string ferramenta, string verbo, string caminho, int nivel) => new()
    {
        Tool = ferramenta,
        Command = $"{verbo} {caminho}",
        Level = nivel,
        SemSempre = true,
        Aviso = "Fica na pasta de dados do AIB: conversas antigas, registros e e-mail. "
                + "Autorize só se foi você quem pediu."
    };
}
