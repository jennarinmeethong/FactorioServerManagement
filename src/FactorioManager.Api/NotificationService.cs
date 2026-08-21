using System.Net.Http.Json;

namespace FactorioManager.Api;

public sealed class NotificationService(
    StateStore state,
    IHttpClientFactory clients,
    SecretStore secrets,
    ILogger<NotificationService> logger)
{
    public async Task<NotificationSettingsStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var configured = await secrets.ReadAsync(cancellationToken);
        var settings = await state.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
        return new(
            settings.AlertsEnabled,
            !string.IsNullOrWhiteSpace(configured.DiscordWebhookUrl),
            !string.IsNullOrWhiteSpace(configured.TelegramBotToken) && !string.IsNullOrWhiteSpace(configured.TelegramChatId));
    }

    public async Task SendAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        var settings = await state.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
        if (!settings.AlertsEnabled) return;

        var configured = await secrets.ReadAsync(cancellationToken);
        var text = $"{title}: {message}";
        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        if (!string.IsNullOrWhiteSpace(configured.DiscordWebhookUrl))
        {
            try
            {
                using var response = await client.PostAsJsonAsync(configured.DiscordWebhookUrl, new { content = text }, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                logger.LogWarning(exception, "Discord notification delivery failed");
            }
        }

        if (!string.IsNullOrWhiteSpace(configured.TelegramBotToken) && !string.IsNullOrWhiteSpace(configured.TelegramChatId))
        {
            try
            {
                var endpoint = $"https://api.telegram.org/bot{Uri.EscapeDataString(configured.TelegramBotToken)}/sendMessage";
                using var response = await client.PostAsJsonAsync(endpoint, new { chat_id = configured.TelegramChatId, text }, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                logger.LogWarning(exception, "Telegram notification delivery failed");
            }
        }
    }
}
