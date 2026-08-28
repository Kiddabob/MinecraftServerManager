using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerBaselineInstaller
{
    bool CanInstall(string loaderId);

    Task<ServerBaselineInstallResult> InstallAsync(
        ServerBaselineInstallRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
