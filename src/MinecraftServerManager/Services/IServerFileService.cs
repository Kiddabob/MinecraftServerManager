using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerFileService
{
    Task<IReadOnlyList<ServerFileItem>> GetItemsAsync(
        string serverRoot,
        string directory,
        CancellationToken cancellationToken = default);

    string? GetParentWithinRoot(string serverRoot, string directory);
}
