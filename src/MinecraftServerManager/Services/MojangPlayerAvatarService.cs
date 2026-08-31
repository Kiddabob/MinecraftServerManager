using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MinecraftServerManager.Services;

public sealed class MojangPlayerAvatarService : IPlayerAvatarService
{
    private const int MaximumSkinBytes = 1_048_576;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly string _cacheRoot;
    private readonly Dictionary<string, Task<string?>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingGate = new();

    public MojangPlayerAvatarService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kidda.MinecraftServerManager",
            "PlayerSkins"))
    {
    }

    internal MojangPlayerAvatarService(string cacheRoot)
    {
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    public Task<string?> GetAvatarPathAsync(
        string playerName,
        Guid? playerId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizePlayerName(playerName);
        if (normalizedName.Length == 0)
        {
            return Task.FromResult<string?>(null);
        }

        var key = playerId?.ToString("N") ?? normalizedName;
        Task<string?> pending;
        lock (_pendingGate)
        {
            if (!_pending.TryGetValue(key, out pending!))
            {
                pending = DownloadAndCacheAsync(normalizedName, playerId, CancellationToken.None);
                _pending[key] = pending;
            }
        }

        return AwaitAndReleaseAsync(key, pending, cancellationToken);
    }

    private async Task<string?> AwaitAndReleaseAsync(
        string key,
        Task<string?> pending,
        CancellationToken cancellationToken)
    {
        try
        {
            return await pending.WaitAsync(cancellationToken);
        }
        finally
        {
            if (pending.IsCompleted)
            {
                lock (_pendingGate)
                {
                    if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                    {
                        _pending.Remove(key);
                    }
                }
            }
        }
    }

    private async Task<string?> DownloadAndCacheAsync(
        string playerName,
        Guid? knownPlayerId,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheRoot);
        var nameCachePath = Path.Combine(_cacheRoot, $"name-{playerName.ToLowerInvariant()}.png");
        var knownIdCachePath = knownPlayerId is null
            ? null
            : Path.Combine(_cacheRoot, $"uuid-{knownPlayerId.Value:N}.png");
        var freshCache = FindFreshCache(knownIdCachePath, nameCachePath);
        if (freshCache is not null)
        {
            return freshCache;
        }

        var staleCache = FindExistingCache(knownIdCachePath, nameCachePath);
        try
        {
            var playerId = knownPlayerId ?? await ResolvePlayerIdAsync(playerName, cancellationToken);
            if (playerId is null)
            {
                return staleCache;
            }

            var idCachePath = Path.Combine(_cacheRoot, $"uuid-{playerId.Value:N}.png");
            freshCache = FindFreshCache(idCachePath, nameCachePath);
            if (freshCache is not null)
            {
                return freshCache;
            }

            var textureUri = await ResolveSkinUriAsync(playerId.Value, cancellationToken);
            if (textureUri is null && knownPlayerId is not null)
            {
                var resolvedPlayerId = await ResolvePlayerIdAsync(playerName, cancellationToken);
                if (resolvedPlayerId is not null && resolvedPlayerId != knownPlayerId)
                {
                    playerId = resolvedPlayerId;
                    idCachePath = Path.Combine(_cacheRoot, $"uuid-{playerId.Value:N}.png");
                    textureUri = await ResolveSkinUriAsync(playerId.Value, cancellationToken);
                }
            }

            if (textureUri is null)
            {
                return staleCache;
            }

            var skin = await DownloadBoundedAsync(textureUri, cancellationToken);
            if (!LooksLikePng(skin))
            {
                return staleCache;
            }

            await WriteAtomicallyAsync(idCachePath, skin, cancellationToken);
            if (!idCachePath.Equals(nameCachePath, StringComparison.OrdinalIgnoreCase))
            {
                await WriteAtomicallyAsync(nameCachePath, skin, cancellationToken);
            }

            return nameCachePath;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or InvalidDataException
                or JsonException
                or FormatException
                or TaskCanceledException)
        {
            return staleCache;
        }
    }

    private static async Task<Guid?> ResolvePlayerIdAsync(
        string playerName,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(playerName)}",
            UriKind.Absolute);
        using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("id", out var idElement))
        {
            return null;
        }

        return Guid.TryParseExact(idElement.GetString(), "N", out var playerId) ? playerId : null;
    }

    private static async Task<Uri?> ResolveSkinUriAsync(Guid playerId, CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"https://sessionserver.mojang.com/session/minecraft/profile/{playerId:N}",
            UriKind.Absolute);
        using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if (!property.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), "textures", StringComparison.Ordinal)
                || !property.TryGetProperty("value", out var value))
            {
                continue;
            }

            var decoded = Convert.FromBase64String(value.GetString() ?? string.Empty);
            if (decoded.Length > 64 * 1024)
            {
                return null;
            }

            using var textureDocument = JsonDocument.Parse(decoded);
            if (!textureDocument.RootElement.TryGetProperty("textures", out var textures)
                || !textures.TryGetProperty("SKIN", out var skin)
                || !skin.TryGetProperty("url", out var url)
                || NormalizeTrustedSkinUri(url.GetString()) is not { } textureUri)
            {
                return null;
            }

            return textureUri;
        }

        return null;
    }

    internal static Uri? NormalizeTrustedSkinUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var textureUri)
            || !textureUri.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (textureUri.Scheme == Uri.UriSchemeHttps)
        {
            return textureUri;
        }

        return textureUri.Scheme == Uri.UriSchemeHttp
            ? new UriBuilder(textureUri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri
            : null;
    }

    private static async Task<byte[]> DownloadBoundedAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumSkinBytes)
        {
            throw new InvalidDataException("The player skin exceeds the supported size.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (target.Length + read > MaximumSkinBytes)
            {
                throw new InvalidDataException("The player skin exceeds the supported size.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return target.ToArray();
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string? FindFreshCache(params string?[] paths) =>
        paths.FirstOrDefault(path =>
            path is not null
            && File.Exists(path)
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= CacheLifetime);

    private static string? FindExistingCache(params string?[] paths) =>
        paths.FirstOrDefault(path => path is not null && File.Exists(path));

    private static string NormalizePlayerName(string playerName)
    {
        var trimmed = playerName.Trim();
        return trimmed.Length is >= 1 and <= 16
            && trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                ? trimmed
                : string.Empty;
    }

    private static bool LooksLikePng(IReadOnlyList<byte> content) =>
        content.Count >= 8
        && content[0] == 0x89
        && content[1] == (byte)'P'
        && content[2] == (byte)'N'
        && content[3] == (byte)'G'
        && content[4] == 0x0D
        && content[5] == 0x0A
        && content[6] == 0x1A
        && content[7] == 0x0A;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MinecraftServerManager", "0.1"));
        return client;
    }
}
