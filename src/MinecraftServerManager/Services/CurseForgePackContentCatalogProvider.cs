using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class CurseForgePackContentCatalogProvider : IPackContentCatalogProvider
{
    private const int MinecraftGameId = 432;
    private const int BukkitPluginsClassId = 5;
    private const int MinecraftModsClassId = 6;
    private const int MaximumPageSize = 50;
    private const int MaximumVersionCount = 200;
    private readonly HttpClient _httpClient;
    private readonly ICurseForgeApiKeyService? _apiKeyService;
    private readonly string _injectedApiKey;

    public CurseForgePackContentCatalogProvider(ICurseForgeApiKeyService apiKeyService)
        : this(CreateHttpClient(), apiKeyService)
    {
    }

    internal CurseForgePackContentCatalogProvider(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _injectedApiKey = apiKey?.Trim() ?? string.Empty;
    }

    internal CurseForgePackContentCatalogProvider(
        HttpClient httpClient,
        ICurseForgeApiKeyService apiKeyService)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));
        _injectedApiKey = string.Empty;
    }

    public string ProviderId => "curseforge";

    public string DisplayName => "CurseForge";

    public async Task<ServerContentSearchPage> SearchPackContentAsync(
        PackCatalogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.Limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        EnsureConfigured();
        var loaderTypes = request.Kind == ServerContentKind.Mod
            ? request.LoaderIds
                .Select(ToModLoaderType)
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .Distinct()
                .ToArray()
            : [];
        var requestUri = BuildSearchRequestUri(
            request,
            loaderTypes.Length == 1 ? loaderTypes[0] : null);
        using var response = await SendAsync(requestUri, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseSearchResponse(
            document.RootElement,
            request.Kind,
            request.Categories,
            loaderTypes.Length > 1 ? loaderTypes : []);
    }

    public async Task<IReadOnlyList<ServerContentVersion>> GetPackVersionsAsync(
        string projectId,
        string minecraftVersion,
        IReadOnlyList<string> loaderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        EnsureNumericId(projectId, "project");
        EnsureConfigured();

        var loaderTypes = loaderIds
            .Select(ToModLoaderType)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .Distinct()
            .Select(value => (int?)value)
            .ToArray();
        if (loaderTypes.Length == 0)
        {
            loaderTypes = [null];
        }

        var versions = new Dictionary<string, ServerContentVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var loaderType in loaderTypes)
        {
            var index = 0;
            while (versions.Count < MaximumVersionCount)
            {
                var requestUri = BuildFilesRequestUri(
                    projectId,
                    minecraftVersion,
                    loaderType,
                    index,
                    MaximumPageSize);
                using var response = await SendAsync(requestUri, cancellationToken);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var page = ParseFilesResponse(document.RootElement);
                foreach (var version in page.Items)
                {
                    versions.TryAdd(version.VersionId, version);
                }

                index += page.ResultCount;
                if (page.ResultCount == 0 || index >= page.TotalCount)
                {
                    break;
                }
            }
        }

        return versions.Values
            .Where(version => version.PrimaryFile is not null)
            .OrderBy(version => ReleaseChannelOrder(version.ReleaseChannel))
            .ThenByDescending(version => version.PublishedAt)
            .Take(MaximumVersionCount)
            .ToArray();
    }

    public async Task<ServerContentVersion> GetPackVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        var parts = versionId.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new InvalidDataException("CurseForge version identifiers must contain both the project and file IDs.");
        }

        EnsureNumericId(parts[0], "project");
        EnsureNumericId(parts[1], "file");
        EnsureConfigured();
        using var response = await SendAsync(
            $"v1/mods/{Uri.EscapeDataString(parts[0])}/files/{Uri.EscapeDataString(parts[1])}",
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseFileResponse(document.RootElement)
            ?? throw new InvalidDataException("CurseForge returned invalid file metadata.");
    }

    public async Task<IReadOnlyList<string>> GetMinecraftVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        using var response = await SendAsync(
            "v1/minecraft/version?sortDescending=true",
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseMinecraftVersions(document.RootElement);
    }

    internal static string BuildSearchRequestUri(
        PackCatalogSearchRequest request,
        int? modLoaderType)
    {
        var parameters = new List<string>
        {
            $"gameId={MinecraftGameId}",
            $"classId={(request.Kind == ServerContentKind.Mod ? MinecraftModsClassId : BukkitPluginsClassId)}",
            $"index={request.Offset}",
            $"pageSize={request.Limit}",
            $"sortField={GetSearchSortField(request.Sort)}",
            "sortOrder=desc"
        };
        AddParameter(parameters, "searchFilter", request.Query.Trim());
        if (!request.MinecraftVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            AddParameter(parameters, "gameVersion", request.MinecraftVersion);
        }

        if (modLoaderType is not null)
        {
            parameters.Add($"modLoaderType={modLoaderType.Value}");
        }

        return $"v1/mods/search?{string.Join('&', parameters)}";
    }

    private static int GetSearchSortField(string sort) => sort.ToLowerInvariant() switch
    {
        "downloads" => 6,
        "updated" => 3,
        "newest" => 11,
        _ => 2
    };

    internal static string BuildFilesRequestUri(
        string projectId,
        string minecraftVersion,
        int? modLoaderType,
        int index,
        int pageSize)
    {
        var parameters = new List<string>
        {
            $"index={Math.Max(0, index)}",
            $"pageSize={Math.Clamp(pageSize, 1, MaximumPageSize)}"
        };
        if (!minecraftVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            AddParameter(parameters, "gameVersion", minecraftVersion);
        }

        if (modLoaderType is not null)
        {
            parameters.Add($"modLoaderType={modLoaderType.Value}");
        }

        return $"v1/mods/{Uri.EscapeDataString(projectId)}/files?{string.Join('&', parameters)}";
    }

    internal static ServerContentSearchPage ParseSearchResponse(
        JsonElement root,
        ServerContentKind requestedKind,
        IReadOnlyList<string>? requestedCategories = null,
        IReadOnlyList<int>? requestedLoaderTypes = null)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("CurseForge returned invalid content search results.");
        }

        var categories = requestedCategories ?? [];
        var loaderTypes = requestedLoaderTypes ?? [];
        var projects = new List<ServerContentProject>();
        foreach (var item in data.EnumerateArray())
        {
            var projectId = GetInt64(item, "id").ToString();
            var title = GetString(item, "name");
            if (projectId == "0" || title.Length == 0 || !GetBoolean(item, "isAvailable", true))
            {
                continue;
            }

            var fileIndexes = GetArray(item, "latestFilesIndexes");
            if (loaderTypes.Count > 0 && !fileIndexes.Any(index =>
                loaderTypes.Contains(GetInt32(index, "modLoader"))))
            {
                continue;
            }

            var projectCategories = GetArray(item, "categories")
                .Select(category => GetString(category, "name"))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (categories.Count > 0 && !categories.Any(requested =>
                projectCategories.Any(category => CategoryMatches(category, requested))))
            {
                continue;
            }

            var authors = GetArray(item, "authors")
                .Select(author => GetString(author, "name"))
                .Where(value => value.Length > 0)
                .Take(3)
                .ToArray();
            var logo = item.TryGetProperty("logo", out var logoValue)
                && logoValue.ValueKind == JsonValueKind.Object
                    ? logoValue
                    : default;
            projects.Add(new ServerContentProject(
                "curseforge",
                projectId,
                GetString(item, "slug"),
                title,
                GetString(item, "summary"),
                authors.Length == 0 ? "CurseForge author" : string.Join(", ", authors),
                GetTrustedAssetUrl(logo, "thumbnailUrl"),
                GetInt64(item, "downloadCount"),
                requestedKind,
                fileIndexes
                    .Select(index => GetString(index, "gameVersion"))
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                projectCategories,
                []));
        }

        var pagination = root.TryGetProperty("pagination", out var paginationValue)
            && paginationValue.ValueKind == JsonValueKind.Object
                ? paginationValue
                : default;
        return new ServerContentSearchPage(
            projects,
            GetInt32(pagination, "index"),
            GetInt32(pagination, "pageSize"),
            GetInt32(pagination, "totalCount"));
    }

    internal static CurseForgeFilePage ParseFilesResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("CurseForge returned invalid content files.");
        }

        var items = data.EnumerateArray()
            .Select(ParseFile)
            .Where(version => version is not null)
            .Cast<ServerContentVersion>()
            .ToArray();
        var pagination = root.TryGetProperty("pagination", out var paginationValue)
            && paginationValue.ValueKind == JsonValueKind.Object
                ? paginationValue
                : default;
        return new CurseForgeFilePage(
            items,
            GetInt32(pagination, "resultCount"),
            GetInt32(pagination, "totalCount"));
    }

    internal static ServerContentVersion? ParseFileResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data))
        {
            throw new InvalidDataException("CurseForge returned invalid content file metadata.");
        }

        return ParseFile(data);
    }

    internal static IReadOnlyList<string> ParseMinecraftVersions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("CurseForge returned invalid Minecraft version metadata.");
        }

        return data.EnumerateArray()
            .Where(item => GetBoolean(item, "approved", true))
            .Select(item => GetString(item, "versionString"))
            .Where(IsMinecraftVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static int? ToModLoaderType(string loaderId) => loaderId.Trim().ToLowerInvariant() switch
    {
        "forge" => 1,
        "cauldron" => 2,
        "liteloader" => 3,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => null
    };

    private static ServerContentVersion? ParseFile(JsonElement file)
    {
        if (file.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fileId = GetInt64(file, "id");
        var projectId = GetInt64(file, "modId");
        var fileLength = GetInt64(file, "fileLength");
        var fileName = Path.GetFileName(GetString(file, "fileName"));
        var downloadUrl = GetTrustedDownloadUrl(file, "downloadUrl");
        var sha1 = GetArray(file, "hashes")
            .Where(hash => GetInt32(hash, "algo") == 1)
            .Select(hash => GetString(hash, "value").ToLowerInvariant())
            .FirstOrDefault(value => value.Length == 40 && value.All(Uri.IsHexDigit))
            ?? string.Empty;
        if (fileId <= 0 || projectId <= 0 || fileLength <= 0
            || !GetBoolean(file, "isAvailable", true)
            || !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || downloadUrl is null
            || sha1.Length == 0)
        {
            return null;
        }

        var gameVersions = GetStringArray(file, "gameVersions");
        var loaders = gameVersions
            .Select(ToLoaderId)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ServerContentVersion(
            "curseforge",
            projectId.ToString(),
            $"{projectId}:{fileId}",
            GetString(file, "displayName"),
            GetString(file, "displayName") is { Length: > 0 } displayName ? displayName : fileName,
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
            string.Empty,
            [new ServerContentFile(
                fileName,
                downloadUrl,
                fileLength,
                string.Empty,
                true,
                sha1)],
            ParseDependencies(file));
    }

    private static IReadOnlyList<ServerContentDependency> ParseDependencies(JsonElement file) =>
        GetArray(file, "dependencies")
            .Select(dependency => new ServerContentDependency(
                string.Empty,
                GetInt64(dependency, "modId").ToString(),
                string.Empty,
                GetInt32(dependency, "relationType") switch
                {
                    1 or 6 => "embedded",
                    2 => "optional",
                    3 => "required",
                    5 => "incompatible",
                    _ => string.Empty
                }))
            .Where(dependency => dependency.ProjectId != "0" && dependency.DependencyType.Length > 0)
            .ToArray();

    private async Task<HttpResponseMessage> SendAsync(
        string requestUri,
        CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
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

    private static void EnsureNumericId(string value, string description)
    {
        if (!long.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"The CurseForge {description} ID is invalid.", nameof(value));
        }
    }

    private static void AddParameter(List<string> parameters, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    private static bool CategoryMatches(string category, string requested) =>
        NormalizeCategory(category).Equals(NormalizeCategory(requested), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCategory(string value) => string.Join(
        '-',
        value.Trim().ToLowerInvariant()
            .Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));

    private static bool IsMinecraftVersion(string value)
    {
        var candidate = value.Trim();
        return candidate.Length is > 2 and <= 24
            && char.IsDigit(candidate[0])
            && candidate.Contains('.')
            && candidate.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    private static string ToLoaderId(string value) => value.Trim().ToLowerInvariant() switch
    {
        "forge" => "forge",
        "cauldron" => "cauldron",
        "liteloader" => "liteloader",
        "fabric" => "fabric",
        "quilt" => "quilt",
        "neoforge" => "neoforge",
        "bukkit" => "bukkit",
        "spigot" => "spigot",
        "paper" => "paper",
        "purpur" => "purpur",
        _ => string.Empty
    };

    private static string GetTrustedAssetUrl(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && IsTrustedCurseForgeHost(uri.Host)
                ? uri.AbsoluteUri
                : string.Empty;
    }

    private static Uri? GetTrustedDownloadUrl(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && IsTrustedCurseForgeHost(uri.Host)
                ? uri
                : null;
    }

    private static bool IsTrustedCurseForgeHost(string host) =>
        host.Equals("curseforge.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase);

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

    private static int ReleaseChannelOrder(string channel) => channel switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3
    };

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

    internal sealed record CurseForgeFilePage(
        IReadOnlyList<ServerContentVersion> Items,
        int ResultCount,
        int TotalCount);
}
