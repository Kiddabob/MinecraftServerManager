namespace MinecraftServerManager.Models;

public sealed class ServerExitedEventArgs : EventArgs
{
    public ServerExitedEventArgs(
        int processId,
        int exitCode,
        DateTimeOffset startedAt,
        DateTimeOffset exitedAt,
        bool stopWasRequested,
        bool forceKillWasRequested)
    {
        ProcessId = processId;
        ExitCode = exitCode;
        StartedAt = startedAt;
        ExitedAt = exitedAt;
        StopWasRequested = stopWasRequested;
        ForceKillWasRequested = forceKillWasRequested;
    }

    public int ProcessId { get; }

    public int ExitCode { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset ExitedAt { get; }

    public bool StopWasRequested { get; }

    public bool ForceKillWasRequested { get; }
}
