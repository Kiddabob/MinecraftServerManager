namespace MinecraftServerManager.Models;

public sealed class ServerProfile
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string ServerType { get; init; } = string.Empty;

    public string MinecraftVersion { get; init; } = string.Empty;

    public string ForgeVersion { get; init; } = string.Empty;

    public string JavaVersion { get; init; } = string.Empty;

    public string ServerDirectory { get; set; } = string.Empty;

    public string JavaExecutable { get; set; } = string.Empty;

    public string ServerJar { get; init; } = string.Empty;

    public IReadOnlyList<string> JavaArguments { get; init; } = [];

    public IReadOnlyList<string> ServerArguments { get; init; } = [];

    public IReadOnlyList<string> RequiredFiles { get; init; } = [];

    public IReadOnlyList<string> RequiredDirectories { get; init; } = [];

    public IReadOnlyList<string> ReadyPatterns { get; init; } = [];

    public string StopCommand { get; init; } = "stop";

    public int StopTimeoutSeconds { get; init; } = 60;
}
