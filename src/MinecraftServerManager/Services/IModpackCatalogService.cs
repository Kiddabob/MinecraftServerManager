using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IModpackCatalogService
{
    string ProviderId { get; }

    Task<ModpackCatalogSearchPage> SearchAsync(
        string query,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModpackCatalogVersion>> GetVersionsAsync(
        string projectId,
        CancellationToken cancellationToken = default);
}
