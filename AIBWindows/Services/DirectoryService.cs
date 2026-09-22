using System;
using System.IO;

namespace AIB.Services;

public static class DirectoryService
{
    private static string _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".AIB");

    public static string DataDir => _dataDir;

    // Subdiretórios de Dados
    public static string SkillsDir => Path.Combine(DataDir, "skills");
    public static string MemoryDir => Path.Combine(DataDir, "memory");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string CharactersDir => Path.Combine(DataDir, "character");

    // Arquivos específicos
    public static string SettingsPath => Path.Combine(DataDir, "profile.dat");

    public static void EnsureDirectories()
    {
        try
        {
            // Migração do local antigo (AppData) para o novo (.AIB)
            string oldDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIB");
            if (Directory.Exists(oldDataDir) && !Directory.Exists(DataDir))
            {
                CopyDirectory(oldDataDir, DataDir);
            }

            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(SkillsDir);
            Directory.CreateDirectory(MemoryDir);
            Directory.CreateDirectory(LogsDir);

            Directory.CreateDirectory(CharactersDir);
            SeedMissingCharacters();

            // Não há pasta temporária: screenshots, ocr_cache e cmd_output em %TEMP%\AIB eram
            // criadas a cada arranque e nada gravava nelas.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao garantir diretórios: {ex.Message}");
        }
    }

    /// <summary>
    /// Pasta de personagens que acompanha a instalação. É o failsafe read-only: a autoridade
    /// é sempre <see cref="CharactersDir"/>, dentro de .AIB. Devolve null quando não existe.
    /// </summary>
    public static string? FailsafeCharactersDir()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // Em produção só a primeira vale (o .csproj copia character\** para junto do exe).
        // As demais cobrem execução a partir da árvore de build local.
        string[] candidates =
        {
            Path.Combine(baseDir, "character"),
            Path.Combine(baseDir, "..", "..", "..", "character"),
            Path.Combine(baseDir, "..", "..", "..", "..", "character")
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>
    /// Semeia em .AIB apenas os personagens que ainda não existem lá.
    /// <para>
    /// A versão anterior testava a pasta inteira (<c>if (!Directory.Exists(CharactersDir))</c>),
    /// o que copiava tudo uma única vez e nunca mais: um personagem novo lançado numa versão
    /// posterior jamais chegava a quem já tinha o app instalado.
    /// </para>
    /// <para>
    /// Item a item, e NUNCA sobrescrevendo: .AIB é a fonte de verdade, então um SOUL.MD editado
    /// pelo usuário não pode ser atropelado por atualização.
    /// </para>
    /// </summary>
    private static void SeedMissingCharacters()
    {
        string? failsafe = FailsafeCharactersDir();
        if (failsafe == null)
        {
            Console.WriteLine("[CHARACTERS] Pasta failsafe não encontrada — nada a semear.");
            return;
        }

        foreach (var sourceCharacter in Directory.GetDirectories(failsafe))
        {
            string name = Path.GetFileName(sourceCharacter);
            string target = Path.Combine(CharactersDir, name);

            if (Directory.Exists(target)) continue; // já existe: a versão do usuário manda.

            CopyDirectory(sourceCharacter, target);
            Console.WriteLine($"[CHARACTERS] Personagem '{name}' semeado a partir do failsafe.");
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
        }
    }

    public static void ApplyFromSettings(UserAppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DataDirectory))
            _dataDir = settings.DataDirectory;

        EnsureDirectories();
    }
}
