using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IPackPlatformVersionService
{
    bool CanResolve(string platformId);

    Task<IReadOnlyList<PackPlatformVersionOption>> GetVersionsAsync(
        string platformId,
        string minecraftVersion,
        CancellationToken cancellationToken = default);
}
