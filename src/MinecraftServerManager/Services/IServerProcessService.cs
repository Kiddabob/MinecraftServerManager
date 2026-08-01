using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerProcessService
{
    event EventHandler<ServerOutputEventArgs>? OutputReceived;

    event EventHandler<ServerExitedEventArgs>? Exited;

    bool IsRunning { get; }

    int? ProcessId { get; }

    DateTimeOffset? StartedAt { get; }

    Task StartAsync(ServerLaunchRequest request, CancellationToken cancellationToken = default);

    Task SendCommandAsync(string command, CancellationToken cancellationToken = default);

    Task<bool> StopAsync(
        string stopCommand,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
