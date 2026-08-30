using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IPackPlatformCatalogService
{
    IReadOnlyList<PackBuildTargetOption> GetBuildTargets();

    IReadOnlyList<PackPlatformOption> GetClientPlatforms();

    IReadOnlyList<PackPlatformOption> GetServerPlatforms();

    IReadOnlyList<PackCategoryOption> GetCategories(ServerContentKind kind);
}
