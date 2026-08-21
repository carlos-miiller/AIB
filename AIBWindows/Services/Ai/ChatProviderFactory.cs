using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using System.Threading;
using AIB.Services;
using OpenAI;
using OpenAI.Chat;

namespace AIB.Services.Ai;

/// <summary>
/// Fábrica única de providers. Concentra a seleção de provider e a normalização de
/// URL/credencial que antes viviam espalhadas dentro de EnsureClient e AskStatelessAsync.
/// Reaproveita a instância enquanto provider, modelo, URL e CREDENCIAL efetiva não mudarem.
/// </summary>
public sealed class ChatProviderFactory : IChatProviderFactory
{
    private readonly HttpClient _httpClient;
    private readonly IToolCallHealer _healer;

    private readonly object _gate = new();
    private IChatProvider? _cached;
    private (string Provider, string Model, string ApiUrl, string Credential) _cachedKey;

    public ChatProviderFactory(HttpClient httpClient, IToolCallHealer healer)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _healer = healer ?? throw new ArgumentNullException(nameof(healer));
    }

    public IChatProvider GetProvider(UserAppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        // A credencial EFETIVA entra na chave. O sentinela "use-vault" nunca muda quando o
        // usuário troca a chave (FirstRunWindow grava no cofre e mantém o sentinela), então
        // uma chave sem a credencial reaproveitaria um cliente morto: era o 401 que só sumia
        // reiniciando o app.
        string credential = settings.AiProvider == "Ollama" ? "" : ResolveCredential(settings);
        var key = (settings.AiProvider ?? "", settings.ModelName ?? "", settings.ApiUrl ?? "", credential);

        // O aquecimento e um turno podem chegar juntos na inicialização: o lock evita
        // dois clientes concorrentes para o mesmo modelo.
        lock (_gate)
        {
            if (_cached != null && _cachedKey == key) return _cached;

            _cached = Build(settings, credential);
            _cachedKey = key;
            return _cached;
        }
    }

    /// <summary>
    /// Credencial efetiva do caminho OpenAI. D-06: o sentinela "use-vault" indica chave
    /// guardada no cofre do Windows; um resultado de erro vira "placeholder" para o SDK
    /// falhar com erro de autenticação e o usuário refazer o onboarding.
    /// </summary>
    private static string ResolveCredential(UserAppSettings settings)
    {
        string apiKey = settings.ApiKey ?? "";

        if (apiKey == "use-vault")
        {
            apiKey = CredentialService.RetrieveCredential("openai", "ApiKey");
            if (apiKey.StartsWith("ERRO", StringComparison.Ordinal)) apiKey = "placeholder";
        }

        return string.IsNullOrEmpty(apiKey) ? "placeholder" : apiKey;
    }

    private IChatProvider Build(UserAppSettings settings, string credential)
    {
        if (settings.AiProvider == "Ollama")
        {
            string ollamaUrl = string.IsNullOrEmpty(settings.ApiUrl)
                ? "http://127.0.0.1:11434"
                : settings.ApiUrl;

            // Evita a resolução IPv6 de "localhost", que causa timeouts de 2 minutos.
            ollamaUrl = ollamaUrl.Replace("localhost", "127.0.0.1");

            string baseUrl = ollamaUrl.Replace("/v1", "").TrimEnd('/');
            var ollamaClient = new OllamaNativeClient(baseUrl, _httpClient);

            Console.WriteLine($"[AI] Cliente inicializado: {settings.ModelName} @ {baseUrl}");
            return new OllamaProvider(
                ollamaClient,
                baseUrl,
                settings.ModelName ?? "",
                _healer,
                _httpClient,
                settings.VerboseConsoleLogging);
        }

        string model = settings.ModelName ?? "";
        string apiUrl = settings.ApiUrl ?? "";

        var options = new OpenAIClientOptions
        {
            // Timeout infinito: quem controla o prazo é o CancellationToken do turno — o
            // padrão de 100s abortava o aquecimento e as respostas longas.
            NetworkTimeout = Timeout.InfiniteTimeSpan,
            Transport = new HttpClientPipelineTransport(_httpClient)
        };
        if (!string.IsNullOrEmpty(apiUrl)) options.Endpoint = new Uri(apiUrl);

        var client = new ChatClient(model, new ApiKeyCredential(credential), options);

        Console.WriteLine($"[AI] Cliente inicializado: {model} @ {apiUrl}");
        return new OpenAiProvider(client, model, _healer, settings.VerboseConsoleLogging);
    }
}
