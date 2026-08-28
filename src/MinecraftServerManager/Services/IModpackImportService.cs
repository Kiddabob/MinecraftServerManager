using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IModpackImportService
{
    Task<ModpackImportResult> ImportAsync(
        ModpackCatalogItem pack,
        ModpackCatalogVersion version,
        string destinationParentDirectory,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
