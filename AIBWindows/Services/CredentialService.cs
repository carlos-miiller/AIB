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

    /// <summary>
    /// A credencial de UM sistema, sem busca global pelos outros arquivos do cofre. Nulo quando
    /// não existe.
    /// <para>
    /// Uma busca global devolveria a primeira chave com o mesmo NOME em qualquer arquivo do cofre:
    /// sem chave do OpenRouter, ela entregaria a chave da OpenAI gravada por uma versão antiga — e a
    /// requisição a mandaria para outro serviço.
    /// </para>
    /// </summary>
    public static string? LerDoSistema(string system, string key)
    {
        try
        {
            string filePath = Path.Combine(CredentialsDir, $"{system.ToLower()}.bin");
            if (!File.Exists(filePath)) return null;

            string json = DecryptFile(filePath);
            if (string.IsNullOrEmpty(json)) return null;

            var creds = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return creds != null && creds.TryGetValue(key, out string? valor) && !string.IsNullOrEmpty(valor)
                ? valor
                : null;
        }
        catch
        {
            return null;
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

    public static void WipeAllCredentials()
    {
        try
        {
            if (Directory.Exists(CredentialsDir))
            {
                Directory.Delete(CredentialsDir, true);
                EnsureDir();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG-COFRE] ERRO ao limpar cofre: {ex.Message}");
        }
    }
}
