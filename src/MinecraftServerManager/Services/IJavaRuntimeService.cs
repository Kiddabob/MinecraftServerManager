using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IJavaRuntimeService
{
    Task<IReadOnlyList<JavaRuntimeInfo>> DiscoverAsync(
        IEnumerable<string> configuredExecutables,
        CancellationToken cancellationToken = default);

    string ResolveExecutablePath(string configuredExecutable, string versionText);

    JavaRuntimeInfo? FindKnownRuntime(string executablePath);

    int? GetRecommendedJavaMajor(string minecraftVersion);

    string GetCompatibilityMessage(string minecraftVersion, JavaRuntimeInfo? runtime);
}
