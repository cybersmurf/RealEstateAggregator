namespace RealEstate.Api.Services.Notifications;

/// <summary>Odeslání zprávy přes Telegram Bot API (parse_mode=HTML).</summary>
public interface ITelegramSender
{
    /// <summary>False, když chybí Telegram:BotToken.</summary>
    bool IsConfigured { get; }

    Task SendAsync(string chatId, string htmlText, CancellationToken ct);
}
