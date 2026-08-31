using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IWorldMapService
{
    Task<WorldMapDescriptor> DiscoverAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<WorldMapRenderResult> RenderAsync(
        WorldMapRenderRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorldMapPlayerPosition>> ReadPlayerPositionsAsync(
        ServerProfile profile,
        WorldMapDescriptor world,
        IReadOnlyCollection<string>? playerNames,
        CancellationToken cancellationToken = default);
}
