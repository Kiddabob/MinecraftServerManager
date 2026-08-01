using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerLaunchRequestFactory
{
    ServerLaunchRequest Create(ServerProfile profile);
}
