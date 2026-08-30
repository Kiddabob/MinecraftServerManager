using System.Collections.Concurrent;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class PackPlatformVersionService : IPackPlatformVersionService
{
    private const int MaximumDisplayedVersions = 40;
    private const string FabricMetadataHost = "meta.fabricmc.net";
    private const string QuiltMetadataHost = "meta.quiltmc.org";
    private const string ForgeMavenHost = "maven.minecraftforge.net";
    private const string NeoForgeMavenHost = "maven.neoforged.net";
    private static readonly Uri ForgeMetadataUri = new(
        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");
    private static readonly Uri NeoForgeMetadataUri = new(
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");
    private readonly ConcurrentDictionary<string, IReadOnlyList<PackPlatformVersionOption>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public bool CanResolve(string platformId) => ResolvePlatform(platformId) is not null;

    public async Task<IReadOnlyList<PackPlatformVersionOption>> GetVersionsAsync(
        string platformId,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformId);
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);
        var normalizedPlatformId = platformId.Trim().ToLowerInvariant();
        var normalizedMinecraftVersion = minecraftVersion.Trim();
        var cacheKey = $"{normalizedPlatformId}:{normalizedMinecraftVersion}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var platform = ResolvePlatform(normalizedPlatformId)
            ?? throw new NotSupportedException(
                $"{platformId} does not publish an exact loader version through a supported official catalogue.");
        IReadOnlyList<PackPlatformVersionOption> versions;
        switch (platform.LoaderId)
        {
            case "minecraft":
                versions =
                [
                    new PackPlatformVersionOption(
                        normalizedPlatformId,
                        platform.LoaderId,
                        normalizedMinecraftVersion,
                        true,
                        platform.CanPrepareServer)
                ];
                break;
            case "fabric-loader":
            {
                var uri = new Uri(
                    $"https://{FabricMetadataHost}/v2/versions/loader/{Uri.EscapeDataString(normalizedMinecraftVersion)}");
                var json = await JavaServerInstallerUtilities.DownloadMetadataAsync(
                    uri,
                    FabricMetadataHost,
                    cancellationToken);
                versions = ParseLoaderJson(
                    json,
                    normalizedPlatformId,
                    platform.LoaderId,
                    platform.CanPrepareServer,
                    hasStableProperty: true,
                    "Fabric");
                break;
            }
            case "quilt-loader":
            {
                var uri = new Uri(
                    $"https://{QuiltMetadataHost}/v3/versions/loader/{Uri.EscapeDataString(normalizedMinecraftVersion)}");
                var json = await JavaServerInstallerUtilities.DownloadMetadataAsync(
                    uri,
                    QuiltMetadataHost,
                    cancellationToken);
                versions = ParseLoaderJson(
                    json,
                    normalizedPlatformId,
                    platform.LoaderId,
                    platform.CanPrepareServer,
                    hasStableProperty: false,
                    "Quilt");
                break;
            }
            case "forge":
            {
                var xml = await JavaServerInstallerUtilities.DownloadMavenMetadataAsync(
                    ForgeMetadataUri,
                    ForgeMavenHost,
                    cancellationToken);
                versions = ParseForgeVersions(
                    xml,
                    normalizedPlatformId,
                    normalizedMinecraftVersion,
                    platform.CanPrepareServer);
                break;
            }
            case "neoforge":
            {
                var xml = await JavaServerInstallerUtilities.DownloadMavenMetadataAsync(
                    NeoForgeMetadataUri,
                    NeoForgeMavenHost,
                    cancellationToken);
                versions = ParseNeoForgeVersions(
                    xml,
                    normalizedPlatformId,
                    normalizedMinecraftVersion,
                    platform.CanPrepareServer);
                break;
            }
            default:
                throw new NotSupportedException($"{platformId} is not supported.");
        }

        if (versions.Count == 0)
        {
            throw new InvalidDataException(
                $"The official loader catalogue returned no versions for Minecraft {normalizedMinecraftVersion}.");
        }

        _cache.TryAdd(cacheKey, versions);
        return versions;
    }

    internal static IReadOnlyList<PackPlatformVersionOption> ParseLoaderJson(
        string json,
        string platformId,
        string loaderId,
        bool canPrepareServer,
        bool hasStableProperty,
        string providerName)
    {
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{providerName} returned invalid loader metadata.");
        }

        var options = new List<PackPlatformVersionOption>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("loader", out var loader)
                || loader.ValueKind != JsonValueKind.Object
                || !loader.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String
                || !IsSafeVersion(versionElement.GetString(), out var version))
            {
                continue;
            }

            var stable = hasStableProperty
                ? loader.TryGetProperty("stable", out var stableElement)
                    && stableElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && stableElement.GetBoolean()
                : !IsPreviewVersion(version);
            options.Add(new PackPlatformVersionOption(
                platformId,
                loaderId,
                version,
                stable,
                canPrepareServer));
        }

        return PreferStable(options);
    }

    internal static IReadOnlyList<PackPlatformVersionOption> ParseForgeVersions(
        string metadataXml,
        string platformId,
        string minecraftVersion,
        bool canPrepareServer)
    {
        var prefix = minecraftVersion + "-";
        var options = JavaServerInstallerUtilities.ParseMavenVersions(metadataXml)
            .Reverse()
            .Where(version => version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(version => version[prefix.Length..])
            .Where(version => IsSafeVersion(version, out _))
            .Select(version => new PackPlatformVersionOption(
                platformId,
                "forge",
                version,
                !IsPreviewVersion(version),
                canPrepareServer));
        return PreferStable(options);
    }

    internal static IReadOnlyList<PackPlatformVersionOption> ParseNeoForgeVersions(
        string metadataXml,
        string platformId,
        string minecraftVersion,
        bool canPrepareServer)
    {
        var prefix = GetNeoForgeVersionPrefix(minecraftVersion);
        var options = JavaServerInstallerUtilities.ParseMavenVersions(metadataXml)
            .Reverse()
            .Where(version => version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(version => IsSafeVersion(version, out _))
            .Select(version => new PackPlatformVersionOption(
                platformId,
                "neoforge",
                version,
                !IsPreviewVersion(version),
                canPrepareServer));
        return PreferStable(options);
    }

    internal static string GetNeoForgeVersionPrefix(string minecraftVersion)
    {
        var parts = minecraftVersion.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3 || parts.Any(part => !int.TryParse(part, out _)))
        {
            throw new InvalidDataException(
                $"Minecraft version '{minecraftVersion}' cannot be mapped to NeoForge's official version scheme.");
        }

        var major = int.Parse(parts[0]);
        var minor = int.Parse(parts[1]);
        var patch = parts.Length == 3 ? int.Parse(parts[2]) : 0;
        return major == 1
            ? $"{minor}.{patch}."
            : $"{major}.{minor}.{patch}.";
    }

    private static PlatformRoute? ResolvePlatform(string platformId) => platformId switch
    {
        "vanilla-client" => new("minecraft", false),
        "vanilla-server" => new("minecraft", true),
        "fabric-client" => new("fabric-loader", false),
        "fabric-server" => new("fabric-loader", true),
        "quilt-client" => new("quilt-loader", false),
        "quilt-server" => new("quilt-loader", true),
        "forge-client" => new("forge", false),
        "forge-server" => new("forge", true),
        "neoforge-client" => new("neoforge", false),
        "neoforge-server" => new("neoforge", true),
        _ => null
    };

    private static IReadOnlyList<PackPlatformVersionOption> PreferStable(
        IEnumerable<PackPlatformVersionOption> options)
    {
        var distinct = options
            .GroupBy(option => option.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return distinct
            .Where(option => option.IsStable)
            .Concat(distinct.Where(option => !option.IsStable))
            .Take(MaximumDisplayedVersions)
            .ToArray();
    }

    private static bool IsSafeVersion(string? value, out string version)
    {
        version = value?.Trim() ?? string.Empty;
        return version.Length is > 0 and <= 100
            && version.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '+' or '_');
    }

    private static bool IsPreviewVersion(string version) =>
        version.Contains('-', StringComparison.Ordinal)
        || version.Contains("alpha", StringComparison.OrdinalIgnoreCase)
        || version.Contains("beta", StringComparison.OrdinalIgnoreCase)
        || version.Contains("preview", StringComparison.OrdinalIgnoreCase)
        || version.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
        || version.Contains("rc", StringComparison.OrdinalIgnoreCase);

    private sealed record PlatformRoute(string LoaderId, bool CanPrepareServer);
}
