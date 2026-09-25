using System.Net.Http.Json;

namespace RealEstate.Api.Services.Notifications;

/// <summary>
/// POST https://api.telegram.org/bot{token}/sendMessage. Telegram HTML podporuje jen b/i/a/code/pre,
/// zprávu proto staví <see cref="NotificationMessageBuilder.BuildTelegramHtml"/>.
/// </summary>
public sealed class TelegramSender(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<TelegramSender> logger) : ITelegramSender
{
    private readonly string? _token = configuration["Telegram:BotToken"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_token);

    public async Task SendAsync(string chatId, string htmlText, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            logger.LogWarning("Telegram bot není nakonfigurován (Telegram:BotToken) – zpráva pro chat {ChatId} se neodeslala", chatId);
            return;
        }

        var client = httpClientFactory.CreateClient("Telegram");
        var payload = new
        {
            chat_id = chatId,
            text = htmlText,
            parse_mode = "HTML",
            disable_web_page_preview = true,
        };

        using var response = await client.PostAsJsonAsync($"https://api.telegram.org/bot{_token}/sendMessage", payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Telegram sendMessage selhal ({(int)response.StatusCode}): {Truncate(body, 300)}");
        }

        logger.LogInformation("Telegram zpráva odeslána do chatu {ChatId}", chatId);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
