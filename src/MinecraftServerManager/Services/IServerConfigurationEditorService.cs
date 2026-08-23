using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerConfigurationEditorService
{
    ServerConfigurationFriendlyDocument Parse(
        ServerProfile profile,
        ServerConfigurationFile file,
        string content);

    string Apply(ServerConfigurationFriendlyDocument document);
}
