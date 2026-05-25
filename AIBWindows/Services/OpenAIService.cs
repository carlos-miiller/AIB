using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using OpenAI.Chat;
using OpenAI;
using System.ClientModel;
using Microsoft.ML.Tokenizers;

namespace AIB.Services;

public class OpenAIService
{
    private ChatClient? _client;
    private string? _lastModel;
    private readonly List<ChatMessage> _history = new();
    private readonly SettingsService _settingsService;
    private readonly ToolRegistry _toolRegistry;
    private CancellationTokenSource? _generationCts;
    private readonly Tokenizer _tokenizer;

    public event Action<int, int>? OnTokenCountChanged;

    public void CancelGeneration()
    {
        _generationCts?.Cancel();
    }

    public ToolRegistry Registry => _toolRegistry;

    // Regex para detectar chamadas de ferramenta escritas em texto pelo LLM
    // Captura padrões como: Action: nome_tool({"key": "value"}) ou Action: nome_tool()
    private static readonly Regex TextActionRegex = new(
        @"(?:Action|Ação|action):\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\(([^)]*)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string SYSTEM_PROMPT =
        """
        Identidade: Você é o AIB, um Agente Autônomo SOTA (State of the Art) rodando localmente no Windows do usuário.
        
        # Padrão de Operação: ReAct Puro
        Para TODA solicitação que exija busca de informação, memória, execução de script ou visão de tela:
        1. PENSE brevemente no plano (uma linha).
        2. EXECUTE a ferramenta mais adequada (SEMPRE use chamada de ferramenta nativa, não escreva em texto).
        3. OBSERVE o resultado retornado.
        4. RESPONDA ao usuário com base na observação.
        
        # Regras Absolutas
        - NUNCA afirme que não possui uma informação sem antes chamar `recall` e `retrieve_credential`.
        - SEMPRE use as ferramentas disponíveis — você tem acesso a memória, terminal, web, OCR e visão de tela.
        - Se uma ferramenta falhar, analise o erro na Observation e tente no máximo mais uma alternativa. Se falhar novamente, informe o usuário do erro e pare. Não entre em loop infinito tentando a mesma coisa.
        - Não peça permissão para executar ferramentas. Aja.
        - Responda SEMPRE em Português (Brasil).
        - Seja conciso nas respostas ao usuário — o raciocínio técnico pertence ao console, não ao chat.
        """;

    public OpenAIService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _toolRegistry = new ToolRegistry();
        _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
    }

    public List<ChatMessage> History => _history;

    public void ResetHistory()
    {
        if (_history != null && _history.Count > 1)
        {
            ChatHistoryService.SaveCurrentSession(_history);
        }
        _history?.Clear();
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var contextualPrompt = SYSTEM_PROMPT + $"\n\nContexto Local:\n- Diretório Home do Usuário (Raiz): {userHome}";
        _history.Add(ChatMessage.CreateSystemMessage(contextualPrompt));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STREAM PRINCIPAL — Loop ReAct com Dual-Mode Tool Execution
    // ─────────────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string userMessage,
        Action<string>? onTechnicalContent = null)
    {
        _generationCts?.Dispose();
        _generationCts = new CancellationTokenSource();
        var ct = _generationCts.Token;

        // Garante que o cliente está configurado com as settings atuais
        EnsureClient();

        if (_history.Count == 0) ResetHistory();
        _history.Add(ChatMessage.CreateUserMessage(userMessage));
        int userLevel = LevelService.GetLevel(_settingsService.LoadSettings().MessageCount);
        var tools = _toolRegistry.GetActiveTools(userLevel);
        
        bool requiresAction = true;
        int maxLoops = 5;

        while (requiresAction && maxLoops-- > 0)
        {
            requiresAction = false;
            var chatOptions = new ChatCompletionOptions();
            foreach (var tool in tools) chatOptions.Tools.Add(tool);

            var updates = _client!.CompleteChatStreamingAsync(_history, chatOptions, ct);

            string fullResponse = "";
            var toolCalls = new List<(string Id, string Name, string Args)>();
            string? pendingTextAction = null; // Fallback: ação escrita como texto

            await foreach (var update in updates.WithCancellation(ct))
            {
                // ── Processamento de chunks de texto ──────────────────────────
                if (update.ContentUpdate.Count > 0)
                {
                    var chunk = update.ContentUpdate[0].Text;
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        fullResponse += chunk;

                        // Detecção do Fallback Regex: o LLM escreveu "Action: nome_tool(...)"
                        var match = TextActionRegex.Match(fullResponse);
                        if (match.Success)
                        {
                            pendingTextAction = fullResponse;
                            onTechnicalContent?.Invoke($"\n[FALLBACK REGEX] Ação em texto detectada: {match.Value}\n");
                            // Não emite mais texto para o usuário — este é conteúdo técnico
                        }
                        else if (!IsOnlyTechnicalContent(chunk))
                        {
                            yield return chunk;
                        }
                        else
                        {
                            onTechnicalContent?.Invoke(chunk);
                        }
                    }
                }

                // ── Processamento de native tool call updates ─────────────────
                foreach (var tcUpdate in update.ToolCallUpdates)
                {
                    var existing = toolCalls.FirstOrDefault(tc => tc.Id == tcUpdate.ToolCallId);
                    if (existing == default && !string.IsNullOrEmpty(tcUpdate.ToolCallId))
                    {
                        toolCalls.Add((tcUpdate.ToolCallId, tcUpdate.FunctionName ?? "", tcUpdate.FunctionArgumentsUpdate?.ToString() ?? ""));
                    }
                    else if (existing != default)
                    {
                        int idx = toolCalls.IndexOf(existing);
                        toolCalls[idx] = (existing.Id, existing.Name, existing.Args + (tcUpdate.FunctionArgumentsUpdate?.ToString() ?? ""));
                    }
                }
            }

            // ── Execução: Native Tool Calls (Modo Primário) ───────────────────
            if (toolCalls.Count > 0)
            {
                requiresAction = true;
                var chatToolCalls = toolCalls
                    .Select(tc => ChatToolCall.CreateFunctionToolCall(tc.Id, tc.Name, BinaryData.FromString(tc.Args)))
                    .ToList();
                _history.Add(ChatMessage.CreateAssistantMessage(chatToolCalls));

                foreach (var tc in toolCalls)
                {
                    onTechnicalContent?.Invoke($"[FERRAMENTA] Nome: {tc.Name} | Args: {tc.Args}");

                    string result = await _toolRegistry.ExecuteToolAsync(tc.Name, tc.Args, userLevel);
                    onTechnicalContent?.Invoke($"[FERRAMENTA] Resultado: {result}\n");
                    _history.Add(ChatMessage.CreateToolMessage(tc.Id, result));

                    // Atualiza a lista de arquivos recentes acessados
                    if (tc.Name == "read_file" || tc.Name == "view_file" || tc.Name == "write_to_file" || tc.Name == "replace_file_content" || tc.Name == "multi_replace_file_content")
                    {
                        try {
                            var dict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(tc.Args);
                            string? path = null;
                            if (dict != null && dict.ContainsKey("AbsolutePath")) path = dict["AbsolutePath"].GetString();
                            else if (dict != null && dict.ContainsKey("TargetFile")) path = dict["TargetFile"].GetString();
                            else if (dict != null && dict.ContainsKey("path")) path = dict["path"].GetString();
                            
                            if (!string.IsNullOrEmpty(path)) ContextService.AddRecentFile(path);
                        } catch { }
                    }

                    // Se uma skill foi materializada, refresca o registry para disponibilizar imediatamente
                    if (tc.Name == "materialize_skill") _toolRegistry.Refresh();
                }
            }
            // ── Execução: Regex Fallback (Modo Secundário de Resiliência) ─────
            else if (pendingTextAction != null)
            {
                requiresAction = true;
                var match = TextActionRegex.Match(pendingTextAction);
                if (match.Success)
                {
                    string toolName = match.Groups[1].Value;
                    string rawArgs = match.Groups[2].Value.Trim();
                    // Tenta construir JSON se não for um JSON
                    string argsJson = rawArgs.StartsWith("{") ? rawArgs : $"{{\"arguments\":\"{rawArgs}\"}}";

                    onTechnicalContent?.Invoke($"[FALLBACK REGEX FERRAMENTA] Nome: {toolName} | Args: {argsJson}");

                    string result = await _toolRegistry.ExecuteToolAsync(toolName, argsJson, userLevel);
                    onTechnicalContent?.Invoke($"[FALLBACK REGEX FERRAMENTA] Resultado: {result}\n");

                    // Injeta no histórico como mensagem de usuário técnica para simular a tool call
                    _history.Add(ChatMessage.CreateAssistantMessage(pendingTextAction));
                    _history.Add(ChatMessage.CreateUserMessage($"[SYSTEM - Tool Result for {toolName}]: {result}"));
                }
            }
            // ── Resposta final de texto (sem tool calls) ──────────────────────
            else if (!string.IsNullOrEmpty(fullResponse))
            {
                // Remove blocos de raciocínio interno antes de salvar no histórico
                string cleanedForHistory = CleanReasoningBlocks(fullResponse);
                _history.Add(ChatMessage.CreateAssistantMessage(cleanedForHistory));

                // Trim do histórico para não estourar o contexto
                await TrimHistoryAsync(userLevel);
            }
        }
    }

    public IAsyncEnumerable<string> SendMessageStreamAsync(string text) => StreamResponseAsync(text);

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers Privados
    // ─────────────────────────────────────────────────────────────────────────

    private void EnsureClient()
    {
        var settings = _settingsService.LoadSettings();
        string modelName = settings.ModelName;

        // Re-cria o cliente se o modelo mudou (evita reuso com modelo errado)
        if (_client == null || _lastModel != modelName)
        {
            string apiUrl = settings.ApiUrl;
            string apiKey = settings.ApiKey;

            if (settings.AiProvider == "Ollama")
            {
                if (string.IsNullOrEmpty(apiUrl)) apiUrl = "http://localhost:11434/v1";
                if (string.IsNullOrEmpty(apiKey)) apiKey = "ollama";
            }

            if (string.IsNullOrEmpty(apiKey)) apiKey = "placeholder";

            var options = new OpenAIClientOptions();
            if (!string.IsNullOrEmpty(apiUrl)) options.Endpoint = new Uri(apiUrl);
            _client = new ChatClient(modelName, new ApiKeyCredential(apiKey), options);
            _lastModel = modelName;

            Console.WriteLine($"[AI] Cliente inicializado: {modelName} @ {apiUrl}");
        }
    }

    private static bool IsOnlyTechnicalContent(string chunk)
    {
        string[] technicalMarkers = { "Thought:", "Action:", "Observation:", "Ação:", "<think>", "</think>" };
        return technicalMarkers.Any(m => chunk.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string CleanReasoningBlocks(string text)
    {
        // Remove blocos de raciocínio interno (ex: <think>...</think> do Qwen)
        text = Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
        // Remove linhas que iniciam com marcadores de raciocínio ReAct
        text = Regex.Replace(text, @"(?m)^(Thought|Action|Observation|Ação):[^\n]*\n?", "");
        return text.Trim();
    }

    private int CalculateCurrentTokens()
    {
        int tokens = 0;
        foreach (var msg in _history)
        {
            if (msg.Content != null)
            {
                foreach (var part in msg.Content)
                {
                    if (part != null && part.Text != null)
                    {
                        tokens += _tokenizer.CountTokens(part.Text);
                    }
                }
            }
        }
        return tokens;
    }

    private void NotifyTokenCount(int userLevel)
    {
        int tokens = CalculateCurrentTokens();
        int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
        OnTokenCountChanged?.Invoke(tokens, maxTokens);
    }

    private async System.Threading.Tasks.Task TrimHistoryAsync(int userLevel)
    {
        int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
        int currentTokens = CalculateCurrentTokens();

        if (currentTokens > maxTokens && _history.Count > 3)
        {
            // Pega a metade das mensagens antigas (ignorando o System Prompt no índice 0)
            int half = (_history.Count - 1) / 2;
            if (half == 0) return;

            var oldMessages = _history.Skip(1).Take(half).ToList();
            
            // Gerar resumo
            string summaryPrompt = "Resuma brevemente os principais pontos, contexto e decisões da conversa a seguir:\n";
            foreach (var m in oldMessages) 
            {
                string txt = string.Join(" ", m.Content.Select(c => c.Text));
                summaryPrompt += $"- {txt}\n";
            }

            var summaryRequest = new List<ChatMessage> {
                ChatMessage.CreateSystemMessage("Você é um sumarizador eficiente. Retorne apenas o resumo sem saudações."),
                ChatMessage.CreateUserMessage(summaryPrompt)
            };

            try
            {
                var completion = await _client!.CompleteChatAsync(summaryRequest);
                string summary = completion.Value.Content[0].Text;

                // Remove the old messages
                _history.RemoveRange(1, half);

                // Insert the summary
                _history.Insert(1, ChatMessage.CreateAssistantMessage($"[RESUMO DO CONTEXTO ANTERIOR]: {summary}"));
            }
            catch
            {
                // Em caso de erro (ex: offline), removemos a mais velha
                _history.RemoveAt(1);
            }
            
            // Recalcula após podar
            currentTokens = CalculateCurrentTokens();
        }
        
        // Sempre notifica a interface
        NotifyTokenCount(userLevel);
    }
}
