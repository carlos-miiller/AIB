using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace AIB.Services;

public class TelegramService
{
    private TelegramBotClient? _botClient;
    private readonly OpenAIService _openAIService;
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _cts;

    public TelegramService(OpenAIService openAIService, SettingsService settingsService)
    {
        _openAIService = openAIService;
        _settingsService = settingsService;
    }

    public async Task StartAsync()
    {
        var settings = _settingsService.LoadSettings();
        var token = CredentialService.RetrieveCredential("Telegram", "BotToken");

        if (string.IsNullOrEmpty(token) || token.Contains("ERRO")) return;

        try
        {
            _botClient = new TelegramBotClient(token);
            _cts = new CancellationTokenSource();

            var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
            _botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, _cts.Token);
            
            var me = await _botClient.GetMe();
            Console.WriteLine($"[TELEGRAM] Bot iniciado: @{me.Username}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TELEGRAM] Erro: {ex.Message}");
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } messageText } message) return;
        long chatId = message.Chat.Id;

        try
        {
            string fullResponse = "";
            await foreach (var chunk in _openAIService.SendMessageStreamAsync(messageText))
            {
                fullResponse += chunk;
            }

            if (!string.IsNullOrEmpty(fullResponse))
            {
                await botClient.SendMessage(chatId, fullResponse, cancellationToken: ct);
            }
        }
        catch { }
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
    {
        Console.WriteLine($"[TELEGRAM] Erro Polling: {exception.Message}");
        return Task.CompletedTask;
    }
}
