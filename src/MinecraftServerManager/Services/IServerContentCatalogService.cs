using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerContentCatalogService
{
    string ProviderId { get; }

    Task<ServerContentSearchPage> SearchAsync(
        string query,
        string minecraftVersion,
        ServerContentKind kind,
        IReadOnlyList<string> loaderIds,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServerContentVersion>> GetVersionsAsync(
        string projectId,
        string minecraftVersion,
        IReadOnlyList<string> loaderIds,
        CancellationToken cancellationToken = default);

    Task<ServerContentVersion> GetVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default);
}
