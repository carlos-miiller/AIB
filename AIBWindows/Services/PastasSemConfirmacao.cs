using System;
using System.Collections.Generic;
using System.IO;

namespace AIB.Services;

/// <summary>
/// Onde gravar e editar não param para perguntar.
/// <para>
/// Antes esta lista era o contrário: confinamento. Uma pasta preenchida BARRAVA todo o resto do
/// disco, e a recusa vinha antes do card — o usuário pedia uma gravação na pasta ao lado e recebia
/// um erro sem nunca ver a pergunta. O rótulo prometia "pastas onde a gravação é permitida", quem
/// lia entendia "pastas que não pedem confirmação", e o desencontro custou turnos de conversa com
/// o modelo tentando caminhos alternativos.
/// </para>
/// <para>
/// Agora a lista só DISPENSA o portão: dentro dela, <c>write</c> e <c>edit</c> executam direto;
/// fora dela, o card de confirmação aparece como sempre apareceu. Nada é recusado por causa desta
/// lista. Vazia — o padrão — tudo pergunta.
/// </para>
/// <para>
/// LIMITE conhecido: dispensar vale para <c>write</c> e <c>edit</c>, e NUNCA para <c>shell</c>.
/// Um comando não tem alvo declarado: ele descobre o que vai tocar enquanto roda. O que o shell
/// ganhou foi o aviso do card quando parece escrever fora destas pastas — ver
/// <see cref="Tools.EscritaNoComando"/>.
/// </para>
/// </summary>
public static class PastasSemConfirmacao
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
    /// Se este caminho está numa das pastas dispensadas. Lista vazia devolve false: dispensar
    /// tem de ser uma escolha escrita, nunca um efeito colateral de campo em branco.
    /// <para>
    /// Qualquer dúvida responde false. Um erro aqui vira "pergunta ao usuário", que é o lado
    /// seguro de errar.
    /// </para>
    /// </summary>
    public static bool Dispensa(string? caminho) => Dispensa(caminho, _raizes);

    public static bool Dispensa(string? caminho, string? bruto)
    {
        var raizes = Analisar(bruto);
        if (raizes.Count == 0) return false;
        if (string.IsNullOrWhiteSpace(caminho)) return false;

        string cheio;
        try { cheio = Concreto(Path.GetFullPath(caminho)); }
        catch { return false; }

        foreach (string raiz in raizes)
            if (Dentro(cheio, Concreto(raiz)))
                return true;

        return false;
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
    /// DENTRO de uma pasta dispensada, apontando para fora, faria a gravação sair da lista sem
    /// perguntar. Resolver nível a nível fecha isso. Qualquer erro devolve o caminho como veio —
    /// e um caminho que não casa é um caminho que pergunta.
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
