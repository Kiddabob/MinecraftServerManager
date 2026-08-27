using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IManagedJavaRuntimeService
{
    IReadOnlyList<ManagedJavaRuntimeOption> GetOptions();

    Task<JavaRuntimeInfo> InstallAsync(
        int majorVersion,
        IProgress<ManagedJavaInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
