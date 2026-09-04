using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Linq;
using System.Text;

namespace AIB.Services;

public sealed class UserAppSettings
{
    public string ApiUrl { get; set; } = "http://127.0.0.1:11434/v1";
    public string ApiKey { get; set; } = "ollama";
    public string ModelName { get; set; } = "qwen2.5:7b";
    public string ActiveCharacter { get; set; } = "Ayano";
    public string KeepAlive { get; set; } = "5m";
    public string AiProvider { get; set; } = ""; // Vazio por default força a tela de Onboarding
    public string ShadowModelName { get; set; } = "qwen2.5:7b";
    public bool SendSystemPrompt { get; set; } = true;
    public bool EnableIntelligentTools { get; set; } = true;
    // Opt-in: a funcionalidade Shadow Assistant fica desligada por default.
    // Quando ligado, o botão do olho aparece no chat e o usuário decide quando ativar.
    public bool ShadowAssistantEnabled { get; set; } = false;

    /// <summary>
    /// Se o orbe pode ler e triar a caixa de entrada.
    /// <para>
    /// Opt-in pelo mesmo motivo que o orbe: ninguém ganha um programa lendo o próprio e-mail
    /// por ter atualizado. E é uma chave SEPARADA da do orbe de propósito — querer a bola no
    /// desktop não é querer que ela abra a caixa de entrada, e amarrar as duas tiraria do
    /// usuário a única decisão que ele realmente precisa tomar aqui.
    /// </para>
    /// </summary>
    public bool ShadowHandlesMail { get; set; } = false;
    // Quando ativo, o console mostra logs detalhados do streaming ReAct
    // (STREAM-DBG, contadores de updates, classificação de chunks).
    // Útil para diagnosticar respostas vazias ou comportamento estranho do modelo.
    public bool VerboseConsoleLogging { get; set; } = false;
    public string DataDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIB");

    // Diretórios
    public string DataDirectory { get; set; } = "";
    public string TempDirectory { get; set; } = "";

    // Avançado

    /// <summary>
    /// Controla apenas o denylist pós-modal de <c>run_command</c>; NÃO controla o modal em si.
    /// Modal sempre dispara em run_command (independente desta flag).
    ///
    /// ON (default): o denylist roda como segunda camada após o modal em níveis &lt; 9.
    /// OFF: denylist é ignorado; o modal é o único portão.
    /// L9: denylist sempre ignorado, independente desta flag (D5).
    ///
    /// Migração: perfis legados sem este campo desserializam para o C# default <c>true</c>
    /// automaticamente via <see cref="System.Text.Json.JsonSerializer"/>.
    /// </summary>
    public bool ConfirmDangerousCommands { get; set; } = true;

    // ─────────────────────────────────────────────────────────────────────
    // Valores que eram constantes no código
    //
    // Cada um traz o padrão como const NOMEADA, e não como literal solto no
    // inicializador. O botão "Restaurar padrões" de cada página, os ensaios e o
    // texto de ajuda leem a MESMA const — três lugares que, com o número
    // digitado em cada um, sairiam de sincronia na primeira mudança.
    // ─────────────────────────────────────────────────────────────────────

    public const int PadraoDeIteracoes = 18;

    /// <summary>
    /// Teto de passos do laço ReAct num turno. Estourar não é sucesso silencioso: o usuário vê
    /// o corte. 18 permite tarefas multi-passo — com 5 já abortava em "ler 6 arquivos antes de
    /// decidir" — e por volta de 20 a coerência do modelo pequeno começa a cair.
    /// </summary>
    public int MaxTurnIterations { get; set; } = PadraoDeIteracoes;

    public const double PadraoDoGatilhoDeCompactacao = 0.85;

    /// <summary>
    /// Fração da cota viva a partir da qual a conversa é compactada. Não é 1,0 de propósito: o
    /// gatilho precisa disparar ANTES do estouro, senão a poda de emergência entra primeiro e
    /// come as mensagens que o capítulo iria resumir.
    /// </summary>
    public double CompactionTrigger { get; set; } = PadraoDoGatilhoDeCompactacao;

    public const double PadraoDaFatiaDeMemoria = 0.25;

    /// <summary>Quanto do contexto disponível é reservado para memória.</summary>
    public double MemoryFraction { get; set; } = PadraoDaFatiaDeMemoria;

    public const int PadraoDeEmailsNoShadow = 3;

    /// <summary>
    /// Quantos e-mails cabem na fala do Shadow antes de o resto virar uma linha de texto. Uma
    /// caixa de entrada inteira flutuando sobre o desktop não é o produto.
    /// </summary>
    public int ShadowMailPreviewCount { get; set; } = PadraoDeEmailsNoShadow;

    public const int PadraoDaJanelaDeEmailEmDias = 3;

    /// <summary>
    /// Quantos dias para trás a varredura olha. Triar backlog é trabalho jogado fora: ninguém
    /// lê 300 pendências de três meses.
    /// </summary>
    public int MailWindowDays { get; set; } = PadraoDaJanelaDeEmailEmDias;

    public const int PadraoDoTempoLimiteImapEmSegundos = 15;

    /// <summary>Quanto se espera por caixa antes de desistir. Servidor mudo não segura a tela.</summary>
    public int MailTimeoutSeconds { get; set; } = PadraoDoTempoLimiteImapEmSegundos;

    // Gamificação / Sistema de Níveis
    public int MessageCount { get; set; } = 0;

    /// <summary>
    /// Caixas de e-mail configuradas — tela-configuracoes §3.12, uma linha da lista por item.
    /// <para>
    /// SEM SENHA. A senha de app de cada caixa vive no <c>MailVault</c>, indexada pelo
    /// endereço, e nunca passa por aqui: este objeto é serializado em JSON e clonado a cada
    /// <c>LoadSettings</c>, dois caminhos por onde uma senha não deve trafegar.
    /// </para>
    /// </summary>
    public List<MailAccountSettings> MailAccounts { get; set; } = new();

    /// <summary>
    /// Cópia para entregar a quem pediu as configurações.
    /// <para>
    /// Era cópia rasa, e a justificativa era "todos os campos são string ou tipo de valor".
    /// Deixou de valer com <see cref="MailAccounts"/>: o <c>MemberwiseClone</c> copia a
    /// REFERÊNCIA da lista, e como o <c>LoadSettings</c> devolve um clone do cache, quem
    /// mexesse na lista recebida estaria mexendo na lista do cache — e na das outras janelas.
    /// </para>
    /// </summary>
    /// <summary>
    /// Põe os números dentro de faixas em que o programa ainda funciona.
    /// <para>
    /// O arquivo é editável à mão e agora carrega valores que, errados, quebram coisas de
    /// verdade: fração de memória em 0,99 não deixa espaço para conversar, gatilho de
    /// compactação em 0 compacta a cada turno, zero iterações não roda turno nenhum. Corrigir
    /// aqui, na entrada, é mais barato que espalhar defesa por cada consumidor — e nenhum deles
    /// tem contexto para saber o que fazer com um valor absurdo.
    /// </para>
    /// <para>
    /// SANEIA, não recusa: um número fora da faixa vira o mais próximo válido, e o usuário
    /// perde o exagero, não as configurações inteiras.
    /// </para>
    /// </summary>
    public UserAppSettings Sanear()
    {
        MaxTurnIterations = Entre(MaxTurnIterations, 1, 60);
        CompactionTrigger = Entre(CompactionTrigger, 0.50, 0.99);
        MemoryFraction = Entre(MemoryFraction, 0.05, 0.60);
        ShadowMailPreviewCount = Entre(ShadowMailPreviewCount, 1, 10);
        MailWindowDays = Entre(MailWindowDays, 1, 30);
        MailTimeoutSeconds = Entre(MailTimeoutSeconds, 5, 120);
        return this;
    }

    private static int Entre(int valor, int minimo, int maximo) =>
        valor < minimo ? minimo : valor > maximo ? maximo : valor;

    private static double Entre(double valor, double minimo, double maximo) =>
        double.IsNaN(valor) || valor < minimo ? minimo : valor > maximo ? maximo : valor;

    public UserAppSettings Clone()
    {
        var copia = (UserAppSettings)MemberwiseClone();
        copia.MailAccounts = MailAccounts.Select(c => c.Clone()).ToList();
        return copia;
    }
}

/// <summary>
/// O que de uma caixa de e-mail vai para o disco — §9 passo 2 de tela-configuracoes.
/// <para>
/// <c>Status</c> e <c>StatusText</c> NÃO estão aqui de propósito: são de tempo de execução.
/// Gravar "Conectada" e reler isso na abertura seguinte seria afirmar na tela algo que
/// ninguém verificou desde a sessão passada.
/// </para>
/// </summary>
public sealed class MailAccountSettings
{
    public string Address { get; set; } = "";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public bool IsPrimary { get; set; }

    public MailAccountSettings Clone() => (MailAccountSettings)MemberwiseClone();
}

public sealed class SettingsService
{
    // Um HttpClient por processo: a classe era instanciada várias vezes e alocava um em cada.
    private static readonly HttpClient _httpClient = new HttpClient();

    private readonly string? _explicitPath;
    private readonly object _gate = new();
    private UserAppSettings? _cache;
    private string? _cachedPath;

    /// <summary>Usa o caminho corrente do <see cref="DirectoryService"/>, resolvido a cada operação.</summary>
    public SettingsService() : this(null) { }

    /// <summary>Caminho fixo — usado por testes e por perfis alternativos.</summary>
    public SettingsService(string? settingsPath)
    {
        _explicitPath = string.IsNullOrWhiteSpace(settingsPath) ? null : settingsPath;
    }

    // Resolvido a cada operação de IO. O antigo 'static readonly' congelava o caminho no
    // carregamento do tipo, e por isso DirectoryService.ApplyFromSettings nunca conseguia
    // de fato realocar o arquivo de settings.
    private string ResolvePath() => _explicitPath ?? DirectoryService.SettingsPath;

    /// <summary>Caminho efetivo neste momento.</summary>
    public string SettingsPath => ResolvePath();

    /// <summary>Settings do disco, servidas do cache em memória. Devolve sempre uma cópia.</summary>
    public UserAppSettings LoadSettings()
    {
        string path = ResolvePath();

        lock (_gate)
        {
            if (_cache != null && _cachedPath == path)
                return _cache.Clone();
        }

        var loaded = ReadFromDisk(path);

        lock (_gate)
        {
            _cache = loaded;
            _cachedPath = path;
            return _cache.Clone();
        }
    }

    /// <summary>Descarta o cache. O próximo <see cref="LoadSettings"/> volta ao disco.</summary>
    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cache = null;
            _cachedPath = null;
        }
    }

    private UserAppSettings ReadFromDisk(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new UserAppSettings();
            SaveSettings(defaults);
            return defaults;
        }

        try
        {
            byte[] encryptedBytes = File.ReadAllBytes(path);
            byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(decryptedBytes);
            return (JsonSerializer.Deserialize<UserAppSettings>(json) ?? new UserAppSettings()).Sanear();
        }
        catch
        {
            // Fallback para arquivo em texto claro (Migração de legado)
            try
            {
                string json = File.ReadAllText(path);
                var settings = (JsonSerializer.Deserialize<UserAppSettings>(json)
                                ?? new UserAppSettings()).Sanear();
                SaveSettings(settings); // Re-salva criptografado
                return settings;
            }
            catch
            {
                return new UserAppSettings();
            }
        }
    }

    public void SaveSettings(UserAppSettings settings)
    {
        string path = ResolvePath();
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        byte[] plainBytes = Encoding.UTF8.GetBytes(json);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, encryptedBytes);

        lock (_gate)
        {
            _cache = settings.Clone();
            _cachedPath = path;
        }
    }

    public async Task<List<string>> GetOllamaModelsAsync(string baseUrl)
    {
        string baseOllamaUrl = string.IsNullOrEmpty(baseUrl) ? "http://localhost:11434" : baseUrl;
        baseOllamaUrl = baseOllamaUrl.Replace("/v1", "").TrimEnd('/');
        
        try
        {
            var response = await _httpClient.GetAsync($"{baseOllamaUrl}/api/tags");
            if (!response.IsSuccessStatusCode) return new List<string>();

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out JsonElement modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out JsonElement nameProp))
                    {
                        models.Add(nameProp.GetString() ?? "");
                    }
                }
            }
            return models;
        }
        catch
        {
            return new List<string>(); // Falha (provavelmente não é Ollama ou está offline)
        }
    }
}
