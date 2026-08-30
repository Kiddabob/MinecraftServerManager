using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerContentInstallService
{
    Task<ServerContentInstallPlan> CreatePlanAsync(
        ServerProfile profile,
        ServerContentTarget target,
        ServerContentProject project,
        ServerContentVersion version,
        CancellationToken cancellationToken = default);

    Task<ServerContentInstallResult> InstallAsync(
        ServerContentInstallPlan plan,
        IProgress<ServerContentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
