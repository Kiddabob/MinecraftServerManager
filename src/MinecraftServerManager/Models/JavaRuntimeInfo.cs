namespace MinecraftServerManager.Models;

public sealed record JavaRuntimeInfo(
    string ExecutablePath,
    string VersionText,
    int MajorVersion,
    string Source)
{
    public string DisplayName => $"Java {MajorVersion} • {VersionText} — {ExecutablePath}";
}
