using MinecraftServerManager.Models;
using System.Text.Json;

namespace MinecraftServerManager.Services;

public sealed class PackContentCatalogService : IPackContentCatalogService
{
    private readonly IReadOnlyDictionary<string, IPackContentCatalogProvider> _providers;

    public PackContentCatalogService(IEnumerable<IPackContentCatalogProvider> providers)
    {
        _providers = providers.ToDictionary(
            provider => provider.ProviderId,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<PackCatalogSearchPage> SearchAsync(
        PackCatalogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var searches = _providers.Values.Select(provider => SearchProviderAsync(
            provider,
            request,
            cancellationToken));
        var responses = await Task.WhenAll(searches);
        var projects = responses
            .SelectMany(response => response.Page?.Items ?? [])
            .OrderByDescending(project => project.Downloads)
            .ThenBy(project => project.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(1, request.Limit))
            .ToArray();
        return new PackCatalogSearchPage(
            projects,
            responses.Select(response => response.Status).ToArray());
    }

    public Task<IReadOnlyList<ServerContentVersion>> GetVersionsAsync(
        string providerId,
        string projectId,
        string minecraftVersion,
        IReadOnlyList<string> loaderIds,
        CancellationToken cancellationToken = default) =>
        GetProvider(providerId).GetPackVersionsAsync(
            projectId,
            minecraftVersion,
            loaderIds,
            cancellationToken);

    public Task<ServerContentVersion> GetVersionAsync(
        string providerId,
        string versionId,
        CancellationToken cancellationToken = default) =>
        GetProvider(providerId).GetPackVersionAsync(versionId, cancellationToken);

    public async Task<IReadOnlyList<string>> GetMinecraftVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers.Values)
        {
            try
            {
                var versions = await provider.GetMinecraftVersionsAsync(cancellationToken);
                if (versions.Count > 0)
                {
                    return versions;
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or InvalidDataException or JsonException
                || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // A provider failure must not prevent another configured provider from supplying versions.
            }
        }

        return [];
    }

    private IPackContentCatalogProvider GetProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new InvalidOperationException($"Pack catalogue provider '{providerId}' is not configured.");
    }

    private static async Task<ProviderSearchResponse> SearchProviderAsync(
        IPackContentCatalogProvider provider,
        PackCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await provider.SearchPackContentAsync(request, cancellationToken);
            return new ProviderSearchResponse(
                page,
                new PackProviderStatus(
                    provider.ProviderId,
                    provider.DisplayName,
                    true,
                    page.TotalHits,
                    $"{page.TotalHits:N0} compatible projects found"));
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException or JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new ProviderSearchResponse(
                null,
                new PackProviderStatus(
                    provider.ProviderId,
                    provider.DisplayName,
                    false,
                    0,
                    exception.Message));
        }
    }

    private sealed record ProviderSearchResponse(
        ServerContentSearchPage? Page,
        PackProviderStatus Status);
}
