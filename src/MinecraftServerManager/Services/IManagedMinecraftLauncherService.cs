using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IManagedMinecraftLauncherService
{
    string LauncherDirectory { get; }

    bool IsInstalled { get; }

    Task<ManagedLauncherInstallResult> InstallPackAsync(
        ManagedLauncherInstallRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    bool TryOpenLauncher(string? instanceId, out string message);
}
