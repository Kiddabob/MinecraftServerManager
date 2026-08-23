using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IWindowPlacementService
{
    WindowPlacement? Current { get; }

    WindowPlacement? Load();

    Task SaveAsync(WindowPlacement placement, CancellationToken cancellationToken = default);
}
