using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class TechnicModpackCatalogService : IModpackCatalogProvider
{
    private const string LauncherBuild = "1131";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public string ProviderId => "technic";

    public string DisplayName => "Technic";

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

        if (string.IsNullOrWhiteSpace(query))
        {
            return new ModpackCatalogSearchPage([], offset, limit, 0);
        }

        var requestUri = $"search?build={LauncherBuild}&q={Uri.EscapeDataString(query.Trim())}";
        using var response = await HttpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { MaxDepth = 32 },
            cancellationToken);
        var page = ParseSearchResponse(document.RootElement, offset, limit);
        return page with { Items = page.Items.Skip(offset).Take(limit).ToArray() };
    }

    public async Task<IReadOnlyList<ModpackCatalogVersion>> GetVersionsAsync(
        ModpackCatalogItem pack,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (!pack.ProviderId.Equals(ProviderId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(pack.Slug))
        {
            throw new ArgumentException("The selected pack is not a valid Technic pack.", nameof(pack));
        }

        var requestUri = $"modpack/{Uri.EscapeDataString(pack.Slug)}?build={LauncherBuild}";
        using var response = await HttpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { MaxDepth = 64 },
            cancellationToken);
        return ParsePackResponse(document.RootElement, pack);
    }

    internal static ModpackCatalogSearchPage ParseSearchResponse(
        JsonElement root,
        int offset = 0,
        int limit = 20)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("modpacks", out var modpacks)
            || modpacks.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Technic returned invalid search results.");
        }

        var items = new List<ModpackCatalogItem>();
        foreach (var modpack in modpacks.EnumerateArray())
        {
            var projectId = GetId(modpack, "id");
            var slug = GetString(modpack, "slug");
            var title = GetString(modpack, "name");
            if (projectId.Length == 0 || slug.Length == 0 || title.Length == 0)
            {
                continue;
            }

            items.Add(new ModpackCatalogItem(
                "technic",
                projectId,
                slug,
                title,
                "Technic Platform modpack",
                "Technic Platform",
                GetTrustedResourceUrl(modpack, "iconUrl"),
                0,
                [],
                ["technic"],
                []));
        }

        return new ModpackCatalogSearchPage(items, offset, limit, items.Count);
    }

    internal static IReadOnlyList<ModpackCatalogVersion> ParsePackResponse(
        JsonElement root,
        ModpackCatalogItem pack)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Technic returned invalid pack details.");
        }

        var projectId = GetId(root, "id");
        var versionNumber = GetString(root, "version");
        if (projectId.Length == 0
            || !projectId.Equals(pack.ProjectId, StringComparison.Ordinal)
            || versionNumber.Length == 0)
        {
            throw new InvalidDataException("Technic returned pack details for a different project or no release.");
        }

        var minecraftVersion = GetString(root, "minecraft");
        var forgeVersion = GetString(root, "forge");
        var serverPackUrl = GetString(root, "serverPackUrl");
        ModpackCatalogFile? package = null;
        if (TryGetTrustedServerPackUri(serverPackUrl, out var packageUri))
        {
            var fileName = Uri.UnescapeDataString(Path.GetFileName(packageUri.AbsolutePath));
            package = new ModpackCatalogFile(
                fileName.Length == 0 ? $"{pack.Slug}-server.zip" : fileName,
                packageUri,
                0,
                string.Empty,
                string.Empty)
            {
                PackageKind = ModpackPackageKind.TechnicServerArchive
            };
        }

        var published = DateTimeOffset.MinValue;
        if (root.TryGetProperty("feed", out var feed) && feed.ValueKind == JsonValueKind.Array)
        {
            var latestUnixTime = feed.EnumerateArray()
                .Select(item => GetInt64(item, "date"))
                .Where(value => value > 0)
                .DefaultIfEmpty(0)
                .Max();
            if (latestUnixTime > 0)
            {
                published = DateTimeOffset.FromUnixTimeSeconds(latestUnixTime);
            }
        }

        return
        [
            new ModpackCatalogVersion(
                "technic",
                pack.ProjectId,
                versionNumber,
                $"{pack.Title} {versionNumber}",
                versionNumber,
                "release",
                published,
                minecraftVersion.Length == 0 ? [] : [minecraftVersion],
                forgeVersion.Length == 0 ? ["technic"] : ["forge"],
                package is null ? "unknown" : "dedicated_server_only",
                package)
        ];
    }

    internal static bool TryGetTrustedServerPackUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            && candidate.Scheme == Uri.UriSchemeHttps
            && candidate.Host.Equals("servers.technicpack.net", StringComparison.OrdinalIgnoreCase))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string GetTrustedResourceUrl(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("cdn.technicpack.net", StringComparison.OrdinalIgnoreCase)
                ? uri.AbsoluteUri
                : string.Empty;
    }

    private static string GetId(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

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
            BaseAddress = new Uri("https://api.technicpack.net/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.1"));
        return client;
    }
}
