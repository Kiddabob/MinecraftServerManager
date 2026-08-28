using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ModrinthModpackCatalogService : IModpackCatalogService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private const string ModpackFacet = "[[\"project_type:modpack\"]]";

    public string ProviderId => "modrinth";

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

        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        var requestUri = $"search?query={Uri.EscapeDataString(query.Trim())}"
            + $"&facets={Uri.EscapeDataString(ModpackFacet)}"
            + $"&index={index}&offset={offset}&limit={limit}";
        using var response = await HttpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseSearchResponse(document.RootElement);
    }

    public async Task<IReadOnlyList<ModpackCatalogVersion>> GetVersionsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("A Modrinth project ID is required.", nameof(projectId));
        }

        var requestUri = $"project/{Uri.EscapeDataString(projectId.Trim())}/version?include_changelog=false";
        using var response = await HttpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseVersionsResponse(document.RootElement);
    }

    internal static ModpackCatalogSearchPage ParseSearchResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("hits", out var hits)
            || hits.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Modrinth returned invalid search results.");
        }

        var items = new List<ModpackCatalogItem>();
        foreach (var hit in hits.EnumerateArray())
        {
            if (!TryGetRequiredString(hit, "project_id", out var projectId)
                || !TryGetRequiredString(hit, "title", out var title))
            {
                continue;
            }

            items.Add(new ModpackCatalogItem(
                "modrinth",
                projectId,
                GetString(hit, "slug"),
                title,
                GetString(hit, "description"),
                GetString(hit, "author"),
                GetTrustedCdnUrl(hit, "icon_url"),
                GetInt64(hit, "downloads"),
                GetStringArray(hit, "versions"),
                GetStringArray(hit, "display_categories"),
                GetStringArray(hit, "environment")));
        }

        return new ModpackCatalogSearchPage(
            items,
            GetInt32(root, "offset"),
            GetInt32(root, "limit"),
            GetInt32(root, "total_hits"));
    }

    internal static IReadOnlyList<ModpackCatalogVersion> ParseVersionsResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Modrinth returned invalid modpack versions.");
        }

        var versions = new List<ModpackCatalogVersion>();
        foreach (var version in root.EnumerateArray())
        {
            if (!TryGetRequiredString(version, "id", out var versionId)
                || !TryGetRequiredString(version, "project_id", out var projectId)
                || !TryGetRequiredString(version, "version_number", out var versionNumber))
            {
                continue;
            }

            versions.Add(new ModpackCatalogVersion(
                "modrinth",
                projectId,
                versionId,
                GetString(version, "name"),
                versionNumber,
                GetString(version, "version_type"),
                GetDateTimeOffset(version, "date_published"),
                GetStringArray(version, "game_versions"),
                GetStringArray(version, "loaders"),
                GetString(version, "environment"),
                FindModpackFile(version)));
        }

        return versions
            .OrderByDescending(version => version.PublishedAt)
            .ToArray();
    }

    private static ModpackCatalogFile? FindModpackFile(JsonElement version)
    {
        if (!version.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = files.EnumerateArray()
            .Where(file => GetString(file, "filename").EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => GetBoolean(file, "primary"));
        foreach (var file in candidates)
        {
            var urlText = GetString(file, "url");
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var downloadUri)
                || downloadUri.Scheme != Uri.UriSchemeHttps
                || !downloadUri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hashes = file.TryGetProperty("hashes", out var hashElement)
                && hashElement.ValueKind == JsonValueKind.Object
                    ? hashElement
                    : default;
            var sha1 = hashes.ValueKind == JsonValueKind.Object ? GetString(hashes, "sha1") : string.Empty;
            var sha512 = hashes.ValueKind == JsonValueKind.Object ? GetString(hashes, "sha512") : string.Empty;
            if (sha512.Length != 128 || !sha512.All(Uri.IsHexDigit))
            {
                continue;
            }

            return new ModpackCatalogFile(
                GetString(file, "filename"),
                downloadUri,
                GetInt64(file, "size"),
                sha1,
                sha512);
        }

        return null;
    }

    private static string GetTrustedCdnUrl(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
                ? uri.AbsoluteUri
                : string.Empty;
    }

    private static bool TryGetRequiredString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = GetString(element, propertyName);
        return value.Length > 0;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray()
            : [];

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

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static DateTimeOffset GetDateTimeOffset(JsonElement element, string propertyName) =>
        DateTimeOffset.TryParse(GetString(element, propertyName), out var value)
            ? value
            : DateTimeOffset.MinValue;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.1"));
        return client;
    }
}
