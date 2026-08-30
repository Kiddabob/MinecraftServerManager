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
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var searches = _providers.Values
            .Select(provider => SearchProviderAsync(
                provider,
                query,
                offset,
                Math.Min(limit, 20),
                cancellationToken))
            .ToArray();
        var responses = await Task.WhenAll(searches);
        var successfulPages = responses
            .Where(response => response.Page is not null)
            .Select(response => response.Page!)
            .ToArray();
        var normalizedQuery = query.Trim();
        var items = successfulPages
            .SelectMany(page => page.Items)
            .DistinctBy(item => $"{item.ProviderId}:{item.ProjectId}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => GetSearchRank(item, normalizedQuery))
            .ThenByDescending(item => item.Downloads)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new ModpackCatalogSearchPage(
            items,
            offset,
            limit,
            successfulPages.Sum(page => Math.Max(page.TotalHits, page.Items.Count)))
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

    private sealed record ProviderSearchResponse(
        ModpackCatalogSearchPage? Page,
        ModpackProviderSearchStatus Status);
}
