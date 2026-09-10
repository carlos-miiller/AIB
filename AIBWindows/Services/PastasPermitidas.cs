using System;
using System.Collections.Generic;
using System.IO;

namespace AIB.Services;

/// <summary>
/// Onde as ferramentas de escrita têm permissão de encostar.
/// <para>
/// O <c>WriteFileTool</c> já dizia, num comentário, o que faltava: "sem confinamento de raiz, o
/// caminho resolvido é a única defesa que o usuário tem". O modal mostra o caminho absoluto, mas
/// mostrar não é impedir — quem clica "permitir" às pressas autoriza uma gravação em
/// <c>...\Start Menu\Programs\Startup</c> do mesmo jeito. Esta classe é a diferença entre
/// "autorizei uma edição" e "autorizei uma edição em qualquer lugar do disco".
/// </para>
/// <para>
/// Lista vazia é o comportamento de sempre: disco inteiro liberado. Confinar é uma escolha, e
/// ligá-la sozinha quebraria a instalação de quem já usa a AIB para mexer em projeto fora da
/// pasta do usuário.
/// </para>
/// <para>
/// LIMITE conhecido: isto vale para <c>write</c> e <c>edit</c>, e NÃO para <c>shell</c>. Um
/// <c>Set-Content</c> escreve onde quiser, e a defesa do shell continua sendo o portão de
/// confirmação mais a denylist. A ajuda na tela diz isso com todas as letras — prometer mais do
/// que se entrega é pior do que não prometer.
/// </para>
/// </summary>
public static class PastasPermitidas
{
    private static volatile string _raizes = "";

    /// <summary>O texto cru como o usuário digitou, uma pasta por linha.</summary>
    public static string Configuradas => _raizes;

    /// <summary>
    /// Ligado no <see cref="SettingsService"/>, e não em cada tela que salva: um único ponto de
    /// sincronia é o que impede o caso "mudei nas configurações e a ferramenta continuou com a
    /// lista velha".
    /// </summary>
    public static void Configurar(string? bruto) => _raizes = (bruto ?? "").Trim();

    /// <summary>Uma pasta por linha, normalizadas e sem repetição. Linha ilegível é ignorada.</summary>
    public static IReadOnlyList<string> Analisar(string? bruto)
    {
        var saida = new List<string>();

        foreach (string linha in (bruto ?? "").Replace("\r", "").Split('\n'))
        {
            string texto = linha.Trim().Trim('"');
            if (texto.Length == 0) continue;

            string cheio;
            try { cheio = Path.GetFullPath(texto); }
            catch { continue; }

            cheio = cheio.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (cheio.Length == 0) continue;

            if (!saida.Exists(x => string.Equals(x, cheio, StringComparison.OrdinalIgnoreCase)))
                saida.Add(cheio);
        }

        return saida;
    }

    /// <summary>
    /// Recusa em texto, ou <c>null</c> quando pode gravar. Sem raiz configurada, nunca recusa.
    /// <para>
    /// A recusa NOMEIA as pastas permitidas. Um "não pode" sem endereço faz o modelo chutar o
    /// caminho seguinte — foi a lição das quatro chamadas idênticas.
    /// </para>
    /// </summary>
    public static string? Barrar(string? caminho) => Barrar(caminho, _raizes);

    public static string? Barrar(string? caminho, string? bruto)
    {
        var raizes = Analisar(bruto);
        if (raizes.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(caminho)) return null;

        string cheio;
        try { cheio = Concreto(Path.GetFullPath(caminho)); }
        catch { return null; }

        foreach (string raiz in raizes)
            if (Dentro(cheio, Concreto(raiz)))
                return null;

        return $"ERRO: '{cheio}' está fora das pastas onde a gravação é permitida. "
               + "Escreva dentro de uma destas: " + string.Join(" | ", raizes)
               + ". Quem decide essa lista é o usuário, em Configurações › Ferramentas — "
               + "não tente contornar por outro caminho.";
    }

    /// <summary>Um caminho está dentro de uma raiz se é a própria raiz ou desce a partir dela.</summary>
    public static bool Dentro(string caminho, string raiz)
    {
        string alvo = caminho.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tronco = raiz.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (tronco.Length == 0) return false;
        if (string.Equals(alvo, tronco, StringComparison.OrdinalIgnoreCase)) return true;

        return alvo.StartsWith(tronco + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Segue junções e links simbólicos de cada trecho que já existe no disco.
    /// <para>
    /// <c>Path.GetFullPath</c> resolve <c>..</c> mas não olha reparse point: uma junção criada
    /// DENTRO da raiz permitida, apontando para fora, seria uma fuga silenciosa — a checagem de
    /// prefixo veria um caminho que começa certo e a gravação cairia noutro lugar. Resolver nível
    /// a nível fecha isso. Qualquer erro devolve o caminho como veio: um confinamento que estoura
    /// não pode virar um confinamento que libera.
    /// </para>
    /// </summary>
    public static string Concreto(string cheio)
    {
        try
        {
            string raiz = Path.GetPathRoot(cheio) ?? "";
            if (raiz.Length == 0) return cheio;

            string[] partes = cheio[raiz.Length..].Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            string atual = raiz;

            foreach (string parte in partes)
            {
                atual = Path.Combine(atual, parte);

                FileSystemInfo? destino =
                    Directory.Exists(atual) ? new DirectoryInfo(atual).ResolveLinkTarget(true) :
                    File.Exists(atual) ? new FileInfo(atual).ResolveLinkTarget(true) :
                    null;

                if (destino != null) atual = destino.FullName;
            }

            return Path.GetFullPath(atual)
                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return cheio;
        }
    }
}
