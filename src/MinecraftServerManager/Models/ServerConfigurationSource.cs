namespace MinecraftServerManager.Models;

public sealed class ServerConfigurationSource
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Category { get; init; } = "Other";

    public string RelativePath { get; init; } = string.Empty;

    public IReadOnlyList<string> FilePatterns { get; init; } = [];

    public bool Recursive { get; init; }
}
