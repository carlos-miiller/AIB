using System;
using System.IO;

namespace AIB.Services;

/// <summary>
/// As skills que vêm com o AIB: ficam no repositório em <c>AIBWindows/skills</c>, vão junto com o
/// executável e, a cada arranque, são espelhadas em <c>~/.AIB/skills/default</c>.
/// <para>
/// Espelhar, não semear: a pasta <c>default</c> é do AIB. O que estiver lá e não vier com o
/// programa é apagado, e o que foi mudado volta ao original. Caso que motivou: a IA da Ellen
/// reescreveu o script de uma skill que roda com o segredo do Bitrix — numa skill que vem com o
/// programa, a mudança some no próximo arranque em vez de virar a versão em uso.
/// </para>
/// <para>
/// As skills do próprio usuário continuam em <c>~/.AIB/skills/&lt;pasta&gt;</c>, intocadas.
/// </para>
/// </summary>
public static class SkillsPadrao
{
    /// <summary>Nome da pasta dentro de <c>~/.AIB/skills</c>.</summary>
    public const string Pasta = "default";

    /// <summary>
    /// A pasta <c>skills</c> que acompanha o executável. As outras candidatas cobrem rodar da
    /// árvore de build. Null quando não há nenhuma.
    /// </summary>
    public static string? Origem()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidatas =
        {
            Path.Combine(baseDir, "skills"),
            Path.Combine(baseDir, "..", "..", "..", "skills"),
            Path.Combine(baseDir, "..", "..", "..", "..", "skills")
        };

        foreach (var c in candidatas)
            if (Directory.Exists(c)) return Path.GetFullPath(c);

        return null;
    }

    /// <summary>
    /// Deixa <paramref name="destino"/> igual a <paramref name="origem"/>: cada subpasta da
    /// origem é copiada de novo, e o que sobrar no destino é apagado. Sem origem, não mexe em
    /// nada — um AIB instalado sem a pasta não pode apagar as skills de quem já as tinha.
    /// Falha de um item não impede os outros.
    /// </summary>
    public static void Sincronizar(string? origem, string destino)
    {
        if (origem == null || !Directory.Exists(origem)) return;

        try
        {
            // Se o próprio destino virou link (junção), apagar o conteúdo apagaria o alvo do
            // link, que pode ser qualquer pasta do usuário. Tira-se o link, não o que ele aponta.
            if (Directory.Exists(destino)
                && new DirectoryInfo(destino).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(destino);

            Directory.CreateDirectory(destino);

            foreach (var arquivo in Directory.GetFiles(destino))
                Tentar(() => File.Delete(arquivo), arquivo);

            // Directory.Delete recursivo não segue junção dentro da pasta: apaga o link só.
            foreach (var pasta in Directory.GetDirectories(destino))
                Tentar(() => Directory.Delete(pasta, true), pasta);

            foreach (var skill in Directory.GetDirectories(origem))
                Tentar(() => Copiar(skill, Path.Combine(destino, Path.GetFileName(skill))), skill);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SKILLS] Falha ao espelhar as skills do AIB: {ex.Message}");
        }
    }

    private static void Tentar(Action acao, string caminho)
    {
        try { acao(); }
        catch (Exception ex) { Console.WriteLine($"[SKILLS] '{caminho}': {ex.Message}"); }
    }

    private static void Copiar(string origem, string destino)
    {
        Directory.CreateDirectory(destino);
        foreach (var arquivo in Directory.GetFiles(origem))
            File.Copy(arquivo, Path.Combine(destino, Path.GetFileName(arquivo)), true);
        foreach (var pasta in Directory.GetDirectories(origem))
            Copiar(pasta, Path.Combine(destino, Path.GetFileName(pasta)));
    }
}
