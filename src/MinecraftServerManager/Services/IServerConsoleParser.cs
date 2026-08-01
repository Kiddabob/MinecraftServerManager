using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerConsoleParser
{
    ServerConsoleSignal Parse(string line);
}
