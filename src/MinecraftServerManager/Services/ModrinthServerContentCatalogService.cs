using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ModrinthServerContentCatalogService : IServerContentCatalogService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly string[] ServerEnvironments =
    [
        "dedicated_server_only",
        "server_only",
        "server_only_client_optional",
        "client_and_server",
        "client_only_server_optional",
        "client_or_server",
        "client_or_server_prefers_both"
    ];

    public string ProviderId => "modrinth";

    public async Task<ServerContentSearchPage> SearchAsync(
        string query,
        string minecraftVersion,
        ServerContentKind kind,
        IReadOnlyList<string> loaderIds,
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

        var facets = BuildSearchFacets(minecraftVersion, kind, loaderIds);
        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        var requestUri = $"search?query={Uri.EscapeDataString(query.Trim())}"
            + $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}"
            + $"&index={index}&offset={offset}&limit={limit}";
        using var response = await HttpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseSearchResponse(document.RootElement, kind);
    }

    public async Task<IReadOnlyList<ServerContentVersion>> GetVersionsAsync(
        string projectId,
        string minecraftVersion,
        IReadOnlyList<string> loaderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var parameters = new List<string> { "include_changelog=false" };
        if (!string.IsNullOrWhiteSpace(minecraftVersion)
            && !minecraftVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            parameters.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftVersion }))}");
        }

        if (loaderIds.Count > 0)
        {
            parameters.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(loaderIds.Distinct(StringComparer.OrdinalIgnoreCase)))}");
        }

        var requestUri = $"project/{Uri.EscapeDataString(projectId.Trim())}/version?{string.Join('&', parameters)}";
        using var response = await HttpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseVersionsResponse(document.RootElement);
    }

    public async Task<ServerContentVersion> GetVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        using var response = await HttpClient.GetAsync(
            $"version/{Uri.EscapeDataString(versionId.Trim())}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseVersion(document.RootElement)
            ?? throw new InvalidDataException("Modrinth returned an invalid content version.");
    }

    internal static IReadOnlyList<IReadOnlyList<string>> BuildSearchFacets(
        string minecraftVersion,
        ServerContentKind kind,
        IReadOnlyList<string> loaderIds)
    {
        var facets = new List<IReadOnlyList<string>>
        {
            new[]
            {
                kind == ServerContentKind.Mod
                    ? "project_type:mod"
                    : "all_project_types:plugin"
            },
            ServerEnvironments.Select(environment => $"environment:{environment}").ToArray()
        };

        if (!string.IsNullOrWhiteSpace(minecraftVersion)
            && !minecraftVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            facets.Add(new[] { $"versions:{minecraftVersion}" });
        }

        var loaders = loaderIds
            .Where(loader => !string.IsNullOrWhiteSpace(loader))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(loader => $"categories:{loader.ToLowerInvariant()}")
            .ToArray();
        if (loaders.Length > 0)
        {
            facets.Add(loaders);
        }

        return facets;
    }

    internal static ServerContentSearchPage ParseSearchResponse(
        JsonElement root,
        ServerContentKind requestedKind)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("hits", out var hits)
            || hits.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Modrinth returned invalid content search results.");
        }

        var projects = new List<ServerContentProject>();
        foreach (var hit in hits.EnumerateArray())
        {
            var projectId = GetString(hit, "project_id");
            var title = GetString(hit, "title");
            if (projectId.Length == 0 || title.Length == 0)
            {
                continue;
            }

            var projectTypes = GetStringArray(hit, "all_project_types");
            var kind = projectTypes.Contains("plugin", StringComparer.OrdinalIgnoreCase)
                && !projectTypes.Contains("mod", StringComparer.OrdinalIgnoreCase)
                    ? ServerContentKind.Plugin
                    : requestedKind;
            projects.Add(new ServerContentProject(
                "modrinth",
                projectId,
                GetString(hit, "slug"),
                title,
                GetString(hit, "description"),
                GetString(hit, "author"),
                GetTrustedCdnUrl(hit, "icon_url"),
                GetInt64(hit, "downloads"),
                kind,
                GetStringArray(hit, "versions"),
                GetStringArray(hit, "display_categories"),
                GetStringArray(hit, "environment")));
        }

        return new ServerContentSearchPage(
            projects,
            GetInt32(root, "offset"),
            GetInt32(root, "limit"),
            GetInt32(root, "total_hits"));
    }

    internal static IReadOnlyList<ServerContentVersion> ParseVersionsResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Modrinth returned invalid content versions.");
        }

        return root.EnumerateArray()
            .Select(ParseVersion)
            .Where(version => version is not null)
            .Cast<ServerContentVersion>()
            .Where(version => version.IsServerCompatible && version.PrimaryFile is not null)
            .OrderBy(version => ReleaseChannelOrder(version.ReleaseChannel))
            .ThenByDescending(version => version.PublishedAt)
            .Take(100)
            .ToArray();
    }

    internal static ServerContentVersion? ParseVersion(JsonElement version)
    {
        if (version.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var versionId = GetString(version, "id");
        var projectId = GetString(version, "project_id");
        var versionNumber = GetString(version, "version_number");
        if (versionId.Length == 0 || projectId.Length == 0 || versionNumber.Length == 0)
        {
            return null;
        }

        return new ServerContentVersion(
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
            ParseFiles(version),
            ParseDependencies(version));
    }

    private static IReadOnlyList<ServerContentFile> ParseFiles(JsonElement version)
    {
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ServerContentFile>();
        foreach (var file in files.EnumerateArray())
        {
            var fileName = GetString(file, "filename");
            var urlText = GetString(file, "url");
            var hashes = file.TryGetProperty("hashes", out var hashValue)
                && hashValue.ValueKind == JsonValueKind.Object
                    ? hashValue
                    : default;
            var sha512 = GetString(hashes, "sha512");
            if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
                || sha512.Length != 128
                || !sha512.All(Uri.IsHexDigit))
            {
                continue;
            }

            result.Add(new ServerContentFile(
                Path.GetFileName(fileName),
                uri,
                GetInt64(file, "size"),
                sha512.ToLowerInvariant(),
                GetBoolean(file, "primary")));
        }

        return result;
    }

    private static IReadOnlyList<ServerContentDependency> ParseDependencies(JsonElement version)
    {
        if (!version.TryGetProperty("dependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return dependencies.EnumerateArray()
            .Select(dependency => new ServerContentDependency(
                GetString(dependency, "version_id"),
                GetString(dependency, "project_id"),
                GetString(dependency, "file_name"),
                GetString(dependency, "dependency_type").ToLowerInvariant()))
            .Where(dependency => dependency.DependencyType.Length > 0)
            .ToArray();
    }

    private static int ReleaseChannelOrder(string channel) => channel switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3
    };

    private static string GetTrustedCdnUrl(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
                ? uri.AbsoluteUri
                : string.Empty;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
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
            Timeout = TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.2"));
        return client;
    }
}
