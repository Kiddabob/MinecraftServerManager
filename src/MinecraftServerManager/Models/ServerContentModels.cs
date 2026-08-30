namespace MinecraftServerManager.Models;

public enum ServerContentKind
{
    Mod,
    Plugin
}

public sealed record ServerContentTarget(
    ServerContentKind Kind,
    string DirectoryName,
    IReadOnlyList<string> LoaderIds)
{
    public string KindText => Kind == ServerContentKind.Mod ? "Mods" : "Plugins";

    public string LoadersText => LoaderIds.Count == 0
        ? "Loader not detected"
        : string.Join(" / ", LoaderIds.Select(ToDisplayText));

    private static string ToDisplayText(string value) => string.Join(
        " ",
        value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}

public sealed record ServerContentItem(
    string Name,
    string Id,
    string Version,
    ServerContentKind Kind,
    string Loader,
    string FileName,
    string RelativePath,
    long Size,
    bool IsEnabled)
{
    public string KindText => Kind == ServerContentKind.Mod ? "Mod" : "Plugin";

    public string VersionText => string.IsNullOrWhiteSpace(Version)
        ? "Version not declared"
        : $"Version {Version}";

    public string IdentityText => string.IsNullOrWhiteSpace(Id)
        ? $"{KindText} metadata was not found"
        : $"ID: {Id}";

    public string FileDetailsText => $"{FileName}  •  {FormatSize(Size)}";

    public string StateText => IsEnabled ? "Enabled" : "Disabled";

    private static string FormatSize(long size) => size >= 1024 * 1024
        ? $"{size / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1d, size / 1024d):0.0} KB";
}

public sealed record ServerContentInventory(
    string ServerDirectory,
    string MinecraftVersion,
    string ServerType,
    IReadOnlyList<ServerContentTarget> Targets,
    IReadOnlyList<ServerContentItem> Items)
{
    public bool SupportsMods => Targets.Any(target => target.Kind == ServerContentKind.Mod);

    public bool SupportsPlugins => Targets.Any(target => target.Kind == ServerContentKind.Plugin);

    public string EnvironmentText => $"Minecraft {MinecraftVersion}  •  {ServerType}";

    public string TargetSummary => Targets.Count == 0
        ? "No standard mod or plugin folder was detected."
        : string.Join(
            "  •  ",
            Targets.Select(target => $"{target.KindText}: {target.LoadersText}"));

    public string ItemCountText => Items.Count switch
    {
        0 => "No installed mods or plugins found",
        1 => "1 installed item",
        _ => $"{Items.Count:N0} installed items"
    };
}

public sealed record ServerContentSearchPage(
    IReadOnlyList<ServerContentProject> Items,
    int Offset,
    int Limit,
    int TotalHits);

public sealed record ServerContentProject(
    string ProviderId,
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string Author,
    string IconUrl,
    long Downloads,
    ServerContentKind Kind,
    IReadOnlyList<string> MinecraftVersions,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Environments)
{
    public string KindText => Kind == ServerContentKind.Mod ? "Mod" : "Plugin";

    public string MetadataText => $"{KindText} by {Author}  •  {Downloads:N0} downloads";

    public string CompatibilityText => MinecraftVersions.Count == 0
        ? "Minecraft versions not supplied"
        : $"Minecraft {string.Join(", ", MinecraftVersions.Take(4))}{(MinecraftVersions.Count > 4 ? "…" : string.Empty)}";
}

public sealed record ServerContentFile(
    string FileName,
    Uri DownloadUri,
    long Size,
    string Sha512,
    bool IsPrimary,
    string Sha1 = "")
{
    public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1d, Size / 1024d):0.0} KB";
}

public sealed record ServerContentDependency(
    string VersionId,
    string ProjectId,
    string FileName,
    string DependencyType);

public sealed record ServerContentVersion(
    string ProviderId,
    string ProjectId,
    string VersionId,
    string Name,
    string VersionNumber,
    string ReleaseChannel,
    DateTimeOffset PublishedAt,
    IReadOnlyList<string> MinecraftVersions,
    IReadOnlyList<string> Loaders,
    string Environment,
    IReadOnlyList<ServerContentFile> Files,
    IReadOnlyList<ServerContentDependency> Dependencies)
{
    public ServerContentFile? PrimaryFile => Files
        .Where(file => file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(file => file.IsPrimary)
        .FirstOrDefault();

    public bool IsServerCompatible => Environment is
        "dedicated_server_only" or
        "server_only" or
        "server_only_client_optional" or
        "client_and_server" or
        "client_only_server_optional" or
        "client_or_server" or
        "client_or_server_prefers_both";

    public string DisplayName => $"{VersionNumber}  •  {ReleaseChannel}";

    public string CompatibilityText => $"Minecraft {string.Join(", ", MinecraftVersions)}  •  {string.Join(" / ", Loaders)}";

    public string DependencyText
    {
        get
        {
            var required = Dependencies.Count(dependency => dependency.DependencyType == "required");
            var optional = Dependencies.Count(dependency => dependency.DependencyType == "optional");
            return required == 0 && optional == 0
                ? "No external dependencies declared"
                : $"{required} required  •  {optional} optional dependencies";
        }
    }
}

public sealed record ServerContentInstallPlanItem(
    string ProjectId,
    string VersionId,
    string DisplayName,
    ServerContentKind Kind,
    ServerContentFile File,
    bool IsDependency)
{
    public string DetailsText => $"{File.FileName}  •  {File.SizeText}{(IsDependency ? "  •  Required dependency" : string.Empty)}";
}

public sealed record ServerContentInstallPlan(
    string ServerDirectory,
    string DestinationDirectory,
    ServerContentKind Kind,
    IReadOnlyList<ServerContentInstallPlanItem> Items,
    IReadOnlyList<string> Warnings)
{
    public long TotalBytes => Items.Sum(item => item.File.Size);

    public string SummaryText => Items.Count == 1
        ? $"Install {Items[0].DisplayName}"
        : $"Install {Items[0].DisplayName} and {Items.Count - 1} required dependencies";
}

public enum ServerContentInstallStage
{
    Downloading,
    Verifying,
    Installing,
    Complete
}

public sealed record ServerContentInstallProgress(
    ServerContentInstallStage Stage,
    string Message,
    long CompletedBytes = 0,
    long? TotalBytes = null)
{
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp(CompletedBytes * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

public sealed record ServerContentInstallResult(
    int InstalledFileCount,
    string DestinationDirectory,
    IReadOnlyList<string> InstalledFiles);

public sealed record ServerContentKindOption(
    string Id,
    string DisplayName,
    ServerContentKind? Kind);
