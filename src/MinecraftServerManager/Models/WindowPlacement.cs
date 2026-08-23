namespace MinecraftServerManager.Models;

public sealed record WindowPlacement
{
    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; } = 1220;

    public int Height { get; init; } = 800;

    public bool IsMaximized { get; init; }
}
