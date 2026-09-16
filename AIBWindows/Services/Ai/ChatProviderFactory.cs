using System;
using System.Collections.Generic;
using System.Net.Http;
using AIB.Services;

namespace AIB.Services.Ai;

/// <summary>
/// Fábrica única de providers: Ollama ou OpenRouter, pelo <c>AiProvider</c> das configurações que
/// recebe. Reaproveita a instância enquanto provedor, modelo, URL e CREDENCIAL efetiva não mudarem.
/// <para>
/// Guarda UMA instância por combinação, e não só a última: a conversa e a triagem de e-mail podem
/// usar provedores diferentes e se alternam o tempo todo. Com uma só, cada alternância construía
/// um provider novo.
/// </para>
/// </summary>
public sealed class ChatProviderFactory : IChatProviderFactory
{
    private readonly HttpClient _httpClient;
    private readonly IToolCallHealer _healer;

    private readonly object _gate = new();
    private readonly Dictionary<(string Provider, string Model, string ApiUrl, string Credential, bool SemColeta, string ProvedorFixo), IChatProvider> _cache = new();

    private readonly Func<string, string> _chaveDe;

    /// <param name="chaveDe">
    /// Quem lê a chave de um provedor. Nulo lê do cofre (<see cref="ChaveDe"/>); o ensaio passa a
    /// sua, para não tocar no cofre real do usuário.
    /// </param>
    public ChatProviderFactory(HttpClient httpClient, IToolCallHealer healer, Func<string, string>? chaveDe = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _healer = healer ?? throw new ArgumentNullException(nameof(healer));
        _chaveDe = chaveDe ?? ChaveDe;
    }

    public IChatProvider GetProvider(UserAppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        string provedor = ProvedoresDeIa.Normalizar(settings.AiProvider, settings.ApiUrl);
        if (provedor.Length == 0) provedor = ProvedoresDeIa.Ollama;

        // A credencial EFETIVA entra na chave: trocar a chave no cofre não muda nada nas
        // configurações, e uma chave de cache sem ela reaproveitaria um cliente com a chave velha
        // — o 401 que só sumia reiniciando o app.
        string credencial = _chaveDe(provedor);
        bool openRouter = provedor == ProvedoresDeIa.OpenRouter;
        var chave = (provedor, settings.ModelName ?? "", settings.ApiUrl ?? "", credencial,
                     openRouter && settings.OpenRouterSemColetaDeDados,
                     openRouter ? (settings.OpenRouterProvedorFixo ?? "").Trim() : "");

        // O aquecimento, um turno e a triagem podem chegar juntos na inicialização.
        lock (_gate)
        {
            if (_cache.TryGetValue(chave, out var existente)) return existente;

            var novo = Build(provedor, settings, credencial);
            _cache[chave] = novo;
            return novo;
        }
    }

    /// <summary>
    /// A chave do provedor, lida do cofre DELE — sem a busca global, que devolveria a chave de
    /// outro serviço. Vazio para quem não usa chave ou quando ela não foi configurada: a requisição
    /// sai sem autorização e o provider devolve o 401 com a frase que diz onde configurar.
    /// </summary>
    public static string ChaveDe(string provedor)
    {
        string? sistema = ProvedoresDeIa.SistemaDaChave(provedor);
        return sistema == null
            ? ""
            : CredentialService.LerDoSistema(sistema, ProvedoresDeIa.NomeDaChave) ?? "";
    }

    private IChatProvider Build(string provedor, UserAppSettings settings, string credencial)
    {
        string modelo = settings.ModelName ?? "";

        if (provedor == ProvedoresDeIa.OpenRouter)
        {
            Console.WriteLine($"[AI] Cliente inicializado: {modelo} @ {ProvedoresDeIa.UrlDoOpenRouter}"
                              + (credencial.Length == 0 ? " (SEM CHAVE configurada)" : ""));
            return new OpenRouterProvider(
                _httpClient, ProvedoresDeIa.UrlDoOpenRouter, credencial, modelo, _healer,
                settings.VerboseConsoleLogging, settings.OpenRouterSemColetaDeDados,
                settings.OpenRouterProvedorFixo);
        }

        string ollamaUrl = string.IsNullOrEmpty(settings.ApiUrl) ? ProvedoresDeIa.UrlDoOllama : settings.ApiUrl;

        // Evita a resolução IPv6 de "localhost", que causa timeouts de 2 minutos.
        ollamaUrl = ollamaUrl.Replace("localhost", "127.0.0.1");

        string baseUrl = ollamaUrl.Replace("/v1", "").TrimEnd('/');
        var ollamaClient = new OllamaNativeClient(baseUrl, _httpClient);

        Console.WriteLine($"[AI] Cliente inicializado: {modelo} @ {baseUrl}");
        return new OllamaProvider(ollamaClient, baseUrl, modelo, _healer, _httpClient, settings.VerboseConsoleLogging);
    }
}
