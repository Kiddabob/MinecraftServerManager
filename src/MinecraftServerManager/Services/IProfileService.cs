using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IProfileService
{
    Task<ServerProfile> LoadAsync(string fileName, CancellationToken cancellationToken = default);
}
