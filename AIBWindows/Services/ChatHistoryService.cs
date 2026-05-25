using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenAI.Chat;

namespace AIB.Services
{
    public class ChatSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Content { get; set; } = "";
    }

    public static class ChatHistoryService
    {
        private static readonly string HistoryFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIB", "chat_history.json");

        public static List<ChatSession> LoadHistory()
        {
            if (!File.Exists(HistoryFilePath)) return new List<ChatSession>();
            try
            {
                var json = File.ReadAllText(HistoryFilePath);
                return JsonSerializer.Deserialize<List<ChatSession>>(json) ?? new List<ChatSession>();
            }
            catch
            {
                return new List<ChatSession>();
            }
        }

        public static void SaveHistory(List<ChatSession> history)
        {
            try
            {
                var dir = Path.GetDirectoryName(HistoryFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

                // Keep only top 50 histories to avoid gigantic files
                if (history.Count > 50)
                {
                    history = history.OrderByDescending(h => h.Timestamp).Take(50).ToList();
                }

                var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(HistoryFilePath, json);
            }
            catch { }
        }

        public static void SaveCurrentSession(List<ChatMessage> currentHistory)
        {
            if (currentHistory == null || currentHistory.Count <= 1) return; // Only system prompt

            var session = new ChatSession
            {
                Timestamp = DateTime.Now
            };

            var lines = new List<string>();
            string firstUserMessage = "Novo Chat";

            foreach (var msg in currentHistory)
            {
                if (msg is UserChatMessage u)
                {
                    var text = string.Join("\n", u.Content.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text));
                    if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith("[SYSTEM"))
                    {
                        if (firstUserMessage == "Novo Chat")
                        {
                            firstUserMessage = text.Length > 40 ? text.Substring(0, 40) + "..." : text;
                        }
                        lines.Add($"USER: {text}");
                    }
                }
                else if (msg is AssistantChatMessage a && a.Content != null)
                {
                    var text = string.Join("\n", a.Content.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lines.Add($"AIB: {text}");
                    }
                }
            }

            if (lines.Count == 0) return;

            session.Title = firstUserMessage;
            session.Content = string.Join("\n\n", lines);

            var history = LoadHistory();
            history.Insert(0, session);
            SaveHistory(history);
        }
        public static void DeleteSession(string sessionId)
        {
            var history = LoadHistory();
            var item = history.FirstOrDefault(h => h.Id == sessionId);
            if (item != null)
            {
                history.Remove(item);
                SaveHistory(history);
            }
        }
    }
}
