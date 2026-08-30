using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IPackContentCatalogProvider
{
    string ProviderId { get; }

    string DisplayName { get; }

    Task<ServerContentSearchPage> SearchPackContentAsync(
        PackCatalogSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServerContentVersion>> GetPackVersionsAsync(
        string projectId,
        string minecraftVersion,
        IReadOnlyList<string> loaderIds,
        CancellationToken cancellationToken = default);

    Task<ServerContentVersion> GetPackVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetMinecraftVersionsAsync(
        CancellationToken cancellationToken = default);
}
