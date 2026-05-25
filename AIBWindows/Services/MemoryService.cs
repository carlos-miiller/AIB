using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LiteDB;
using SmartComponents.LocalEmbeddings;

namespace AIB.Services;

public static class MemoryService
{
    private static string DbPath => Path.Combine(DirectoryService.DataDir, "memory.db");
    private static LocalEmbedder? _embedder;
    
    private static LocalEmbedder Embedder
    {
        get
        {
            if (_embedder == null)
            {
                // Carrega o modelo ONNX automaticamente na primeira execução
                _embedder = new LocalEmbedder();
            }
            return _embedder;
        }
    }

    public class MemoryRecord
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public float[] Vector { get; set; } = Array.Empty<float>();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public static Task<string> RememberAsync(string key, string content)
    {
        try
        {
            using var db = new LiteDatabase(DbPath);
            var col = db.GetCollection<MemoryRecord>("memories");
            
            // Gerar embedding do conteúdo
            var vector = Embedder.Embed($"{key}\n{content}");
            
            var record = new MemoryRecord
            {
                Title = key,
                Content = content,
                Vector = vector.Values.ToArray()
            };
            
            col.Insert(record);
            return Task.FromResult($"[RAG] Memória Semântica guardada com sucesso! (ID: {record.Id})");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"ERRO ao salvar memória no LiteDB: {ex.Message}");
        }
    }

    private static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dotProduct = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return dotProduct / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    public static string Recall(string query)
    {
        try
        {
            using var db = new LiteDatabase(DbPath);
            var col = db.GetCollection<MemoryRecord>("memories");
            var allRecords = col.FindAll().ToList();

            if (allRecords.Count == 0) 
                return "A memória está vazia. Você ainda não salvou fatos importantes.";

            var queryVector = Embedder.Embed(query).Values.ToArray();

            // Calcular Similaridade Semântica (Cosseno)
            var scoredRecords = allRecords.Select(r => new
            {
                Record = r,
                Score = CosineSimilarity(queryVector, r.Vector)
            })
            .OrderByDescending(x => x.Score)
            .Take(3)
            .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"### [RAG] Top 3 Memórias Semânticas Localizadas para '{query}':");
            
            bool foundAny = false;
            foreach (var match in scoredRecords)
            {
                // Se a similaridade for muito baixa (<20%), ignoramos
                if (match.Score < 0.2f) continue;
                
                sb.AppendLine($"\n--- [{match.Record.CreatedAt:d}] {match.Record.Title} (Match: {match.Score:P0}) ---");
                sb.AppendLine(match.Record.Content);
                foundAny = true;
            }

            if (!foundAny) return $"Nenhuma memória semântica perfeitamente alinhada com '{query}' foi encontrada (Match < 20%).";

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"ERRO ao acessar RAG: {ex.Message}";
        }
    }

    public static string ListMemories()
    {
        try
        {
            using var db = new LiteDatabase(DbPath);
            var col = db.GetCollection<MemoryRecord>("memories");
            var allRecords = col.FindAll().OrderByDescending(r => r.CreatedAt).ToList();

            if (allRecords.Count == 0) return "Nenhum fragmento no banco de dados RAG.";

            return "Memórias no banco LiteDB:\n- " + string.Join("\n- ", allRecords.Select(r => $"ID:{r.Id} | {r.Title} ({r.CreatedAt:d})"));
        }
        catch (Exception ex)
        {
            return $"ERRO ao listar memória: {ex.Message}";
        }
    }
}
