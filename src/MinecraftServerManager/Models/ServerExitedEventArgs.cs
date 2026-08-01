namespace MinecraftServerManager.Models;

public sealed class ServerExitedEventArgs : EventArgs
{
    public ServerExitedEventArgs(
        int processId,
        int exitCode,
        DateTimeOffset startedAt,
        DateTimeOffset exitedAt,
        bool stopWasRequested)
    {
        ProcessId = processId;
        ExitCode = exitCode;
        StartedAt = startedAt;
        ExitedAt = exitedAt;
        StopWasRequested = stopWasRequested;
    }

    public int ProcessId { get; }

    public int ExitCode { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset ExitedAt { get; }

    public bool StopWasRequested { get; }
}
