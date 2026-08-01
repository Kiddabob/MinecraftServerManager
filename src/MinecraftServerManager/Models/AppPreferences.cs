namespace MinecraftServerManager.Models;

public sealed record AppPreferences
{
    public int UpdateCheckIntervalMinutes { get; init; } = 15;

    public string Theme { get; init; } = "System";

    public string AccentColor { get; init; } = "System";

    public string? LastProfileId { get; init; }
}
