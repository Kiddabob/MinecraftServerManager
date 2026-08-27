namespace MinecraftServerManager.Models;

public enum ManagedJavaInstallStage
{
    Checking,
    Downloading,
    Verifying,
    Extracting,
    Installing,
    Validating,
    Completed
}

public sealed record ManagedJavaInstallProgress(
    ManagedJavaInstallStage Stage,
    string Message,
    long? CompletedBytes = null,
    long? TotalBytes = null)
{
    public double? Percent => CompletedBytes is not null
        && TotalBytes is > 0
            ? Math.Clamp(CompletedBytes.Value * 100d / TotalBytes.Value, 0d, 100d)
            : null;
}
