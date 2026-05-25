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
        - NUNCA afirme que não possui uma informação sem antes tentar `manage_memory` com action="recall" e, para credenciais, `manage_vault` com action="retrieve".
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
        
        // Inicia o aquecimento e trava de memória em background
        Task.Run(() => WarmupAndKeepAliveAsync());
    }

    public event Action<bool>? OnWarmupStateChanged;

    private async Task WarmupAndKeepAliveAsync()
    {
        try
        {
            EnsureClient();
            var settings = _settingsService.LoadSettings();
            if (settings.AiProvider != "Ollama") return;

            string apiUrl = string.IsNullOrEmpty(settings.ApiUrl) ? "http://127.0.0.1:11434" : settings.ApiUrl.Replace("/v1", "").TrimEnd('/');
            
            Console.WriteLine($"[WARMUP] Iniciando trava de memória (Keep-Alive Infinita) para {settings.ModelName}...");
            using var httpClient = new System.Net.Http.HttpClient();
            var payload = new { model = settings.ModelName, keep_alive = -1 };
            var content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            
            // 1. Envia requisição crua para travar o modelo na VRAM
            await httpClient.PostAsync($"{apiUrl}/api/generate", content);
            Console.WriteLine("[WARMUP] Modelo trancado na memória com sucesso.");

            // Dispara evento para travar a interface
            OnWarmupStateChanged?.Invoke(true);

            // 2. Compila a Árvore de Gramática com o Histórico Real
            Console.WriteLine("[WARMUP] Compilando gramática das Nativas no histórico oficial...");

            // Força a criação do System Prompt Oficial se estiver vazio
            if (_history.Count == 0) ResetHistory();

            // Snapshot do histórico real para restaurar depois (descarta as mensagens fantasma)
            int realHistoryCount = _history.Count;

            // Adiciona a mensagem fantasma de heartbeat (transitória)
            _history.Add(ChatMessage.CreateUserMessage("[SYSTEM_HEARTBEAT] O sistema acabou de iniciar. Responda apenas 'SISTEMA ONLINE'. Não use nenhuma ferramenta."));

            int userLevel = LevelService.GetLevel(settings.MessageCount);
            var tools = _toolRegistry.GetActiveTools(userLevel);
            var chatOptions = new ChatCompletionOptions() { Temperature = 0.1f };
            foreach (var tool in tools) chatOptions.Tools.Add(tool);

            try
            {
                // Faz a requisição completa usando o _history para garantir Prefix Match perfeito
                var completion = await _client!.CompleteChatAsync(_history, chatOptions, CancellationToken.None);
                string responseText = completion.Value.Content[0].Text;
                Console.WriteLine($"[WARMUP] Gramática em cache! Resposta final: {responseText}");
            }
            finally
            {
                // Remove tudo que foi adicionado durante o warmup (heartbeat + resposta).
                // Mantemos apenas o histórico "real" anterior ao warmup para não desperdiçar tokens.
                if (_history.Count > realHistoryCount)
                    _history.RemoveRange(realHistoryCount, _history.Count - realHistoryCount);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARMUP ERRO] {ex.Message}");
        }
        finally
        {
            // Libera a interface
            OnWarmupStateChanged?.Invoke(false);
        }
    }

    public List<ChatMessage> History => _history;

    public void ResetHistory()
    {
        if (_history != null && _history.Count > 1)
        {
            ChatHistoryService.SaveCurrentSession(_history);
        }
        _history?.Clear();

        // Setting "SendSystemPrompt": permite desligar quando o usuário tem um Modelfile
        // do Ollama com SYSTEM embutido (evita duplicação de instruções).
        if (_settingsService.LoadSettings().SendSystemPrompt)
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var contextualPrompt = SYSTEM_PROMPT + $"\n\nContexto Local:\n- Diretório Home do Usuário (Raiz): {userHome}";

            try
            {
                var skills = SkillService.ListLocalSkills();
                if (skills.Count > 0)
                {
                    contextualPrompt += "\n\nHabilidades dinâmicas disponíveis (use a ferramenta 'execute_skill' para chamá-las passando 'skill_name'):\n";
                    foreach (var skill in skills)
                    {
                        if (skill.Interpreter.Equals("markdown", StringComparison.OrdinalIgnoreCase)) continue;
                        contextualPrompt += $"- {skill.Name}: {skill.Description}\n";
                    }
                }
            }
            catch { }

            _history.Add(ChatMessage.CreateSystemMessage(contextualPrompt));
        }
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
            var chatOptions = new ChatCompletionOptions() { Temperature = 0.1f };

            // Setting "EnableIntelligentTools": permite desligar todas as tool defs
            // quando o usuário quer chat puro/rápido (útil para Ollama em hardware modesto,
            // já que cada tool inflada a gramática JSON e adiciona latência de prefill).
            if (_settingsService.LoadSettings().EnableIntelligentTools)
            {
                foreach (var tool in tools) chatOptions.Tools.Add(tool);
            }

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

                    // Simula uma tool call coerente: emite Assistant com ToolCall + ToolMessage com mesmo Id.
                    // Isso mantém o histórico em formato ReAct legítimo para a próxima iteração.
                    string syntheticId = $"fallback_{Guid.NewGuid():N}";
                    var syntheticCall = ChatToolCall.CreateFunctionToolCall(syntheticId, toolName, BinaryData.FromString(argsJson));
                    _history.Add(ChatMessage.CreateAssistantMessage(new[] { syntheticCall }));
                    _history.Add(ChatMessage.CreateToolMessage(syntheticId, result));
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

            // Notifica a UI a cada iteração do loop ReAct para que o contador de tokens
            // reflita o histórico real (e não só os momentos de trim).
            NotifyTokenCount(userLevel);
        }
    }

    public IAsyncEnumerable<string> SendMessageStreamAsync(string text) => StreamResponseAsync(text);

    public async Task<string> AskStatelessAsync(string systemPrompt, string userPrompt, string? overrideModel = null)
    {
        var settings = _settingsService.LoadSettings();
        string modelName = overrideModel ?? settings.ModelName;

        string apiUrl = settings.ApiUrl;
        string apiKey = settings.ApiKey;

        if (settings.AiProvider == "Ollama")
        {
            if (string.IsNullOrEmpty(apiUrl)) apiUrl = "http://127.0.0.1:11434/v1";
            if (string.IsNullOrEmpty(apiKey)) apiKey = "ollama";
            
            // Bypass IPv6 DNS resolution issues that cause 2-minute timeouts
            apiUrl = apiUrl.Replace("localhost", "127.0.0.1");
        }
        if (string.IsNullOrEmpty(apiKey)) apiKey = "placeholder";

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(apiUrl)) options.Endpoint = new Uri(apiUrl);
        var localClient = new ChatClient(modelName, new ApiKeyCredential(apiKey), options);

        var msgs = new List<ChatMessage>();
        if (settings.SendSystemPrompt)
        {
            msgs.Add(ChatMessage.CreateSystemMessage(systemPrompt));
        }
        msgs.Add(ChatMessage.CreateUserMessage(userPrompt));
        
        var chatOptions = new ChatCompletionOptions();
        try
        {
            var response = await localClient.CompleteChatAsync(msgs, chatOptions);
            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            return $"[ERROR]: {ex.Message}";
        }
    }

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
                if (string.IsNullOrEmpty(apiUrl)) apiUrl = "http://127.0.0.1:11434/v1";
                if (string.IsNullOrEmpty(apiKey)) apiKey = "ollama";
                
                // Bypass IPv6 DNS resolution issues that cause 2-minute timeouts
                apiUrl = apiUrl.Replace("localhost", "127.0.0.1");
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

        // Sliding window: enquanto estiver acima do limite, remove a mensagem mais antiga
        // (mantendo SEMPRE o System Prompt no índice 0). Cuidado especial para não deixar
        // um Assistant com tool_calls sem suas ToolMessages correspondentes — a API rejeita.
        int safetyCounter = 0;
        while (currentTokens > maxTokens && _history.Count > 3 && safetyCounter++ < 200)
        {
            int removeIdx = 1; // primeiro após o System Prompt
            var msg = _history[removeIdx];

            _history.RemoveAt(removeIdx);

            // Se a mensagem removida era um Assistant com tool_calls, remova também as ToolMessages
            // imediatamente seguintes (são as respostas dessas tool_calls).
            if (msg is AssistantChatMessage acm && acm.ToolCalls != null && acm.ToolCalls.Count > 0)
            {
                while (_history.Count > 1 && _history[1] is ToolChatMessage)
                    _history.RemoveAt(1);
            }

            currentTokens = CalculateCurrentTokens();
        }

        // (Sumarização agressiva foi removida: gastava 1 chamada LLM por trim e perdia nuance.
        // Para resumos sob demanda, considere uma tool 'summarize_context' explícita.)
        await System.Threading.Tasks.Task.CompletedTask;

        // Sempre notifica a interface
        NotifyTokenCount(userLevel);
    }
}
