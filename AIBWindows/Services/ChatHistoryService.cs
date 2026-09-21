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

        /// <summary>
        /// Pasta desta conversa em <c>memory/sessions</c>. Vazia nas conversas gravadas antes
        /// deste campo existir — elas reabrem sem memoria, do jeito antigo.
        /// <para>
        /// Sem esta ligacao nao ha como voltar aos capitulos e atos de uma conversa: o Id aqui
        /// e um Guid e a pasta e um carimbo de tempo, e nada os relacionava.
        /// </para>
        /// </summary>
        public string MemorySessionId { get; set; } = "";

        /// <summary>
        /// A thread de e-mail que originou esta conversa — <c>conta|thr:id</c>. Vazia nas
        /// conversas comuns, que é o caso normal.
        /// <para>
        /// Faz dois trabalhos. Tira a conversa da LISTA do painel (§6.1), que é das conversas
        /// que o usuário começou — uma caixa movimentada encheria a lista de linhas que ele
        /// nunca abriu. E é por ela que "Abrir com &lt;NOME&gt;" reencontra o que já foi
        /// conversado sobre aquele e-mail, em vez de começar do zero toda vez.
        /// </para>
        /// <para>
        /// Guarda a CHAVE, que é conta mais identificador de thread — nunca assunto nem
        /// remetente. Ver a nota de <see cref="AIB.Services.Mail.ArquivoDeConversas"/> sobre
        /// por que nome de conversa não vira nome de nada.
        /// </para>
        /// </summary>
        public string MailThreadKey { get; set; } = "";
    }

    /// <summary>
    /// Uma fala de uma conversa arquivada, já separada de quem a disse.
    /// </summary>
    /// <param name="DoUsuario">true = fala do usuário; false = fala da AIB.</param>
    /// <param name="Texto">O texto da fala, sem o prefixo do arquivo.</param>
    public sealed record ChatTurn(bool DoUsuario, string Texto);

    public static class ChatHistoryService
    {
        /// <summary>
        /// Serializa ler-modificar-gravar do arquivo de histórico.
        /// <para>
        /// Sem isto, dois arquivamentos concorrentes leem a mesma lista, cada um acrescenta a
        /// sua conversa e o segundo grava por cima do primeiro: uma das conversas some sem
        /// erro nenhum. Deixou de ser hipótese quando a conversa passou a ser arquivada a cada
        /// turno — antes havia uma gravação por conversa, agora há uma por turno.
        /// </para>
        /// </summary>
        private static readonly object Trava = new();

        private const string PrefixoUsuario = "USER: ";
        private const string PrefixoAgente = "AIB: ";

        /// <summary>
        /// O histórico mora na pasta do usuário, junto com o resto do que é dele.
        /// <para>
        /// Ficava em <c>%APPDATA%\AIB</c>, sobra de antes da migração para <c>~/.AIB</c> —
        /// era o único arquivo do usuário que tinha ficado para trás. A pasta do programa é
        /// failsafe de leitura; a do usuário é a autoridade, e isso vale para o histórico
        /// como vale para memória, personagens e skills.
        /// </para>
        /// <para>
        /// Não há migração do arquivo antigo: ele foi descartado de propósito, a pedido, para
        /// o histórico recomeçar limpo no formato lido pelo <see cref="Parse"/>.
        /// </para>
        /// </summary>
        private static string HistoryFilePath => Path.Combine(DiretorioDoHistorico, "chat_history.json");

        /// <summary>
        /// Raiz alternativa para a suíte de ensaios. Mesmo papel do override da auditoria: um
        /// ensaio que fecha uma janela de conversa dispara o arquivamento, e sem isto a suíte
        /// escreveria conversas inventadas no histórico real do usuário.
        /// </summary>
        public static string? HistoryDirectoryOverride { get; set; }

        private static string DiretorioDoHistorico => ResolverDiretorio(HistoryDirectoryOverride);

        /// <summary>
        /// Onde o histórico vai parar, dada a sobrescrita. Função pura de propósito: a suíte
        /// inteira roda com a sobrescrita ligada, então um ensaio que lesse o caminho efetivo
        /// só veria a pasta temporária e nunca perceberia se o padrão de produção voltasse
        /// para o lugar errado.
        /// </summary>
        public static string ResolverDiretorio(string? sobrescrita) =>
            sobrescrita ?? DirectoryService.DataDir;

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

        /// <summary>Quantas conversas do USUÁRIO o arquivo guarda — as que a lista do painel mostra.</summary>
        public const int MaxConversasDoUsuario = 50;

        /// <summary>Quantas conversas nascidas de e-mail o arquivo guarda, num teto à parte.</summary>
        public const int MaxConversasDeEmail = 50;

        /// <summary>
        /// Corta o histórico nos dois tetos, mantendo as mais recentes de cada lado e a ordem
        /// em que estavam.
        /// <para>
        /// DOIS tetos, e não um de cinquenta para tudo: as conversas de e-mail ficam fora da
        /// lista do painel (<see cref="ConversasDoUsuario"/>), mas contavam no corte. Uma caixa
        /// movimentada, com o usuário abrindo e-mail atrás de e-mail, expulsava do arquivo as
        /// conversas que ELE começou — sem aviso, porque as que entravam nem apareciam na lista.
        /// </para>
        /// <para>
        /// O corte tira a entrada DESTE arquivo e nada mais. A sessão em <c>memory/sessions</c>,
        /// com o <c>raw.jsonl</c>, fica onde está: por regra do projeto ele nunca é apagado.
        /// </para>
        /// <para>
        /// Função pura, pública para o ensaio: o arquivo do histórico é um só para a suíte
        /// inteira, e gravar cem conversas nele para testar o corte atropelaria os outros.
        /// </para>
        /// </summary>
        public static List<ChatSession> Podar(List<ChatSession>? history)
        {
            if (history == null) return new List<ChatSession>();

            var ficam = new HashSet<ChatSession>(ReferenceEqualityComparer.Instance);

            ficam.UnionWith(history
                .Where(h => string.IsNullOrEmpty(h.MailThreadKey))
                .OrderByDescending(h => h.Timestamp)
                .Take(MaxConversasDoUsuario));

            ficam.UnionWith(history
                .Where(h => !string.IsNullOrEmpty(h.MailThreadKey))
                .OrderByDescending(h => h.Timestamp)
                .Take(MaxConversasDeEmail));

            return history.Where(ficam.Contains).ToList();
        }

        public static void SaveHistory(List<ChatSession> history)
        {
            try
            {
                var dir = Path.GetDirectoryName(HistoryFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

                history = Podar(history);

                var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(HistoryFilePath, json);
            }
            catch { }
        }

        /// <param name="titulo">
        /// Nome dado pelo modelo, quando existe. Sem ele vale a heurística antiga — a primeira
        /// mensagem do usuário, cortada em 40 caracteres —, que nomeia o começo da conversa e
        /// não o assunto dela.
        /// </param>
        /// <param name="id">
        /// Identidade da conversa VIVA. Passando o mesmo id de novo, a entrada é substituída em
        /// vez de duplicada — é o que permite arquivar a cada turno em vez de só no fim.
        /// </param>
        public static void SaveCurrentSession(
            List<ChatMessage> currentHistory, string? titulo = null, string? id = null,
            string? memorySessionId = null, string? mailThreadKey = null)
        {
            if (currentHistory == null || currentHistory.Count <= 1) return; // Only system prompt

            var session = new ChatSession
            {
                Timestamp = DateTime.Now
            };

            if (!string.IsNullOrWhiteSpace(id)) session.Id = id!;
            if (!string.IsNullOrWhiteSpace(memorySessionId)) session.MemorySessionId = memorySessionId!;
            if (!string.IsNullOrWhiteSpace(mailThreadKey)) session.MailThreadKey = mailThreadKey!;

            var lines = new List<string>();
            string firstUserMessage = "Novo Chat";

            // REDIGIDO na entrada: este arquivo é a QUARTA saída da conversa para o disco, ao
            // lado do raw.jsonl, do texto do resumidor e do registro de execução — e era a única
            // sem o redator. Um corpo de e-mail embrulhado que chegasse a uma fala (o modelo
            // citando o resultado de mail_read, por exemplo) vinha para cá inteiro. Aplicado
            // antes do título, para ele também não levar um pedaço do corpo.
            static string Gravavel(string texto) => Mail.ConteudoDeTerceiros.Redigir(texto);

            foreach (var msg in currentHistory)
            {
                if (msg is UserChatMessage u)
                {
                    var text = Gravavel(string.Join("\n", u.Content.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text)));
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
                    var text = Gravavel(string.Join("\n", a.Content.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text)));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lines.Add($"AIB: {text}");
                    }
                }
            }

            if (lines.Count == 0) return;

            session.Title = string.IsNullOrWhiteSpace(titulo) ? firstUserMessage : titulo!;
            session.Content = string.Join("\n\n", lines);

            lock (Trava)
            {
                Persistir(session);
            }
        }

        /// <summary>
        /// O que a LISTA do painel mostra (§6.1): as conversas que o usuário começou.
        /// <para>
        /// Fora ficam as nascidas de um e-mail (§3.11). Não por serem menos conversa — elas são
        /// gravadas igual e têm memória igual —, mas porque a lista é curta e uma caixa
        /// movimentada a encheria de linhas que ninguém abriu, enterrando as que foram abertas.
        /// Elas são reencontradas pelo próprio e-mail, em <see cref="ConversaDoEmail"/>.
        /// </para>
        /// <para>
        /// Aqui, e não na View: o filtro escrito lá seria invisível para o ensaio e, no dia em
        /// que houvesse uma segunda tela de histórico, só uma das duas o teria.
        /// </para>
        /// </summary>
        public static List<ChatSession> ConversasDoUsuario() =>
            LoadHistory().Where(h => string.IsNullOrEmpty(h.MailThreadKey)).ToList();

        /// <summary>
        /// A conversa já havida sobre uma thread de e-mail, ou <c>null</c> se é a primeira vez.
        /// <para>
        /// A mais RECENTE, se houver mais de uma: as antigas são de antes de o vínculo existir,
        /// ou de uma reabertura que não encontrou a anterior. Continuar a última é o que
        /// corresponde ao que o usuário lembra.
        /// </para>
        /// </summary>
        public static ChatSession? ConversaDoEmail(string? chaveDaThread)
        {
            if (string.IsNullOrWhiteSpace(chaveDaThread)) return null;

            return LoadHistory()
                .Where(h => string.Equals(h.MailThreadKey, chaveDaThread, StringComparison.Ordinal))
                .OrderByDescending(h => h.Timestamp)
                .FirstOrDefault();
        }

        /// <summary>Insere ou substitui a sessão no arquivo. Sempre sob <see cref="Trava"/>.</summary>
        private static void Persistir(ChatSession session)
        {
            var history = LoadHistory();

            // Mesma conversa gravada de novo: substitui no lugar. A conversa viva é arquivada
            // a cada turno, e sem isto ela apareceria na lista uma vez por turno, cada cópia
            // com um pedaço a mais.
            int existente = history.FindIndex(h => h.Id == session.Id);
            if (existente >= 0)
            {
                history[existente] = session;
                SaveHistory(history);
                return;
            }

            // Abrir uma conversa antiga põe o conteúdo dela no histórico vivo, e arquivá-la
            // gravaria esse mesmo conteúdo como se fosse uma sessão nova. Sem esta checagem,
            // cada ida e volta pelo painel multiplicava a mesma conversa.
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

        /// <summary>
        /// Quantas conversas salvas e quanto elas pesam em disco — §6.6 E10.
        /// <para>
        /// O cálculo vive AQUI, e não na View, porque quem sabe onde cada pedaço de uma
        /// conversa mora é a persistência. Uma View que somasse arquivos precisaria conhecer o
        /// formato da memória, e passaria a mentir na primeira vez que ele mudasse.
        /// </para>
        /// <para>
        /// Entram o arquivo do histórico e, por conversa, os CAPÍTULOS e os ATOS gerados pela
        /// compressão de contexto. O <c>raw.jsonl</c> fica de fora de propósito: ele guarda os
        /// mesmos turnos que já estão no arquivo do histórico, em outro formato, e somar os
        /// dois contaria a mesma conversa duas vezes.
        /// </para>
        /// </summary>
        public static (int Chats, long Bytes) Peso()
        {
            var sessoes = LoadHistory();
            long bytes = Tamanho(HistoryFilePath);

            foreach (var sessao in sessoes)
            {
                if (string.IsNullOrWhiteSpace(sessao.MemorySessionId)) continue;

                try
                {
                    var memoria = new Memory.SessionMemory(sessao.MemorySessionId);
                    bytes += Tamanho(memoria.ChaptersPath) + Tamanho(memoria.ActsPath);
                }
                catch
                {
                    // Id inválido numa conversa antiga não pode derrubar a contagem das outras.
                }
            }

            return (sessoes.Count, bytes);
        }

        private static long Tamanho(string caminho)
        {
            try
            {
                var arquivo = new FileInfo(caminho);
                return arquivo.Exists ? arquivo.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static void DeleteSession(string sessionId)
        {
            lock (Trava)
            {
                var history = LoadHistory();
                var item = history.FirstOrDefault(h => h.Id == sessionId);
                if (item == null) return;

                history.Remove(item);
                SaveHistory(history);
            }
        }

        /// <summary>
        /// Apaga TODAS as conversas havidas sobre uma thread de e-mail. Devolve quantas saíram.
        /// <para>
        /// Todas, e não só a mais recente que <see cref="ConversaDoEmail"/> devolve: as antigas
        /// existem (de antes do vínculo, ou de reaberturas que não acharam a anterior), e
        /// "descartar a conversa sobre este e-mail" que deixasse uma delas voltaria a mostrá-la
        /// na próxima abertura.
        /// </para>
        /// </summary>
        public static int DeleteConversasDoEmail(string? chaveDaThread)
        {
            if (string.IsNullOrWhiteSpace(chaveDaThread)) return 0;

            lock (Trava)
            {
                var history = LoadHistory();
                int removidas = history.RemoveAll(h =>
                    string.Equals(h.MailThreadKey, chaveDaThread, StringComparison.Ordinal));

                if (removidas > 0) SaveHistory(history);
                return removidas;
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
