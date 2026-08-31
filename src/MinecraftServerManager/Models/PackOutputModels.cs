namespace MinecraftServerManager.Models;

public enum PackOutputStage
{
    Resolving,
    Downloading,
    Arranging,
    InstallingServerBaseline,
    WritingManifest,
    Complete
}

public sealed record PackOutputRequest(
    string PackName,
    PackBuildTarget Target,
    string MinecraftVersion,
    string ClientPlatformId,
    string ServerPlatformId,
    string ClientLoaderId,
    string ClientLoaderVersion,
    string ServerLoaderId,
    string ServerLoaderVersion,
    string DestinationParentDirectory,
    IReadOnlyList<PackDraftItem> Items);

public sealed record PackOutputPlanItem(
    PackDraftItem DraftItem,
    ServerContentFile File,
    IReadOnlyList<string> RelativePaths)
{
    public string ProviderId => DraftItem.ProviderId;

    public string DisplayName => DraftItem.DisplayName;

    public string DestinationText => string.Join(", ", RelativePaths.Select(path =>
        path.Replace(Path.DirectorySeparatorChar, '/')));
}

public sealed record PackOutputPlan(
    string PackName,
    PackBuildTarget Target,
    string MinecraftVersion,
    string ClientPlatformId,
    string ServerPlatformId,
    string ClientLoaderId,
    string ClientLoaderVersion,
    string ServerLoaderId,
    string ServerLoaderVersion,
    int? RecommendedJavaMajor,
    string DestinationDirectory,
    IReadOnlyList<PackOutputPlanItem> Items)
{
    public long TotalBytes => Items.Sum(item => item.File.Size);

    public string TotalSizeText => TotalBytes >= 1024L * 1024L * 1024L
        ? $"{TotalBytes / 1024d / 1024d / 1024d:0.00} GB"
        : $"{TotalBytes / 1024d / 1024d:0.0} MB";

    public bool PreparesServerBaseline => Target is PackBuildTarget.Server or PackBuildTarget.ClientAndServer
        && !string.IsNullOrWhiteSpace(ServerLoaderId)
        && !string.IsNullOrWhiteSpace(ServerLoaderVersion);

    public string ServerBaselineText => PreparesServerBaseline
        ? $"{ServerLoaderId} {ServerLoaderVersion}  •  Java {RecommendedJavaMajor?.ToString() ?? "unknown"}"
        : "Content files only";

    public string SummaryText => PreparesServerBaseline && Items.Count == 0
        ? $"Prepare the exact server baseline for {PackName}."
        : PreparesServerBaseline
            ? $"Download {Items.Count:N0} verified file{(Items.Count == 1 ? string.Empty : "s")} ({TotalSizeText}) and prepare the exact server baseline for {PackName}."
        : $"Download {Items.Count:N0} verified file{(Items.Count == 1 ? string.Empty : "s")} ({TotalSizeText}) for {PackName}.";
}

public sealed record PackOutputProgress(
    PackOutputStage Stage,
    string Message,
    long CompletedBytes = 0,
    long? TotalBytes = null,
    int CompletedFiles = 0,
    int TotalFiles = 0)
{
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp(CompletedBytes * 100d / TotalBytes.Value, 0d, 100d)
        : TotalFiles > 0
            ? Math.Clamp(CompletedFiles * 100d / TotalFiles, 0d, 100d)
            : null;
}

public sealed record PackOutputResult(
    string OutputDirectory,
    int DownloadedFileCount,
    int ArrangedFileCount,
    string ManifestPath,
    bool ServerBaselinePrepared,
    string ServerDirectory,
    string ServerLauncherFileName,
    int? RecommendedJavaMajor);

public sealed record MinecraftLauncherInstallRequest(
    string PackName,
    string MinecraftVersion,
    string LoaderId,
    string LoaderVersion,
    string ClientDirectory,
    string ManifestPath);

public sealed record MinecraftLauncherInstallResult(
    string ProfileId,
    string VersionId,
    string LauncherProfilesPath,
    string BackupPath,
    string ClientDirectory,
    string Message);
