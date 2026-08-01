using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class JsonProfileService : IProfileService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<ServerProfile> LoadAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var profilePath = Path.Combine(AppContext.BaseDirectory, "Profiles", fileName);
        if (!File.Exists(profilePath))
        {
            throw new FileNotFoundException($"Server profile was not found: {profilePath}", profilePath);
        }

        await using var stream = File.OpenRead(profilePath);
        var profile = await JsonSerializer.DeserializeAsync<ServerProfile>(
            stream,
            SerializerOptions,
            cancellationToken);

        if (profile is null)
        {
            throw new InvalidDataException($"Server profile is empty: {profilePath}");
        }

        profile.ServerDirectory = Environment.ExpandEnvironmentVariables(profile.ServerDirectory);
        profile.JavaExecutable = Environment.ExpandEnvironmentVariables(profile.JavaExecutable);
        return profile;
    }
}
