namespace MinecraftServerManager.Models;

public enum ModpackImportStage
{
    DownloadingPackage,
    VerifyingPackage,
    InspectingPackage,
    DownloadingFiles,
    ExtractingOverrides,
    InstallingServerBaseline,
    CreatingProfile,
    Complete
}

public sealed record ModpackImportProgress(
    ModpackImportStage Stage,
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

public sealed record ModpackImportResult(
    string ServerDirectory,
    string MinecraftVersion,
    string LoaderId,
    string LoaderVersion,
    int InstalledFileCount,
    ProfileImportResult ProfileImport)
{
    public bool IsRunnable => ProfileImport.Profile is not null;
}
