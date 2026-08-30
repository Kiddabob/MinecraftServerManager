using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IModpackCatalogProvider
{
    string ProviderId { get; }

    string DisplayName { get; }

    Task<ModpackCatalogSearchPage> SearchAsync(
        string query,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModpackCatalogVersion>> GetVersionsAsync(
        ModpackCatalogItem pack,
        CancellationToken cancellationToken = default);
}
