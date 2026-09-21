using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services.Ai;
using OpenAI.Chat;

namespace AIB.Services.Agent;

/// <summary>
/// Aquecimento intencional (doc 03 §1): carrega o modelo com o keep-alive em vigor e compila
/// a gramática das ferramentas com um heartbeat fantasma, mascarando a latência de carga.
/// NÃO é disparado por construtor — o App chama explicitamente depois que a UI existe.
/// Todo o trabalho acontece sobre um EphemeralMessageStore: o histórico vivo nunca é
/// escrito aqui, o que elimina o RemoveRange que apagava mensagens de uma requisição
/// concorrente do usuário.
/// </summary>
public sealed class WarmupService
{
    private const string HeartbeatMessage =
        "[SYSTEM_HEARTBEAT] O sistema acabou de iniciar. Responda apenas 'SISTEMA ONLINE'. Não use nenhuma ferramenta.";

    private static readonly IReadOnlyList<ChatTool> NoTools = Array.Empty<ChatTool>();

    private readonly SettingsService _settingsService;
    private readonly ToolRegistry _toolRegistry;
    private readonly IChatProviderFactory _providerFactory;
    private readonly TokenCounter _tokenCounter;

    public WarmupService(
        SettingsService settingsService,
        ToolRegistry toolRegistry,
        IChatProviderFactory providerFactory,
        TokenCounter tokenCounter)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
    }

    /// <summary>Disparado no início e no fim do aquecimento (true/false).</summary>
    public event Action<bool>? OnWarmupStateChanged;

    /// <summary>Roda o aquecimento inteiro sobre uma cópia descartável. Nunca lança.</summary>
    public async Task RunAsync(IReadOnlyList<ChatMessage> historySeed, CancellationToken ct)
    {
        bool locked = false;
        try
        {
            var settings = _settingsService.LoadSettings();
            // Só o Ollama tem modelo a carregar nesta máquina. No OpenRouter o heartbeat seria
            // uma requisição paga, e a interface travada esperando nada.
            // Provedor vazio é Ollama, como na fábrica.
            if (ProvedoresDeIa.Normalizar(settings.AiProvider, settings.ApiUrl) == ProvedoresDeIa.OpenRouter) return;

            var provider = _providerFactory.GetProvider(settings);

            // 1. Carrega o modelo (POST /api/generate com o keep-alive em vigor).
            await provider.WarmupAsync(ct).ConfigureAwait(false);

            // Trava a interface só depois da carga, como sempre foi.
            locked = true;
            OnWarmupStateChanged?.Invoke(true);

            // 2. Compila a gramática das ferramentas sobre uma CÓPIA do histórico real,
            // para o prefix match do Ollama bater sem tocar na conversa viva.
            Console.WriteLine("[WARMUP] Compilando gramática das Nativas no histórico oficial...");

            var store = new EphemeralMessageStore(historySeed, _tokenCounter);
            store.AppendUserMessage(HeartbeatMessage);

            int userLevel = LevelService.GetLevel(settings.MessageCount);
            IReadOnlyList<ChatTool> tools = settings.EnableIntelligentTools
                ? _toolRegistry.GetActiveTools(userLevel)
                : NoTools;

            var result = await provider.CompleteAsync(
                store.Snapshot(),
                tools,
                // O keep-alive da tela, que o Default já traz. Era -1 fixo aqui: quem escolhia
                // "5 minutos" tinha o modelo travado na memória desde a abertura do app.
                ChatRequestOptions.Default,
                ct).ConfigureAwait(false);

            if (result.PromptEvalCount.HasValue)
            {
                int totalTokens = store.CountTokens();
                int reaproveitados = Math.Max(0, totalTokens - result.PromptEvalCount.Value);
                Console.WriteLine(
                    $"[WARMUP] Prefill: {result.PromptEvalCount.Value} de {totalTokens} tokens reprocessados "
                    + $"({reaproveitados} vieram do cache de prefixo).");
            }

            Console.WriteLine($"[WARMUP] Gramática em cache! Resposta final: {result.Text}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARMUP ERRO] {ex.Message}");
        }
        finally
        {
            // Libera a interface apenas se chegamos a travá-la.
            if (locked) OnWarmupStateChanged?.Invoke(false);
        }
    }
}
