namespace MinecraftServerManager.Services;

public interface IModpackInstallLocationService
{
    string ManagedInstancesDirectory { get; }

    string EnsureManagedInstancesDirectory();
}
