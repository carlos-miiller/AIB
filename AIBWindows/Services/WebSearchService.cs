using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIB.Services;

public static class WebSearchService
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public static async Task<string> SearchAsync(string query, string engine = "DuckDuckGo")
    {
        try
        {
            if (engine == "DuckDuckGo")
            {
                // API direta do DDG (Instant Answer)
                string url = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AIB-Assistant/1.0");
                var response = await _httpClient.GetStringAsync(url);
                
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                string abstractText = root.GetProperty("AbstractText").GetString() ?? "";
                var results = new System.Text.StringBuilder();

                if (!string.IsNullOrWhiteSpace(abstractText))
                {
                    results.AppendLine($"### Resumo ({root.GetProperty("AbstractSource").GetString()}):");
                    results.AppendLine(abstractText);
                }

                results.AppendLine("\n### Tópicos Relacionados:");
                foreach (var topic in root.GetProperty("RelatedTopics").EnumerateArray().Take(5))
                {
                    if (topic.TryGetProperty("Text", out JsonElement text))
                        results.AppendLine($"- {text.GetString()}");
                }
                return results.ToString();
            }
            else
            {
                // Para Google e Bing, como não temos API Key, usaremos uma busca HTML simplificada
                // ou simplesmente abriremos o navegador para o usuário se o scraping falhar.
                // Mas para o Agente, precisamos de TEXTO. 
                // Como scraping de Google é bloqueado fácil, vamos sugerir o uso do DuckDuckGo se falhar.
                
                string searchUrl = engine == "Google" 
                    ? $"https://www.google.com/search?q={Uri.EscapeDataString(query)}"
                    : $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}";

                return $"[INFO] O motor {engine} requer scraping complexo ou API Key. \n" +
                       $"Por favor, use DuckDuckGo nas configurações para obter resultados de texto diretos.\n" +
                       $"URL da busca: {searchUrl}";
            }
        }
        catch (Exception ex)
        {
            return $"ERRO na busca ({engine}): {ex.Message}";
        }
    }
}
