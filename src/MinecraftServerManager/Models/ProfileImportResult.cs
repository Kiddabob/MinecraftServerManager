namespace MinecraftServerManager.Models;

public sealed record ProfileImportResult(
    ServerProfile? Profile,
    bool WasCreated,
    string Message);
