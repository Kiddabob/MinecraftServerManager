using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerConfigurationService
{
    Task<ServerConfigurationDiscoveryResult> DiscoverAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<ServerConfigurationDocument> ReadAsync(
        ServerProfile profile,
        ServerConfigurationFile file,
        CancellationToken cancellationToken = default);

    Task<ServerConfigurationSaveResult> SaveAsync(
        ServerProfile profile,
        ServerConfigurationDocument original,
        string content,
        CancellationToken cancellationToken = default);
}
