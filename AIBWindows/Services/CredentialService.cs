using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIB.Services;

public static class CredentialService
{
    private static string CredentialsDir => Path.Combine(DirectoryService.DataDir, "credentials");

    public static void EnsureDir()
    {
        if (!Directory.Exists(CredentialsDir))
            Directory.CreateDirectory(CredentialsDir);
    }

    public static async Task<string> StoreCredentialAsync(string system, string key, string value)
    {
        try
        {
            EnsureDir();
            string filePath = Path.Combine(CredentialsDir, $"{system.ToLower()}.bin");
            
            // Carrega mapa existente ou cria novo
            var creds = new Dictionary<string, string>();
            if (File.Exists(filePath))
            {
                var decryptedJson = DecryptFile(filePath);
                if (!string.IsNullOrEmpty(decryptedJson))
                    creds = JsonSerializer.Deserialize<Dictionary<string, string>>(decryptedJson) ?? new();
            }

            creds[key] = value;
            string json = JsonSerializer.Serialize(creds);
            
            // Criptografa usando Windows DPAPI (Current User)
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] encryptedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            
            await File.WriteAllBytesAsync(filePath, encryptedData);
            return $"Credencial '{key}' para o sistema '{system}' armazenada com criptografia de nível de sistema.";
        }
        catch (Exception ex)
        {
            return $"ERRO ao armazenar credencial: {ex.Message}";
        }
    }

    public static string RetrieveCredential(string system, string key)
    {
        try
        {
            EnsureDir();
            string filePath = Path.Combine(CredentialsDir, $"{system.ToLower()}.bin");
            Console.WriteLine($"[DEBUG-COFRE] Buscando sistema: {system} | Chave: {key}");
            Console.WriteLine($"[DEBUG-COFRE] Caminho do arquivo: {filePath}");

            if (!File.Exists(filePath)) 
            {
                Console.WriteLine($"[DEBUG-COFRE] Sistema '{system}' não encontrado. Tentando busca global...");
                // Busca global: Varre todos os sistemas para ver se a chave existe em algum lugar
                var allFiles = Directory.GetFiles(CredentialsDir, "*.bin");
                foreach (var file in allFiles)
                {
                    var data = DecryptFile(file);
                    if (string.IsNullOrEmpty(data)) continue;
                    var c = JsonSerializer.Deserialize<Dictionary<string, string>>(data);
                    if (c != null && c.TryGetValue(key, out string? val))
                    {
                        Console.WriteLine($"[DEBUG-COFRE] SUCESSO GLOBAL: Chave '{key}' encontrada no sistema '{Path.GetFileNameWithoutExtension(file)}'.");
                        return val;
                    }
                }
                Console.WriteLine($"[DEBUG-COFRE] ERRO: Chave '{key}' não encontrada em nenhum sistema.");
                return "ERRO: Credencial não encontrada em nenhum sistema.";
            }

            var decryptedJson = DecryptFile(filePath);
            if (string.IsNullOrEmpty(decryptedJson)) return "ERRO: Falha ao descriptografar arquivo.";

            var creds = JsonSerializer.Deserialize<Dictionary<string, string>>(decryptedJson);
            if (creds != null && creds.TryGetValue(key, out string? value))
            {
                Console.WriteLine($"[DEBUG-COFRE] SUCESSO: Chave '{key}' encontrada.");
                return value;
            }
            Console.WriteLine($"[DEBUG-COFRE] ERRO: Chave '{key}' não existe dentro do sistema '{system}'.");
            return "ERRO: Chave não encontrada para este sistema.";
        }
        catch (Exception ex)
        {
            return $"ERRO ao recuperar credencial: {ex.Message}";
        }
    }

    private static string DecryptFile(string path)
    {
        try
        {
            byte[] encryptedData = File.ReadAllBytes(path);
            byte[] decryptedData = ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedData);
        }
        catch { return ""; }
    }

    public static string ListSystems()
    {
        EnsureDir();
        var systems = Directory.GetFiles(CredentialsDir, "*.bin")
                               .Select(Path.GetFileNameWithoutExtension)
                               .ToList();
        
        return systems.Count > 0 
            ? "Sistemas com credenciais seguras: " + string.Join(", ", systems)
            : "Nenhuma credencial segura armazenada.";
    }
}
