using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;

namespace AIB.Services;

public class UserAppSettings
{
    public string ApiUrl { get; set; } = "http://localhost:11434/v1/";
    public string ApiKey { get; set; } = "ollama";
    public string ModelName { get; set; } = "qwen2.5:7b";
    public string AiProvider { get; set; } = "Ollama"; // "Ollama" ou "Google Gemini"
    public string DataDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIB");

    // Diretórios
    public string DataDirectory { get; set; } = "";
    public string TempDirectory { get; set; } = "";

    // Avançado
    public int MaxContextTokens { get; set; } = 30000;
    public bool ConfirmDangerousCommands { get; set; } = true;
    public bool EphemeralSkillContext { get; set; } = true;
    public string SearchEngine { get; set; } = "DuckDuckGo"; // Google, DuckDuckGo, Bing

    // Gamificação / Sistema de Níveis
    public int MessageCount { get; set; } = 0;
}

public class SettingsService
{
    private static readonly string SettingsPath = DirectoryService.SettingsPath;
    private readonly HttpClient _httpClient = new HttpClient();

    public UserAppSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath))
        {
            var defaults = new UserAppSettings();
            SaveSettings(defaults);
            return defaults;
        }

        try
        {
            byte[] encryptedBytes = File.ReadAllBytes(SettingsPath);
            byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(decryptedBytes);
            return JsonSerializer.Deserialize<UserAppSettings>(json) ?? new UserAppSettings();
        }
        catch
        {
            // Fallback para arquivo em texto claro (Migração de legado)
            try
            {
                string json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<UserAppSettings>(json) ?? new UserAppSettings();
                SaveSettings(settings); // Re-salva criptografado
                return settings;
            }
            catch
            {
                return new UserAppSettings();
            }
        }
    }

    public void SaveSettings(UserAppSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        byte[] plainBytes = Encoding.UTF8.GetBytes(json);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SettingsPath, encryptedBytes);
    }

    public async Task<List<string>> GetOllamaModelsAsync(string baseUrl)
    {
        string baseOllamaUrl = string.IsNullOrEmpty(baseUrl) ? "http://localhost:11434" : baseUrl;
        baseOllamaUrl = baseOllamaUrl.Replace("/v1", "").TrimEnd('/');
        
        try
        {
            var response = await _httpClient.GetAsync($"{baseOllamaUrl}/api/tags");
            if (!response.IsSuccessStatusCode) return new List<string>();

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out JsonElement modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out JsonElement nameProp))
                    {
                        models.Add(nameProp.GetString() ?? "");
                    }
                }
            }
            return models;
        }
        catch
        {
            return new List<string>(); // Falha (provavelmente não é Ollama ou está offline)
        }
    }
}
