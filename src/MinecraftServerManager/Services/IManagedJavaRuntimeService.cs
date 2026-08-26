using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IManagedJavaRuntimeService
{
    IReadOnlyList<ManagedJavaRuntimeOption> GetOptions();

    Task<JavaRuntimeInfo> InstallAsync(
        int majorVersion,
        CancellationToken cancellationToken = default);
}
