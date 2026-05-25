using System;
using System.IO;

namespace AIB.Services;

public static class DirectoryService
{
    private static string _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".AIB");
    private static string _tempDir = Path.Combine(Path.GetTempPath(), "AIB");

    public static string DataDir => _dataDir;
    public static string TempDir => _tempDir;

    // Subdiretórios de Dados
    public static string SkillsDir => Path.Combine(DataDir, "skills");
    public static string MemoryDir => Path.Combine(DataDir, "memory");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    // Subdiretórios Temporários
    public static string ScreenshotCacheDir => Path.Combine(TempDir, "screenshots");
    public static string OcrCacheDir => Path.Combine(TempDir, "ocr_cache");
    public static string CmdOutputDir => Path.Combine(TempDir, "cmd_output");

    // Arquivos específicos
    public static string SettingsPath => Path.Combine(DataDir, "profile.dat");
    public static string MemoryPath => Path.Combine(DataDir, "memory.json");

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

            Directory.CreateDirectory(TempDir);
            Directory.CreateDirectory(ScreenshotCacheDir);
            Directory.CreateDirectory(OcrCacheDir);
            Directory.CreateDirectory(CmdOutputDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao garantir diretórios: {ex.Message}");
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
        
        if (!string.IsNullOrWhiteSpace(settings.TempDirectory))
            _tempDir = settings.TempDirectory;

        EnsureDirectories();
    }
}
