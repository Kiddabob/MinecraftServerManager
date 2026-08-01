using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IAppUpdateService
{
    event EventHandler<AppUpdateStatusChangedEventArgs>? StatusChanged;

    bool IsUpdateReady { get; }

    void SetCheckIntervalMinutes(int minutes);

    void StartMonitoring();

    void ApplyUpdateAndRestart();
}
