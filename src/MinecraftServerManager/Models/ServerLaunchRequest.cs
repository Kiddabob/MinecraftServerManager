namespace MinecraftServerManager.Models;

public sealed class ServerLaunchRequest
{
    public required string ExecutablePath { get; init; }

    public required string WorkingDirectory { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }
}
