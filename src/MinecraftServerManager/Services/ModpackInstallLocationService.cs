namespace MinecraftServerManager.Services;

public sealed class ModpackInstallLocationService : IModpackInstallLocationService
{
    private const string ApplicationDirectoryName = "Kidda.MinecraftServerManager";
    private const string InstancesDirectoryName = "Instances";

    public ModpackInstallLocationService()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    internal ModpackInstallLocationService(string localApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        ManagedInstancesDirectory = Path.Combine(
            Path.GetFullPath(localApplicationDataDirectory),
            ApplicationDirectoryName,
            InstancesDirectoryName);
    }

    public string ManagedInstancesDirectory { get; }

    public string EnsureManagedInstancesDirectory()
    {
        Directory.CreateDirectory(ManagedInstancesDirectory);
        return ManagedInstancesDirectory;
    }
}
