using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace AIB.Services;

public class OnlineSkill
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string GithubUrl { get; set; } = "";
    public string Author { get; set; } = "";
}

public static class SkillsShService
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public static async Task<string> SearchAsync(string query)
    {
        try
        {
            // O skills.sh usa uma API de busca. 
            // Como fallback, podemos usar a busca via npx se a API mudar.
            // Para ser rápido para a IA, vamos tentar o endpoint de busca deles.
            string url = $"https://skills.sh/api/search?q={Uri.EscapeDataString(query)}";
            
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AIB-Assistant/1.0");
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                // Fallback: Tenta listar via comando npx
                return await CommandService.ExecuteAsync($"npx skills search {query}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var results = new System.Text.StringBuilder();
            results.AppendLine($"### Resultados do skills.sh para '{query}':\n");

            foreach (var item in doc.RootElement.EnumerateArray().Take(5))
            {
                string name = item.GetProperty("name").GetString() ?? "Sem nome";
                string desc = item.GetProperty("description").GetString() ?? "Sem descrição";
                string github = item.GetProperty("github").GetString() ?? "";
                
                results.AppendLine($"- **{name}**: {desc}");
                results.AppendLine($"  URL: {github}\n");
            }

            return results.ToString();
        }
        catch (Exception)
        {
            // Se a API falhar, o npx é o nosso porto seguro
            return await CommandService.ExecuteAsync($"npx skills search \"{query}\"");
        }
    }
}
