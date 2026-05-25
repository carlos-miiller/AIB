using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AIB.Services;

public class ShadowAssistantService
{
    private readonly DispatcherTimer _timer;
    private OcrService? _ocrService;
    private readonly OpenAIService _openAIService;
    private string _lastOcrText = "";
    
    public bool IsActive { get; private set; }
    
    // Evento disparado quando houver uma sugestão válida do LLM
    public event Action<string>? OnSuggestionReceived;

    private const string SYSTEM_PROMPT = 
        "Você é o Shadow Assistant. O usuário acabou de focar na tela cujo texto bruto de OCR segue abaixo. " +
        "Sua missão: Identificar se o usuário precisa de um resumo, uma sugestão de código, ou a resposta para o problema exibido na tela. " +
        "REGRA DE OURO: Você SÓ deve responder se tiver 100% de certeza absoluta de que sua sugestão é útil e precisa para o contexto atual. " +
        "Se houver qualquer dúvida, ou se os dados na tela forem vagos ou irrelevantes, retorne APENAS a palavra 'NOTHING'. " +
        "Não cumprimente, não explique, seja direto.";

    public ShadowAssistantService(OpenAIService openAIService)
    {
        _openAIService = openAIService;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _timer.Tick += async (s, e) => await ProcessShadowTick();
    }

    public void Start()
    {
        if (!IsActive)
        {
            IsActive = true;
            _timer.Start();
            Console.WriteLine("[SHADOW] Serviço INICIADO.");
        }
    }

    public void Stop()
    {
        if (IsActive)
        {
            IsActive = false;
            _timer.Stop();
            Console.WriteLine("[SHADOW] Serviço PAUSADO.");
        }
    }

    private async Task ProcessShadowTick()
    {
        _timer.Stop();

        try
        {
            // Inicializa lazy para não travar a inicialização do app
            if (_ocrService == null)
            {
                _ocrService = new OcrService();
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SHADOW] Extraindo OCR da tela ativa...");
            string currentText = await _ocrService.ExtractTextFromActiveScreenAsync();
            
            if (string.IsNullOrWhiteSpace(currentText))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SHADOW] Nenhuma tela/texto encontrado.");
                return;
            }

            if (currentText == _lastOcrText)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SHADOW] Tela inalterada (Diff idêntico). Abortando requisição para economizar.");
                return;
            }

            _lastOcrText = currentText;

            // Inicializa configurações
            var settings = new SettingsService().LoadSettings();

            // --- LOGGING NO TERMINAL ---
            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] [SHADOW ASSISTANT] OCR detectou texto ({currentText.Length} caracteres). Enviando para Ollama...");

            // Chama o LLM via OpenAIService no modo stateless
            string llmResponse = await _openAIService.AskStatelessAsync(SYSTEM_PROMPT, $"[TEXTO EXTRAÍDO DA TELA VIA OCR]:\n{currentText}", settings.ShadowModelName);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SHADOW ASSISTANT] Resposta da IA: {llmResponse}");
            Console.WriteLine("--------------------------------------------------");
            // ---------------------------

            if (!string.IsNullOrWhiteSpace(llmResponse) && !llmResponse.Trim().Equals("NOTHING", StringComparison.OrdinalIgnoreCase) && !llmResponse.Contains("[ERROR]"))
            {
                OnSuggestionReceived?.Invoke(llmResponse.Trim());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] [SHADOW ERRO]: {ex.Message}\n");
        }
        finally
        {
            if (IsActive)
            {
                _timer.Start();
            }
        }
    }
}
