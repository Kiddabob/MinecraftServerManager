using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IPackPlatformCatalogService
{
    IReadOnlyList<PackBuildTargetOption> GetBuildTargets();

    IReadOnlyList<PackPlatformOption> GetClientPlatforms(string? minecraftVersion = null);

    IReadOnlyList<PackPlatformOption> GetServerPlatforms(string? minecraftVersion = null);

    IReadOnlyList<PackCategoryOption> GetCategories(ServerContentKind kind);
}
