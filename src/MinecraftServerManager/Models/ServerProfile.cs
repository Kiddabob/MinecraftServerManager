using System.Text.Json.Serialization;

namespace MinecraftServerManager.Models;

public sealed class ServerProfile
{
    public int ProfileFormatVersion { get; set; } = 1;

    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ServerType { get; set; } = string.Empty;

    public string MinecraftVersion { get; set; } = string.Empty;

    public string ForgeVersion { get; set; } = string.Empty;

    public string JavaVersion { get; set; } = string.Empty;

    public string ServerDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public string? IconPath { get; set; }

    public string JavaExecutable { get; set; } = string.Empty;

    public string ServerJar { get; set; } = string.Empty;

    public string LaunchScript { get; set; } = string.Empty;

    public IReadOnlyList<string> JavaArguments { get; set; } = [];

    public IReadOnlyList<string> ServerArguments { get; set; } = [];

    public IReadOnlyList<string> DirectLaunchArguments { get; set; } = [];

    public IReadOnlyList<string> RequiredFiles { get; set; } = [];

    public IReadOnlyList<string> RequiredDirectories { get; set; } = [];

    public IReadOnlyList<ServerConfigurationSource> ConfigurationSources { get; set; } = [];

    public IReadOnlyList<ServerConfigurationSchema> ConfigurationSchemas { get; set; } = [];

    public IReadOnlyList<string> ReadyPatterns { get; set; } = [];

    public IReadOnlyList<string> FailurePatterns { get; set; } = [];

    public IReadOnlyList<string> PlayerJoinPatterns { get; set; } = [];

    public IReadOnlyList<string> PlayerLeavePatterns { get; set; } = [];

    public string ListPlayersCommand { get; set; } = string.Empty;

    public string BroadcastCommandPrefix { get; set; } = string.Empty;

    public string SaveCommand { get; set; } = string.Empty;

    public string StopCommand { get; set; } = "stop";

    public int StopTimeoutSeconds { get; set; } = 60;
}
