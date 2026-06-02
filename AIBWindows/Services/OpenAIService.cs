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

    // System prompt comprimido (~120 tokens vs ~250 antes). Modelos pequenos respondem
    // melhor a regras curtas e diretivas. Encoraja:
    //   - <think>...</think> para raciocínio explícito (preservado no histórico p/ continuidade)
    //   - parallel tool calls quando aplicável (reduz # de iterações)
    private const string SYSTEM_PROMPT =
        """
        Você é o AIB, agente local de IA no Windows do usuário.

        Operação: pense brevemente (use <think>...</think> para raciocinar), execute as ferramentas necessárias, responda.

        Regras:
        - Antes de dizer "não sei", chame manage_memory(action=recall).
        - As ferramentas DEVEM ser chamadas OBRIGATORIAMENTE usando a funcionalidade de Function Calling (JSON) nativa da API. NUNCA escreva blocos como '<execute_tool>' ou código de ferramenta como texto livre na sua resposta.
        - NUNCA tente gerar todo o projeto (HTML/CSS/JS) de uma vez. Use a ferramenta nativa para criar UM único arquivo por vez, espere o resultado, e só então crie o próximo arquivo.
        - Se uma ferramenta falhar (ex: falta de parâmetro), leia o erro, corrija o JSON da ferramenta e tente mais UMA vez.
        - Aja sem pedir permissão. Responda em Português (Brasil), conciso.
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
            // num_ctx=16384: ajustado de 8192 após observação de que respostas vazias
            // ("Ação executada com sucesso") ocorrem quando o histórico + tool results
            // somam ~5500 tokens e gemma4 decide não gerar resposta por falta de espaço.
            // Gemma4:e2b suporta nominalmente 131k; 16k é equilíbrio entre folga e custo
            // de prefill em CPU. Se sentir lentidão excessiva, voltar para 12288 ou 8192.
            // Ollama mantém esse num_ctx para todas as chamadas enquanto keep_alive=-1.
            var payload = new {
                model = settings.ModelName,
                keep_alive = -1,
                options = new { num_ctx = 16384 }
            };
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
        NotifyTokenCount(userLevel); // Atualiza contador na UI assim que usuário envia mensagem
        var tools = _toolRegistry.GetActiveTools(userLevel);
        
        bool requiresAction = true;
        // maxLoops 18: permite tarefas multi-passo. Antes era 5 (muito restritivo —
        // qualquer tarefa que precisasse de "ler 6 arquivos antes de decidir" abortava).
        // Com gemma4:e2b (5.1B + thinking) consegue manter coerência por ~20 iterações.
        int maxLoops = 18;

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
            // Agregado por Index (a chave estável entre chunks parciais de uma mesma tool call).
            // ToolCallId e FunctionName só vêm no PRIMEIRO chunk de cada tool; chunks seguintes
            // trazem só mais FunctionArgumentsUpdate. Agregar por ToolCallId perdia argumentos.
            var toolCallsByIndex = new Dictionary<int, ToolCallAccumulator>();
            string? pendingTextAction = null; // Fallback: ação escrita como texto

            // State machine de 3 modos para lidar com chat templates "Harmony" (gemma4 e similares):
            //   Streaming    -> texto vai direto ao usuário (após strip de tokens)
            //   InsideThink  -> dentro de <think>...</think>, descarta para technical
            //   WaitingFinal -> após </think>, bufferiza até ver <channel|>/<|message|>.
            //                   Esse intervalo é o canal "analysis/commentary" do gemma4 —
            //                   conteúdo de pre-final que parece pensamento estruturado mas
            //                   NÃO é a resposta de fato. Descarta o buffer ao ver o marker
            //                   de canal final; emite ao usuário só o que vem depois.
            //
            //   Se o stream terminar AINDA em WaitingFinal (modelo sem channels, ex: qwen
            //   puro), o buffer é a resposta real e é emitido no fim.
            //   Se terminar em InsideThink (modelo abriu <think> e nunca fechou — bug),
            //   também emitimos o conteúdo "think" como fallback para não engolir tudo.
            int mode = 0; // 0=Streaming, 1=InsideThink, 2=WaitingFinal
            var waitBuffer = new System.Text.StringBuilder();
            var thinkBuffer = new System.Text.StringBuilder(); // só para fallback se </think> nunca chegar
            bool anythingYielded = false;
            string[] finalChannelMarkers = { "<channel|>", "<|channel|>", "<|message|>" };

            // Carry buffer dinâmico: alguns chunks do streaming chegam pequenos e podem
            // partir markers (ex: "<th" + "ink>conteudo"). Antes do yield, calculamos se
            // o fim do texto acumulado parece PREFIXO de algum marker que vigiamos —
            // se sim, retemos no carry. Lógica de tamanho fixo (ex: 12 chars) falhava
            // porque chunks muito pequenos faziam o conteúdo do marker chegar fragmentado
            // ao yield antes da state machine conseguir reconstruí-lo.
            string carry = "";
            string[] markersToWatch = { "<think>", "</think>", "<channel|>", "<|channel|>", "<|message|>" };

            // Diagnóstico do stream. Em modo normal, só o [STREAM-END] resumido sai.
            // Em modo verbose (Configurações → "Logs detalhados no console"), saem também
            // contadores por tipo de update e [STREAM-DBG] do primeiro update relevante.
            bool verboseLogging = _settingsService.LoadSettings().VerboseConsoleLogging;
            int updateCount = 0;
            int updatesWithContent = 0;
            int updatesWithTools = 0;
            int updatesEmpty = 0;
            int rawTextChars = 0;
            string? finishReason = null;
            bool firstUpdateLogged = false;

            int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
            int baselineTokens = CalculateCurrentTokens();

            await foreach (var update in updates.WithCancellation(ct))
            {
                updateCount++;
                if (update.FinishReason.HasValue) finishReason = update.FinishReason.Value.ToString();

                if (verboseLogging)
                {
                    int contentParts = update.ContentUpdate?.Count ?? 0;
                    int toolUpdates = update.ToolCallUpdates?.Count ?? 0;
                    if (contentParts > 0) updatesWithContent++;
                    if (toolUpdates > 0) updatesWithTools++;
                    if (contentParts == 0 && toolUpdates == 0) updatesEmpty++;
                    if (contentParts > 0)
                    {
                        foreach (var part in update.ContentUpdate)
                            if (!string.IsNullOrEmpty(part.Text)) rawTextChars += part.Text.Length;
                    }

                    if (!firstUpdateLogged && (contentParts > 0 || toolUpdates > 0))
                    {
                        var dbg = new System.Text.StringBuilder();
                        dbg.Append($"[STREAM-DBG] update#{updateCount}: contentParts={contentParts} toolUpdates={toolUpdates}");
                        if (contentParts > 0)
                        {
                            for (int i = 0; i < update.ContentUpdate.Count; i++)
                            {
                                var p = update.ContentUpdate[i];
                                string preview = p.Text?.Length > 0
                                    ? p.Text.Substring(0, Math.Min(40, p.Text.Length)).Replace("\n", "\\n")
                                    : "(no text)";
                                dbg.Append($" | part{i} Kind={p.Kind} TextLen={p.Text?.Length ?? 0} \"{preview}\"");
                            }
                        }
                        Console.WriteLine(dbg.ToString());
                        firstUpdateLogged = true;
                    }
                }
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
                            continue;
                        }

                        // Combina com o carry e retém no novo carry só o que parece prefixo
                        // de marker (ou nada se o fim for texto comum).
                        string combined = carry + chunk;
                        int retain = FindTrailingMarkerPrefix(combined, markersToWatch);
                        string remaining = combined.Substring(0, combined.Length - retain);
                        carry = retain > 0 ? combined.Substring(combined.Length - retain) : "";
                        while (remaining.Length > 0)
                        {
                            if (mode == 1) // InsideThink
                            {
                                int closeIdx = remaining.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                                if (closeIdx < 0)
                                {
                                    onTechnicalContent?.Invoke(remaining);
                                    thinkBuffer.Append(remaining); // guarda para fallback caso </think> nunca chegue
                                    remaining = "";
                                }
                                else
                                {
                                    int closeLen = "</think>".Length;
                                    onTechnicalContent?.Invoke(remaining.Substring(0, closeIdx + closeLen));
                                    thinkBuffer.Clear(); // think fechou normalmente, descarta fallback
                                    remaining = remaining.Substring(closeIdx + closeLen);
                                    mode = 2; // entra em WaitingFinal
                                }
                            }
                            else if (mode == 2) // WaitingFinal — bufferiza até ver marker de canal final
                            {
                                int markerIdx = -1;
                                int markerLen = 0;
                                foreach (var m in finalChannelMarkers)
                                {
                                    int idx = remaining.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                                    if (idx >= 0 && (markerIdx < 0 || idx < markerIdx))
                                    {
                                        markerIdx = idx;
                                        markerLen = m.Length;
                                    }
                                }

                                if (markerIdx < 0)
                                {
                                    // Sem marker neste chunk — bufferiza, mostra no console
                                    waitBuffer.Append(remaining);
                                    onTechnicalContent?.Invoke(remaining);
                                    remaining = "";
                                }
                                else
                                {
                                    // Marker encontrado: descarta buffer e tudo antes (pre-final)
                                    if (markerIdx > 0)
                                        onTechnicalContent?.Invoke(remaining.Substring(0, markerIdx));
                                    waitBuffer.Clear();
                                    remaining = remaining.Substring(markerIdx + markerLen);
                                    mode = 0; // volta para Streaming — agora é canal final
                                }
                            }
                            else // mode == 0, Streaming
                            {
                                int thinkIdx = remaining.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                                if (thinkIdx < 0)
                                {
                                    string userPart = StripTemplateTokens(remaining);
                                    if (!string.IsNullOrEmpty(userPart))
                                    {
                                        anythingYielded = true;
                                        yield return userPart;
                                    }
                                    remaining = "";
                                }
                                else
                                {
                                    if (thinkIdx > 0)
                                    {
                                        string userPart = StripTemplateTokens(remaining.Substring(0, thinkIdx));
                                        if (!string.IsNullOrEmpty(userPart))
                                        {
                                            anythingYielded = true;
                                            yield return userPart;
                                        }
                                    }
                                    onTechnicalContent?.Invoke("<think>");
                                    remaining = remaining.Substring(thinkIdx + "<think>".Length);
                                    mode = 1; // entra em InsideThink
                                }
                            }
                        }
                    }
                }

                // ── Processamento de native tool call updates ─────────────────
                // Indexa por tcUpdate.Index (estável entre chunks). Preenche Id/Name
                // só quando ainda vazios — chunks subsequentes podem trazê-los nulos.
                foreach (var tcUpdate in update.ToolCallUpdates)
                {
                    int idx = tcUpdate.Index;
                    if (!toolCallsByIndex.TryGetValue(idx, out var entry))
                    {
                        entry = new ToolCallAccumulator();
                        toolCallsByIndex[idx] = entry;
                    }
                    if (string.IsNullOrEmpty(entry.Id) && !string.IsNullOrEmpty(tcUpdate.ToolCallId))
                        entry.Id = tcUpdate.ToolCallId;
                    if (string.IsNullOrEmpty(entry.Name) && !string.IsNullOrEmpty(tcUpdate.FunctionName))
                        entry.Name = tcUpdate.FunctionName;
                    if (tcUpdate.FunctionArgumentsUpdate != null)
                        entry.ArgsBuilder.Append(tcUpdate.FunctionArgumentsUpdate.ToString());
                }
                
                // Atualização em tempo real do contador de tokens durante o stream
                if (updateCount % 10 == 0 || update.FinishReason.HasValue)
                {
                    int streamedTokens = _tokenizer.CountTokens(fullResponse);
                    foreach (var tc in toolCallsByIndex.Values)
                    {
                        streamedTokens += _tokenizer.CountTokens(tc.ArgsBuilder.ToString());
                    }
                    OnTokenCountChanged?.Invoke(baselineTokens + streamedTokens, maxTokens);
                }
            }

            // Processa o carry final (o que sobrou retido pra evitar quebra de marker).
            // Roda a state machine mais uma vez sobre ele.
            if (carry.Length > 0)
            {
                string remaining = carry;
                carry = "";
                while (remaining.Length > 0)
                {
                    if (mode == 1) // InsideThink
                    {
                        int closeIdx = remaining.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                        if (closeIdx < 0)
                        {
                            onTechnicalContent?.Invoke(remaining);
                            thinkBuffer.Append(remaining);
                            remaining = "";
                        }
                        else
                        {
                            int closeLen = "</think>".Length;
                            onTechnicalContent?.Invoke(remaining.Substring(0, closeIdx + closeLen));
                            thinkBuffer.Clear();
                            remaining = remaining.Substring(closeIdx + closeLen);
                            mode = 2;
                        }
                    }
                    else if (mode == 2) // WaitingFinal
                    {
                        int markerIdx = -1;
                        int markerLen = 0;
                        foreach (var m in finalChannelMarkers)
                        {
                            int idx = remaining.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0 && (markerIdx < 0 || idx < markerIdx)) { markerIdx = idx; markerLen = m.Length; }
                        }
                        if (markerIdx < 0)
                        {
                            waitBuffer.Append(remaining);
                            onTechnicalContent?.Invoke(remaining);
                            remaining = "";
                        }
                        else
                        {
                            if (markerIdx > 0) onTechnicalContent?.Invoke(remaining.Substring(0, markerIdx));
                            waitBuffer.Clear();
                            remaining = remaining.Substring(markerIdx + markerLen);
                            mode = 0;
                        }
                    }
                    else // Streaming
                    {
                        int thinkIdx = remaining.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                        if (thinkIdx < 0)
                        {
                            string userPart = StripTemplateTokens(remaining);
                            if (!string.IsNullOrEmpty(userPart)) { anythingYielded = true; yield return userPart; }
                            remaining = "";
                        }
                        else
                        {
                            if (thinkIdx > 0)
                            {
                                string userPart = StripTemplateTokens(remaining.Substring(0, thinkIdx));
                                if (!string.IsNullOrEmpty(userPart)) { anythingYielded = true; yield return userPart; }
                            }
                            onTechnicalContent?.Invoke("<think>");
                            remaining = remaining.Substring(thinkIdx + "<think>".Length);
                            mode = 1;
                        }
                    }
                }
            }

            // Resumo de cada iteração do ReAct. Versão expandida sob VerboseConsoleLogging.
            if (verboseLogging)
            {
                Console.WriteLine($"[STREAM-END] updates={updateCount} (content={updatesWithContent} tools={updatesWithTools} empty={updatesEmpty}) rawText={rawTextChars}ch finish={finishReason ?? "none"} mode={mode} yielded={anythingYielded} wait={waitBuffer.Length}ch think={thinkBuffer.Length}ch full={fullResponse.Length}ch tools={toolCallsByIndex.Count}");
            }
            else
            {
                Console.WriteLine($"[STREAM-END] updates={updateCount} finish={finishReason ?? "none"} mode={mode} full={fullResponse.Length}ch tools={toolCallsByIndex.Count} yielded={anythingYielded}");
            }

            // Flush em camadas (do mais provável ao fallback de último recurso):
            //
            // 1. Terminou em WaitingFinal (modelo usou <think> mas nunca emitiu <channel|>):
            //    o buffer É a resposta real. Acontece com qwen com thinking, modelos sem
            //    harmony, ou gemma4 quando responde sem channels.
            if (mode == 2 && waitBuffer.Length > 0)
            {
                string flushed = StripTemplateTokens(waitBuffer.ToString());
                if (!string.IsNullOrEmpty(flushed))
                {
                    anythingYielded = true;
                    yield return flushed;
                }
                waitBuffer.Clear();
            }
            // 2. Terminou em InsideThink (modelo abriu <think> e nunca fechou — bug do modelo):
            //    o conteúdo do think era na verdade a resposta. Vaza para o user como
            //    último recurso para não engolir resposta. Melhor mostrar "pensamento bruto"
            //    do que mostrar "Ação executada com sucesso".
            else if (mode == 1 && thinkBuffer.Length > 0 && !anythingYielded && toolCallsByIndex.Count == 0)
            {
                string flushed = StripTemplateTokens(thinkBuffer.ToString());
                if (!string.IsNullOrEmpty(flushed))
                {
                    Console.WriteLine("[STREAM-END] Fallback: <think> nunca fechou, emitindo conteúdo do think como resposta.");
                    anythingYielded = true;
                    yield return flushed;
                }
            }
            thinkBuffer.Clear();

            // ── Execução: Native Tool Calls (Modo Primário) ───────────────────
            if (toolCallsByIndex.Count > 0)
            {
                requiresAction = true;

                // Ordena pelo Index (ordem original do modelo)
                var orderedCalls = toolCallsByIndex
                    .OrderBy(kv => kv.Key)
                    .Select(kv => kv.Value)
                    .ToList();

                // Monta a mensagem assistant com TODAS as tool_calls de uma vez —
                // essencial pro histórico ReAct: cada ToolCall id precisa estar
                // referenciado por uma ToolMessage com o mesmo id depois.
                var chatToolCalls = orderedCalls
                    .Select(tc => ChatToolCall.CreateFunctionToolCall(tc.Id, tc.Name, BinaryData.FromString(tc.ArgsBuilder.ToString())))
                    .ToList();
                _history.Add(ChatMessage.CreateAssistantMessage(chatToolCalls));

                // Log de TODAS as chamadas antes de executar (deixa claro o paralelismo)
                if (orderedCalls.Count > 1)
                    onTechnicalContent?.Invoke($"\n[PARALELO] Executando {orderedCalls.Count} ferramentas em paralelo:\n");
                foreach (var tc in orderedCalls)
                    onTechnicalContent?.Invoke($"[FERRAMENTA] Nome: {tc.Name} | Args: {tc.ArgsBuilder}\n");

                // EXECUÇÃO PARALELA: dispara todas e espera o conjunto.
                // Vantagem em CPU lenta: 3 leituras de arquivo independentes rodam concorrente
                // em vez de seriadas. Cada ExecuteToolAsync é async, então o Task.WhenAll
                // explora o paralelismo do scheduler .NET.
                var tasks = orderedCalls
                    .Select(tc => ExecuteToolPairedAsync(tc, userLevel))
                    .ToArray();
                var results = await Task.WhenAll(tasks);

                // Processa resultados na ordem original (mesmo que tenham terminado fora de ordem)
                foreach (var (tc, result) in results)
                {
                    onTechnicalContent?.Invoke($"[FERRAMENTA] Resultado ({tc.Name}): {result}\n");
                    _history.Add(ChatMessage.CreateToolMessage(tc.Id, result));

                    // Atualiza a lista de arquivos recentes acessados
                    if (tc.Name == "read_file" || tc.Name == "view_file" || tc.Name == "write_to_file" || tc.Name == "replace_file_content" || tc.Name == "multi_replace_file_content")
                    {
                        try
                        {
                            var dict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(tc.ArgsBuilder.ToString());
                            string? path = null;
                            if (dict != null && dict.ContainsKey("AbsolutePath")) path = dict["AbsolutePath"].GetString();
                            else if (dict != null && dict.ContainsKey("TargetFile")) path = dict["TargetFile"].GetString();
                            else if (dict != null && dict.ContainsKey("path")) path = dict["path"].GetString();

                            if (!string.IsNullOrEmpty(path)) ContextService.AddRecentFile(path);
                        }
                        catch { }
                    }

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
                // Preserva blocos <think>...</think> no HISTÓRICO. Razão: a LLM (gemma4
                // tem capability "thinking" treinada) consegue ver seu próprio raciocínio
                // em iterações futuras, dando continuidade de plano em tarefas multi-passo.
                // Para o USUÁRIO esses blocos não são exibidos — IsOnlyTechnicalContent
                // (filtro do stream) já desvia chunks com <think> para onTechnicalContent.
                _history.Add(ChatMessage.CreateAssistantMessage(fullResponse));

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
        // D-06: use-vault sentinel honors the vault-backed key storage
        if (apiKey == "use-vault")
        {
            apiKey = CredentialService.RetrieveCredential("openai", "ApiKey");
            if (apiKey.StartsWith("ERRO"))
            {
                // Should not reach if D-03 detector ran; treat as deny.
                // OpenAI SDK will fail with auth error → user sees error → re-runs FirstRunWindow.
                apiKey = "placeholder";
            }
        }
        if (string.IsNullOrEmpty(apiKey)) apiKey = "placeholder";

        var options = new OpenAIClientOptions();
        options.NetworkTimeout = System.Threading.Timeout.InfiniteTimeSpan;
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
            // D-06: use-vault sentinel honors the vault-backed key storage
            if (apiKey == "use-vault")
            {
                apiKey = CredentialService.RetrieveCredential("openai", "ApiKey");
                if (apiKey.StartsWith("ERRO"))
                {
                    // Should not reach if D-03 detector ran; treat as deny.
                    // OpenAI SDK will fail with auth error → user sees error → re-runs FirstRunWindow.
                    apiKey = "placeholder";
                }
            }

            if (string.IsNullOrEmpty(apiKey)) apiKey = "placeholder";

            var options = new OpenAIClientOptions();
            options.NetworkTimeout = System.Threading.Timeout.InfiniteTimeSpan;
            if (!string.IsNullOrEmpty(apiUrl)) options.Endpoint = new Uri(apiUrl);
            _client = new ChatClient(modelName, new ApiKeyCredential(apiKey), options);
            _lastModel = modelName;

            Console.WriteLine($"[AI] Cliente inicializado: {modelName} @ {apiUrl}");
        }
    }

    /// <summary>
    /// Agregador de chunks de uma única tool call durante o streaming.
    /// API OpenAI envia ToolCallId/FunctionName apenas no PRIMEIRO chunk de cada call
    /// e FunctionArgumentsUpdate em pedaços. Index é a chave estável entre chunks.
    /// </summary>
    private class ToolCallAccumulator
    {
        public string Id = "";
        public string Name = "";
        public System.Text.StringBuilder ArgsBuilder = new();
    }

    /// <summary>
    /// Executa uma tool call e devolve o par (acumulador, resultado) para preservar
    /// associação durante <see cref="Task.WhenAll{TResult}"/> paralelo.
    /// </summary>
    private async Task<(ToolCallAccumulator Tc, string Result)> ExecuteToolPairedAsync(ToolCallAccumulator tc, int userLevel)
    {
        string result = await _toolRegistry.ExecuteToolAsync(tc.Name, tc.ArgsBuilder.ToString(), userLevel);
        return (tc, result);
    }

    /// <summary>
    /// Remove tokens de chat-template que vazam crus de alguns modelos via Ollama
    /// (especialmente gemma4 e variantes que usam estilo Harmony/channels). Esses
    /// tokens NUNCA devem aparecer para o usuário final.
    ///
    /// Inclui também tags <think>/</think> ÓRFÃS (sem par correspondente). A state
    /// machine principal já cuida dos pares válidos; este filtro pega o caso em que
    /// o modelo emite uma tag solta como artefato (típico do gemma4 quando "muda
    /// de canal" sem fechar adequadamente).
    /// </summary>
    private static readonly string[] ChatTemplateTokens = new[]
    {
        // Gemma4 / Harmony-style channels
        "<channel|>", "<|channel|>",
        "<message|>", "<|message|>",
        "<|return|>",
        // Qwen / generic ChatML
        "<|im_start|>", "<|im_end|>",
        // Llama 3+
        "<|begin_of_text|>", "<|end_of_text|>",
        "<|start_header_id|>", "<|end_header_id|>",
        "<|eot_id|>",
        // GPT
        "<|endoftext|>",
        // Role markers genéricos
        "<|user|>", "<|assistant|>", "<|system|>",
        "<user|>", "<assistant|>", "<system|>",
        // Think tags órfãs (pares válidos são tratados pela state machine antes)
        "<think>", "</think>",
    };

    private static string StripTemplateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var token in ChatTemplateTokens)
        {
            text = text.Replace(token, "", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    /// <summary>
    /// Retorna o tamanho do maior sufixo de <paramref name="text"/> que é prefixo
    /// (de pelo menos 1 char) de algum dos markers em <paramref name="markers"/>.
    /// Usado para manter o carry buffer só pelo tempo necessário: enquanto o fim do
    /// texto acumulado pode ser início de um marker, espera. Quando não pode, processa.
    /// </summary>
    private static int FindTrailingMarkerPrefix(string text, string[] markers)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int maxOverlap = 0;
        foreach (var marker in markers)
        {
            int maxK = Math.Min(marker.Length - 1, text.Length);
            for (int k = maxK; k > maxOverlap; k--)
            {
                if (string.Compare(text, text.Length - k, marker, 0, k, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    maxOverlap = k;
                    break;
                }
            }
        }
        return maxOverlap;
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
            
            if (msg is AssistantChatMessage acm && acm.ToolCalls != null && acm.ToolCalls.Count > 0)
            {
                try
                {
                    string toolCallsJson = System.Text.Json.JsonSerializer.Serialize(acm.ToolCalls);
                    tokens += _tokenizer.CountTokens(toolCallsJson);
                }
                catch { }
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
