using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerConsoleParserFactory
{
    IServerConsoleParser Create(ServerProfile profile);
}
