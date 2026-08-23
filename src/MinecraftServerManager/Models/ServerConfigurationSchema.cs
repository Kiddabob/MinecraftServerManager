namespace MinecraftServerManager.Models;

public sealed class ServerConfigurationSchema
{
    public string FilePattern { get; init; } = string.Empty;

    public IReadOnlyList<ServerConfigurationFieldDefinition> Fields { get; init; } = [];
}

public sealed class ServerConfigurationFieldDefinition
{
    public string Key { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Presentation { get; init; } = string.Empty;

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public double? Step { get; init; }

    public IReadOnlyList<ServerConfigurationOptionDefinition> Options { get; init; } = [];
}

public sealed class ServerConfigurationOptionDefinition
{
    public string Value { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
}
