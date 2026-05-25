using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIB.Services;

public static class FileService
{
    private const int MAX_FILE_SIZE = 100 * 1024; // 100KB

    public static async Task<string> WriteFileAsync(string path, string content)
    {
        try
        {
            // Se o caminho não for absoluto, salva no diretório temporário
            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(DirectoryService.TempDir, path);
            }

            string? directory = Path.GetDirectoryName(path);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, content, Encoding.UTF8);
            var info = new FileInfo(path);
            return $"Arquivo escrito com sucesso em: {path} ({info.Length / 1024.0:F1} KB)";
        }
        catch (Exception ex)
        {
            return $"ERRO ao escrever arquivo: {ex.Message}";
        }
    }

    public static async Task<string> ReadFileForAIAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return "ERRO: Arquivo não encontrado.";

            var fileInfo = new FileInfo(path);
            
            // Verificação de binário simples
            byte[] sampleBytes = new byte[Math.Min(8192, (int)fileInfo.Length)];
            using (var fs = File.OpenRead(path))
            {
                await fs.ReadAsync(sampleBytes, 0, sampleBytes.Length);
            }
            if (sampleBytes.Any(b => b == 0)) return "ERRO: Arquivo binário detectado. Não é possível ler como texto.";

            if (fileInfo.Length > MAX_FILE_SIZE)
            {
                using var reader = new StreamReader(path, Encoding.UTF8, true);
                char[] buffer = new char[MAX_FILE_SIZE / 2];
                int read = await reader.ReadAsync(buffer, 0, buffer.Length);
                string partial = new string(buffer, 0, read);
                return $"[CONTEÚDO TRUNCADO - Primeiros {MAX_FILE_SIZE/1024}KB de {fileInfo.Length/1024}KB]:\n```\n{partial}\n```";
            }

            string content = await File.ReadAllTextAsync(path, Encoding.UTF8);
            return $"[CONTEÚDO DE {path}]:\n```\n{content}\n```";
        }
        catch (Exception ex)
        {
            return $"ERRO ao ler arquivo: {ex.Message}";
        }
    }

    public static string ListDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return "ERRO: Diretório não encontrado.";

            var sb = new StringBuilder();
            sb.AppendLine($"Conteúdo de: {path}\n");

            var dirs = Directory.GetDirectories(path);
            foreach (var d in dirs.OrderBy(x => x))
                sb.AppendLine($"  📁 {Path.GetFileName(d)}/");

            var files = Directory.GetFiles(path);
            foreach (var f in files.OrderBy(x => x))
            {
                var fi = new FileInfo(f);
                string size = fi.Length < 1024 ? $"{fi.Length}B" :
                              fi.Length < 1024 * 1024 ? $"{fi.Length / 1024}KB" :
                              $"{fi.Length / (1024 * 1024)}MB";
                sb.AppendLine($"  📄 {Path.GetFileName(f)} ({size})");
            }

            sb.AppendLine($"\nTotal: {dirs.Length} pastas, {files.Length} arquivos");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"ERRO ao listar diretório: {ex.Message}";
        }
    }
}
