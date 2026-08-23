namespace MinecraftServerManager.Models;

public sealed record PlayerPlaytimeSnapshot(
    string ProfileId,
    string ProfileName,
    string PlayerName,
    TimeSpan Playtime,
    bool IsOnline,
    DateTimeOffset? LastSeenUtc);
