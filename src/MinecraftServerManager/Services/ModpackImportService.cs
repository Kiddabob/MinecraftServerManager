using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ModpackImportService : IModpackImportService
{
    private readonly IReadOnlyDictionary<string, IModpackImportProvider> _providers;

    public ModpackImportService(IEnumerable<IModpackImportProvider> providers)
    {
        _providers = providers.ToDictionary(
            provider => provider.ProviderId,
            StringComparer.OrdinalIgnoreCase);
    }

    public bool CanImport(ModpackCatalogItem pack, ModpackCatalogVersion version) =>
        _providers.TryGetValue(pack.ProviderId, out var provider)
        && provider.CanImport(pack, version);

    public Task<ModpackImportResult> ImportAsync(
        ModpackCatalogItem pack,
        ModpackCatalogVersion version,
        string destinationParentDirectory,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(version);
        if (!_providers.TryGetValue(pack.ProviderId, out var provider))
        {
            throw new NotSupportedException(
                $"Server installation from {pack.ProviderName} is not available.");
        }

        if (!provider.CanImport(pack, version))
        {
            throw new InvalidOperationException(version.ImportReadinessText);
        }

        return provider.ImportAsync(
            pack,
            version,
            destinationParentDirectory,
            progress,
            cancellationToken);
    }
}
