using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IModpackCatalogService
{
    Task<ModpackCatalogSearchPage> SearchAsync(
        string query,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<ModpackCatalogSearchPage> SearchAsync(
        ModpackCatalogSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModpackCatalogVersion>> GetVersionsAsync(
        ModpackCatalogItem pack,
        CancellationToken cancellationToken = default);
}
