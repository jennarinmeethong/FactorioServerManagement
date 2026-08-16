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
        await using (var stream = File.Create(paths.Secrets))
            await JsonSerializer.SerializeAsync(stream, secrets, Json, cancellationToken);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(paths.Secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
