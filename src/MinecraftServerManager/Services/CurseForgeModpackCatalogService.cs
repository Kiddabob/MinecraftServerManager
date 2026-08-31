using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class CurseForgeModpackCatalogService : IModpackCatalogProvider
{
    private const int MinecraftGameId = 432;
    private const int MaximumPageSize = 50;
    private readonly HttpClient _httpClient;
    private readonly ICurseForgeApiKeyService? _apiKeyService;
    private readonly string _injectedApiKey;
    private int? _modpackClassId;

    public CurseForgeModpackCatalogService(ICurseForgeApiKeyService apiKeyService)
        : this(CreateHttpClient(), apiKeyService)
    {
    }

    internal CurseForgeModpackCatalogService(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _injectedApiKey = apiKey?.Trim() ?? string.Empty;
    }

    internal CurseForgeModpackCatalogService(
        HttpClient httpClient,
        ICurseForgeApiKeyService apiKeyService)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));
        _injectedApiKey = string.Empty;
    }

    public string ProviderId => "curseforge";

    public string DisplayName => "CurseForge";

    public async Task<ModpackCatalogSearchPage> SearchAsync(
        string query,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        EnsureConfigured();
        var classId = await GetModpackClassIdAsync(cancellationToken);
        var pageSize = Math.Min(limit, MaximumPageSize);
        var parameters = new List<string>
        {
            $"gameId={MinecraftGameId}",
            $"classId={classId}",
            $"index={offset}",
            $"pageSize={pageSize}",
            "sortField=2",
            "sortOrder=desc"
        };
        if (!string.IsNullOrWhiteSpace(query))
        {
            parameters.Add($"searchFilter={Uri.EscapeDataString(query.Trim())}");
        }

        using var response = await SendAsync($"v1/mods/search?{string.Join('&', parameters)}", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseSearchResponse(document.RootElement);
    }

    public async Task<IReadOnlyList<ModpackCatalogVersion>> GetVersionsAsync(
        ModpackCatalogItem pack,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (!pack.ProviderId.Equals(ProviderId, StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(pack.ProjectId, out var projectId)
            || projectId <= 0)
        {
            throw new ArgumentException("The selected pack is not a valid CurseForge modpack.", nameof(pack));
        }

        EnsureConfigured();
        using var response = await SendAsync(
            $"v1/mods/{projectId}/files?index=0&pageSize={MaximumPageSize}",
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseVersionsResponse(document.RootElement, pack);
    }

    internal static int ParseModpackClassId(JsonElement root)
    {
        foreach (var category in GetArray(root, "data"))
        {
            var name = GetString(category, "name");
            var slug = GetString(category, "slug");
            var id = GetInt32(category, "id");
            if (id > 0
                && (name.Equals("Modpacks", StringComparison.OrdinalIgnoreCase)
                    || slug.Equals("modpacks", StringComparison.OrdinalIgnoreCase)))
            {
                return id;
            }
        }

        throw new InvalidDataException("CurseForge did not return its Minecraft modpack category.");
    }

    internal static ModpackCatalogSearchPage ParseSearchResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("CurseForge returned invalid modpack search results.");
        }

        var items = new List<ModpackCatalogItem>();
        foreach (var item in data.EnumerateArray())
        {
            var projectId = GetInt64(item, "id");
            var title = GetString(item, "name");
            if (projectId <= 0 || title.Length == 0 || !GetBoolean(item, "isAvailable", true))
            {
                continue;
            }

            var indexes = GetArray(item, "latestFilesIndexes");
            var versions = indexes
                .Select(index => GetString(index, "gameVersion"))
                .Where(IsMinecraftVersion)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var loaders = indexes
                .Select(index => ToLoaderId(GetInt32(index, "modLoader")))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var categories = GetArray(item, "categories")
                .Select(category => GetString(category, "name"))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var authors = GetArray(item, "authors")
                .Select(author => GetString(author, "name"))
                .Where(value => value.Length > 0)
                .Take(3)
                .ToArray();
            var logo = item.TryGetProperty("logo", out var logoElement)
                && logoElement.ValueKind == JsonValueKind.Object
                    ? logoElement
                    : default;
            items.Add(new ModpackCatalogItem(
                "curseforge",
                projectId.ToString(),
                GetString(item, "slug"),
                title,
                GetString(item, "summary"),
                authors.Length == 0 ? "CurseForge author" : string.Join(", ", authors),
                GetTrustedAssetUrl(logo, "thumbnailUrl"),
                GetInt64(item, "downloadCount"),
                versions,
                categories,
                loaders,
                []));
        }

        var pagination = root.TryGetProperty("pagination", out var paginationElement)
            && paginationElement.ValueKind == JsonValueKind.Object
                ? paginationElement
                : default;
        return new ModpackCatalogSearchPage(
            items,
            GetInt32(pagination, "index"),
            GetInt32(pagination, "pageSize"),
            GetInt32(pagination, "totalCount"));
    }

    internal static IReadOnlyList<ModpackCatalogVersion> ParseVersionsResponse(
        JsonElement root,
        ModpackCatalogItem pack)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("CurseForge returned invalid modpack versions.");
        }

        return data.EnumerateArray()
            .Where(file => GetBoolean(file, "isAvailable", true))
            .Select(file =>
            {
                var fileId = GetInt64(file, "id");
                var gameVersions = GetStringArray(file, "gameVersions");
                var loaders = gameVersions
                    .Select(ToLoaderId)
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new ModpackCatalogVersion(
                    "curseforge",
                    pack.ProjectId,
                    fileId.ToString(),
                    GetString(file, "displayName"),
                    FirstNonEmpty(GetString(file, "displayName"), GetString(file, "fileName")),
                    GetInt32(file, "releaseType") switch
                    {
                        1 => "release",
                        2 => "beta",
                        3 => "alpha",
                        _ => "unknown"
                    },
                    GetDateTimeOffset(file, "fileDate"),
                    gameVersions.Where(IsMinecraftVersion).ToArray(),
                    loaders,
                    GetBoolean(file, "isServerPack") || GetInt64(file, "serverPackFileId") > 0
                        ? "catalog_only_server_pack"
                        : "catalog_only",
                    null);
            })
            .Where(version => version.VersionId != "0")
            .OrderByDescending(version => version.PublishedAt)
            .ToArray();
    }

    private async Task<int> GetModpackClassIdAsync(CancellationToken cancellationToken)
    {
        if (_modpackClassId is { } cached)
        {
            return cached;
        }

        using var response = await SendAsync(
            $"v1/categories?gameId={MinecraftGameId}&classesOnly=true",
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        _modpackClassId = ParseModpackClassId(document.RootElement);
        return _modpackClassId.Value;
    }

    private async Task<HttpResponseMessage> SendAsync(string requestUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-api-key", GetApiKey());
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private void EnsureConfigured()
    {
        if (GetApiKey().Length == 0)
        {
            throw new HttpRequestException(
                "No approved CurseForge developer API key is connected. Use Sources & accounts on the Build a pack page to add one.");
        }
    }

    private string GetApiKey() => _apiKeyService?.GetApiKey()?.Trim() ?? _injectedApiKey;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool IsMinecraftVersion(string value)
    {
        var candidate = value.Trim();
        return candidate.Length is > 2 and <= 24
            && char.IsDigit(candidate[0])
            && candidate.Contains('.')
            && candidate.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    private static string ToLoaderId(int value) => value switch
    {
        1 => "forge",
        2 => "cauldron",
        3 => "liteloader",
        4 => "fabric",
        5 => "quilt",
        6 => "neoforge",
        _ => string.Empty
    };

    private static string ToLoaderId(string value) => value.Trim().ToLowerInvariant() switch
    {
        "forge" => "forge",
        "cauldron" => "cauldron",
        "liteloader" => "liteloader",
        "fabric" => "fabric",
        "quilt" => "quilt",
        "neoforge" => "neoforge",
        _ => string.Empty
    };

    private static string GetTrustedAssetUrl(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("curseforge.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase))
                ? uri.AbsoluteUri
                : string.Empty;
    }

    private static IReadOnlyList<JsonElement> GetArray(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [];

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName) =>
        GetArray(element, propertyName)
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
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

    private static bool GetBoolean(JsonElement element, string propertyName, bool fallback = false) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static DateTimeOffset GetDateTimeOffset(JsonElement element, string propertyName) =>
        DateTimeOffset.TryParse(GetString(element, propertyName), out var value)
            ? value
            : DateTimeOffset.MinValue;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.curseforge.com/"),
            Timeout = TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.2"));
        return client;
    }
}
