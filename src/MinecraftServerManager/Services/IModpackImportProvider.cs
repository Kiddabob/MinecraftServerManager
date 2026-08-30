using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IModpackImportProvider
{
    string ProviderId { get; }

    bool CanImport(ModpackCatalogItem pack, ModpackCatalogVersion version);

    Task<ModpackImportResult> ImportAsync(
        ModpackCatalogItem pack,
        ModpackCatalogVersion version,
        string destinationParentDirectory,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
