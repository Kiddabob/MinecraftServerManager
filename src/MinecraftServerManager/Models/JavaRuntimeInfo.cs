namespace MinecraftServerManager.Models;

public sealed record JavaRuntimeInfo(
    string ExecutablePath,
    string VersionText,
    int MajorVersion,
    string Source,
    string Vendor = "",
    string Architecture = "")
{
    public string DisplayName => $"Java {MajorVersion} • {VersionText} — {ExecutablePath}";

    public string Title => $"Java {MajorVersion} — {VersionText}";

    public string DetailsText => string.Join(
        " • ",
        new[] { Vendor, Architecture, Source }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
