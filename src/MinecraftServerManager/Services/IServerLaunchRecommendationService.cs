using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IServerLaunchRecommendationService
{
    ServerLaunchRecommendation Recommend(
        string serverDirectory,
        string serverType,
        string minecraftVersion,
        int? detectedJavaMajorVersion = null);
}
