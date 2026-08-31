using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IMinecraftLauncherIntegrationService
{
    string LauncherDirectory { get; }

    Task<MinecraftLauncherInstallResult> InstallAsync(
        MinecraftLauncherInstallRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    bool TryOpenLauncher(out string message);
}
