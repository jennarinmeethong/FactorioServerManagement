using System.Text.Json;

namespace FactorioManager.Api;

public sealed class SecretStore(DataPaths paths)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SecretSettings> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.Secrets)) return new SecretSettings(null, null);
        await using var stream = File.OpenRead(paths.Secrets);
        return await JsonSerializer.DeserializeAsync<SecretSettings>(stream, Json, cancellationToken) ?? new SecretSettings(null, null);
    }

    public async Task WriteAsync(SecretSettings secrets, CancellationToken cancellationToken = default)
    {
        secrets = secrets with
        {
            FactorioUsername = secrets.FactorioUsername?.Trim(),
            FactorioToken = secrets.FactorioToken?.Trim(),
            DiscordWebhookUrl = secrets.DiscordWebhookUrl?.Trim(),
            TelegramBotToken = secrets.TelegramBotToken?.Trim(),
            TelegramChatId = secrets.TelegramChatId?.Trim()
        };

        // Write beside the target and replace it only after the complete JSON has
        // reached disk. This prevents a restart during File.Create from erasing
        // the saved Factorio token.
        var temporaryPath = paths.Secrets + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, secrets, Json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            File.Move(temporaryPath, paths.Secrets, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(paths.Secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public async Task<SecretSettings> UpdateAsync(string? username, string? token, CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(cancellationToken);
        var next = new SecretSettings(
            string.IsNullOrWhiteSpace(username) ? current.FactorioUsername : username.Trim(),
            string.IsNullOrWhiteSpace(token) ? current.FactorioToken : token.Trim(),
            current.DiscordWebhookUrl,
            current.TelegramBotToken,
            current.TelegramChatId);
        await WriteAsync(next, cancellationToken);
        return next;
    }

    public async Task<SecretSettings> UpdateNotificationsAsync(
        string? discordWebhookUrl,
        string? telegramBotToken,
        string? telegramChatId,
        CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(cancellationToken);
        var next = current with
        {
            DiscordWebhookUrl = string.IsNullOrWhiteSpace(discordWebhookUrl) ? current.DiscordWebhookUrl : discordWebhookUrl,
            TelegramBotToken = string.IsNullOrWhiteSpace(telegramBotToken) ? current.TelegramBotToken : telegramBotToken,
            TelegramChatId = string.IsNullOrWhiteSpace(telegramChatId) ? current.TelegramChatId : telegramChatId
        };
        await WriteAsync(next, cancellationToken);
        return next;
    }
}
