using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IPackContentCatalogService
{
    Task<PackCatalogSearchPage> SearchAsync(
        PackCatalogSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServerContentVersion>> GetVersionsAsync(
        string providerId,
        string projectId,
        string minecraftVersion,
        IReadOnlyList<string> loaderIds,
        CancellationToken cancellationToken = default);

    Task<ServerContentVersion> GetVersionAsync(
        string providerId,
        string versionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetMinecraftVersionsAsync(
        CancellationToken cancellationToken = default);
}
