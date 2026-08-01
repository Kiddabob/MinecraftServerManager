namespace MinecraftServerManager.Models;

public sealed class AppUpdateSettings
{
    public string GitHubRepositoryUrl { get; init; } = string.Empty;

    public int CheckIntervalMinutes { get; init; } = 15;
}
