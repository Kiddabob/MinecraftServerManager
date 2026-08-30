using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerReadinessService
{
    ServerReadinessReport Evaluate(ServerProfile profile);

    Task<ServerReadinessReport> AcceptEulaAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);
}
