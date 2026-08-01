using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerConsoleParser
{
    ServerConsoleParseResult Parse(string line, ServerOutputStream stream);
}
