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

    /// <summary>
    /// Uma fala de uma conversa arquivada, já separada de quem a disse.
    /// </summary>
    /// <param name="DoUsuario">true = fala do usuário; false = fala da AIB.</param>
    /// <param name="Texto">O texto da fala, sem o prefixo do arquivo.</param>
    public sealed record ChatTurn(bool DoUsuario, string Texto);

    public static class ChatHistoryService
    {
        private const string PrefixoUsuario = "USER: ";
        private const string PrefixoAgente = "AIB: ";

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

            // Abrir uma conversa antiga põe o conteúdo dela no histórico vivo, e a próxima
            // troca de conversa salvaria esse mesmo conteúdo como se fosse uma sessão nova.
            // Sem esta checagem, cada ida e volta pelo painel multiplicava a mesma conversa.
            if (history.Any(h => h.Content == session.Content)) return;

            history.Insert(0, session);
            SaveHistory(history);
        }
        /// <summary>
        /// Desfaz o formato de <see cref="SaveCurrentSession"/> e devolve as falas separadas.
        /// <para>
        /// O arquivo guarda a conversa como texto corrido, uma fala por bloco, prefixada por
        /// <c>USER:</c> ou <c>AIB:</c>. Para RECUPERAR CONTEXTO isso basta — o bloco inteiro
        /// vira uma mensagem só. Para ABRIR a conversa, não: cada fala precisa voltar ao seu
        /// balão e ao seu papel no histórico do modelo.
        /// </para>
        /// <para>
        /// A varredura é por LINHA, e não por bloco separado de linha em branco: uma fala com
        /// parágrafos — que é o caso normal de resposta com markdown — seria picada em várias.
        /// Linha sem prefixo pertence à fala corrente.
        /// </para>
        /// </summary>
        public static IReadOnlyList<ChatTurn> Parse(string? content)
        {
            var falas = new List<ChatTurn>();
            if (string.IsNullOrWhiteSpace(content)) return falas;

            bool? doUsuario = null;
            var atual = new List<string>();

            void Fechar()
            {
                if (doUsuario == null) return;
                string texto = string.Join("\n", atual).Trim();
                if (texto.Length > 0) falas.Add(new ChatTurn(doUsuario.Value, texto));
                atual.Clear();
            }

            foreach (var linha in content.Replace("\r\n", "\n").Split('\n'))
            {
                if (linha.StartsWith(PrefixoUsuario, StringComparison.Ordinal))
                {
                    Fechar();
                    doUsuario = true;
                    atual.Add(linha.Substring(PrefixoUsuario.Length));
                }
                else if (linha.StartsWith(PrefixoAgente, StringComparison.Ordinal))
                {
                    Fechar();
                    doUsuario = false;
                    atual.Add(linha.Substring(PrefixoAgente.Length));
                }
                else if (doUsuario != null)
                {
                    atual.Add(linha);
                }
                // Linha antes do primeiro prefixo é lixo de formato anterior: ignorada.
            }

            Fechar();
            return falas;
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

        public static void ClearHistory()
        {
            try
            {
                if (File.Exists(HistoryFilePath))
                {
                    File.Delete(HistoryFilePath);
                }
            }
            catch { }
        }
    }
}
