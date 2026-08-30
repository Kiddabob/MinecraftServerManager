using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerContentInventoryService
{
    Task<ServerContentInventory> DiscoverAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);
}
