using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class FtbModpackCatalogService : IModpackCatalogProvider
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public string ProviderId => "ftb";

    public string DisplayName => "Feed The Beast";

    public async Task<ModpackCatalogSearchPage> SearchAsync(
        string query,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var requested = Math.Clamp(offset + limit, 1, 50);
        var requestUri = string.IsNullOrWhiteSpace(query)
            ? $"modpack/featured/{requested}"
            : $"modpack/search/{requested}?term={Uri.EscapeDataString(query.Trim())}";
        using var response = await HttpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { MaxDepth = 32 },
            cancellationToken);
        var packIds = ParsePackIds(document.RootElement);
        var detailTasks = packIds
            .Select(packId => GetPackDocumentAsync(packId, cancellationToken))
            .ToArray();
        var details = await Task.WhenAll(detailTasks);
        var items = details
            .Select(detail => ParsePackItem(detail.RootElement))
            .Where(item => item is not null)
            .Cast<ModpackCatalogItem>()
            .Skip(offset)
            .Take(limit)
            .ToArray();
        foreach (var detail in details)
        {
            detail.Dispose();
        }

        return new ModpackCatalogSearchPage(items, offset, limit, packIds.Count);
    }

    public async Task<IReadOnlyList<ModpackCatalogVersion>> GetVersionsAsync(
        ModpackCatalogItem pack,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (!pack.ProviderId.Equals(ProviderId, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(pack.ProjectId, out var packId)
            || packId <= 0)
        {
            throw new ArgumentException("The selected pack is not a valid FTB pack.", nameof(pack));
        }

        using var document = await GetPackDocumentAsync(packId, cancellationToken);
        return ParseVersions(document.RootElement, pack);
    }

    internal static IReadOnlyList<int> ParsePackIds(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("packs", out var packs)
            || packs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("FTB returned invalid catalogue results.");
        }

        return packs.EnumerateArray()
            .Where(item => item.TryGetInt32(out var id) && id > 0)
            .Select(item => item.GetInt32())
            .Distinct()
            .ToArray();
    }

    internal static ModpackCatalogItem? ParsePackItem(JsonElement root)
    {
        var projectId = GetInt32(root, "id");
        var title = GetString(root, "name");
        if (projectId <= 0 || title.Length == 0)
        {
            return null;
        }

        var versions = root.TryGetProperty("versions", out var versionElements)
            && versionElements.ValueKind == JsonValueKind.Array
                ? versionElements.EnumerateArray().ToArray()
                : [];
        var minecraftVersions = versions
            .SelectMany(version => GetTargets(version))
            .Where(target => target.Type.Equals("game", StringComparison.OrdinalIgnoreCase)
                && target.Name.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
            .Select(target => target.Version)
            .Where(version => version.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var loaders = versions
            .SelectMany(version => GetTargets(version))
            .Where(target => target.Type.Equals("modloader", StringComparison.OrdinalIgnoreCase))
            .Select(target => target.Name)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tags = root.TryGetProperty("tags", out var tagElements)
            && tagElements.ValueKind == JsonValueKind.Array
                ? tagElements.EnumerateArray()
                    .Select(tag => GetString(tag, "name"))
                    .Where(tag => tag.Length > 0)
                    .ToArray()
                : [];
        var authors = root.TryGetProperty("authors", out var authorElements)
            && authorElements.ValueKind == JsonValueKind.Array
                ? authorElements.EnumerateArray()
                    .Select(author => GetString(author, "name"))
                    .Where(author => author.Length > 0)
                    .ToArray()
                : [];

        return new ModpackCatalogItem(
            "ftb",
            projectId.ToString(),
            GetString(root, "slug"),
            title,
            FirstNonEmpty(GetString(root, "synopsis"), GetString(root, "description")),
            authors.Length == 0 ? "Feed The Beast" : string.Join(", ", authors),
            FindSquareArtUrl(root),
            GetInt64(root, "installs"),
            minecraftVersions,
            tags,
            loaders,
            ["server"]);
    }

    internal static IReadOnlyList<ModpackCatalogVersion> ParseVersions(
        JsonElement root,
        ModpackCatalogItem pack)
    {
        if (GetInt32(root, "id").ToString() != pack.ProjectId
            || !root.TryGetProperty("versions", out var versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("FTB returned invalid pack versions.");
        }

        var results = new List<ModpackCatalogVersion>();
        foreach (var version in versions.EnumerateArray())
        {
            var versionId = GetInt32(version, "id");
            var versionNumber = GetString(version, "name");
            if (versionId <= 0 || versionNumber.Length == 0)
            {
                continue;
            }

            var targets = GetTargets(version);
            var minecraftVersions = targets
                .Where(target => target.Type.Equals("game", StringComparison.OrdinalIgnoreCase)
                    && target.Name.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                .Select(target => target.Version)
                .Where(value => value.Length > 0)
                .ToArray();
            var loaders = targets
                .Where(target => target.Type.Equals("modloader", StringComparison.OrdinalIgnoreCase))
                .Select(target => target.Name)
                .Where(value => value.Length > 0)
                .ToArray();
            var manifestUri = new Uri(
                $"https://api.feed-the-beast.com/v1/modpacks/public/modpack/{pack.ProjectId}/{versionId}");
            var manifest = new ModpackCatalogFile(
                $"FTB manifest {versionNumber}",
                manifestUri,
                0,
                string.Empty,
                string.Empty)
            {
                PackageKind = ModpackPackageKind.FtbManifest
            };
            var publishedUnix = GetInt64(version, "released");
            results.Add(new ModpackCatalogVersion(
                "ftb",
                pack.ProjectId,
                versionId.ToString(),
                $"{pack.Title} {versionNumber}",
                versionNumber,
                GetString(version, "type"),
                publishedUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(publishedUnix)
                    : DateTimeOffset.MinValue,
                minecraftVersions,
                loaders,
                "dedicated_server_only",
                manifest));
        }

        return results.OrderByDescending(version => version.PublishedAt).ToArray();
    }

    private static async Task<JsonDocument> GetPackDocumentAsync(
        int packId,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync($"modpack/{packId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { MaxDepth = 64 },
            cancellationToken);
    }

    private static IReadOnlyList<FtbTarget> GetTargets(JsonElement element)
    {
        if (!element.TryGetProperty("targets", out var targets)
            || targets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return targets.EnumerateArray()
            .Select(target => new FtbTarget(
                GetString(target, "name"),
                GetString(target, "version"),
                GetString(target, "type")))
            .Where(target => target.Name.Length > 0)
            .ToArray();
    }

    private static string FindSquareArtUrl(JsonElement root)
    {
        if (!root.TryGetProperty("art", out var art) || art.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var item in art.EnumerateArray()
                     .OrderByDescending(item => GetString(item, "type")
                         .Equals("square", StringComparison.OrdinalIgnoreCase)))
        {
            var value = GetString(item, "url");
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.Equals("cdn.feed-the-beast.com", StringComparison.OrdinalIgnoreCase))
            {
                return uri.AbsoluteUri;
            }
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static long GetInt64(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.TryGetInt64(out var result)
            ? result
            : 0;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.feed-the-beast.com/v1/modpacks/public/"),
            Timeout = TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.1"));
        return client;
    }

    private sealed record FtbTarget(string Name, string Version, string Type);
}
