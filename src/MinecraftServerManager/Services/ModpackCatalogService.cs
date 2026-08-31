using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ModpackCatalogService : IModpackCatalogService
{
    private readonly IReadOnlyDictionary<string, IModpackCatalogProvider> _providers;

    public ModpackCatalogService(IEnumerable<IModpackCatalogProvider> providers)
    {
        _providers = providers.ToDictionary(
            provider => provider.ProviderId,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ModpackCatalogSearchPage> SearchAsync(
        string query,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default)
        => await SearchAsync(
            new ModpackCatalogSearchRequest(query, offset, limit, [], string.Empty, string.Empty, []),
            cancellationToken);

    public async Task<ModpackCatalogSearchPage> SearchAsync(
        ModpackCatalogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var selectedProviderIds = request.ProviderIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedProviders = _providers.Values
            .Where(provider => selectedProviderIds.Count == 0 || selectedProviderIds.Contains(provider.ProviderId))
            .ToArray();
        // Keep the combined result window stable while paging. Each provider contributes
        // its largest supported first page, then the app applies the cross-provider filters
        // and paging to that bounded, deterministic set.
        const int fetchLimit = 100;
        var searches = selectedProviders
            .Select(provider => SearchProviderAsync(
                provider,
                request.Query,
                0,
                fetchLimit,
                cancellationToken))
            .ToArray();
        var responses = await Task.WhenAll(searches);
        var successfulPages = responses
            .Where(response => response.Page is not null)
            .Select(response => response.Page!)
            .ToArray();
        var normalizedQuery = request.Query.Trim();
        var matchingItems = successfulPages
            .SelectMany(page => page.Items)
            .DistinctBy(item => $"{item.ProviderId}:{item.ProjectId}", StringComparer.OrdinalIgnoreCase)
            .Where(item => MatchesFilters(item, request))
            .OrderBy(item => GetSearchRank(item, normalizedQuery))
            .ThenByDescending(item => item.Downloads)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var items = matchingItems
            .Skip(request.Offset)
            .Take(request.Limit)
            .ToArray();
        return new ModpackCatalogSearchPage(
            items,
            request.Offset,
            request.Limit,
            matchingItems.Length)
        {
            ProviderStatuses = responses.Select(response => response.Status).ToArray()
        };
    }

    public Task<IReadOnlyList<ModpackCatalogVersion>> GetVersionsAsync(
        ModpackCatalogItem pack,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (!_providers.TryGetValue(pack.ProviderId, out var provider))
        {
            throw new NotSupportedException(
                $"The modpack provider '{pack.ProviderId}' is not available.");
        }

        return provider.GetVersionsAsync(pack, cancellationToken);
    }

    private static async Task<ProviderSearchResponse> SearchProviderAsync(
        IModpackCatalogProvider provider,
        string query,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await provider.SearchAsync(query, offset, limit, cancellationToken);
            return new ProviderSearchResponse(
                page,
                new ModpackProviderSearchStatus(
                    provider.ProviderId,
                    provider.DisplayName,
                    true,
                    page.Items.Count,
                    page.Items.Count == 0
                        ? "No matching packs"
                        : $"{page.Items.Count:N0} shown"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidDataException
                or ArgumentException or System.Text.Json.JsonException)
        {
            return new ProviderSearchResponse(
                null,
                new ModpackProviderSearchStatus(
                    provider.ProviderId,
                    provider.DisplayName,
                    false,
                    0,
                    exception.Message));
        }
    }

    private static int GetSearchRank(ModpackCatalogItem item, string query)
    {
        if (query.Length == 0)
        {
            return 3;
        }

        if (item.Title.Equals(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 0;
        }

        if (item.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 1;
        }

        return item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || item.Slug.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? 2
                : 3;
    }

    private static bool MatchesFilters(ModpackCatalogItem item, ModpackCatalogSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.MinecraftVersion)
            && !item.MinecraftVersions.Contains(request.MinecraftVersion, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.LoaderId)
            && !item.Loaders.Contains(request.LoaderId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return request.Categories.Count == 0 || request.Categories.Any(category => CategoryMatches(item, category));
    }

    private static bool CategoryMatches(ModpackCatalogItem item, string requestedCategory)
    {
        var aliases = requestedCategory.ToLowerInvariant() switch
        {
            "technology" => new[] { "technology", "tech" },
            "optimization" => new[] { "optimization", "performance", "optimisation" },
            "adventure" => new[] { "adventure", "exploration" },
            "action" => new[] { "action", "combat", "pvp" },
            "space" => new[] { "space", "galactic", "astronomy" },
            "magic" => new[] { "magic", "magical" },
            "quests" => new[] { "quest", "quests" },
            "vanilla-plus" => new[] { "vanilla+", "vanilla plus", "vanilla-plus" },
            _ => new[] { requestedCategory.ToLowerInvariant() }
        };
        var searchable = string.Join(' ', item.Categories.Append(item.Title).Append(item.Description)).ToLowerInvariant();
        return aliases.Any(searchable.Contains);
    }

    private sealed record ProviderSearchResponse(
        ModpackCatalogSearchPage? Page,
        ModpackProviderSearchStatus Status);
}
