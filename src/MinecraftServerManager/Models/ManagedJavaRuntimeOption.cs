namespace MinecraftServerManager.Models;

public sealed record ManagedJavaRuntimeOption(
    int MajorVersion,
    string MinecraftVersions,
    bool IsLongTermSupported,
    bool IsInstalled,
    string ExecutablePath)
{
    public string DisplayName => $"Java {MajorVersion}";

    public string SupportText => IsLongTermSupported
        ? $"For {MinecraftVersions} • Long-term support"
        : $"For {MinecraftVersions} • Legacy runtime; no longer supported upstream";

    public string ActionText => IsInstalled
        ? "Managed copy installed"
        : $"Install managed Java {MajorVersion}";

    public bool CanInstall => !IsInstalled;
}
