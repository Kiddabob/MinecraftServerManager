namespace MinecraftServerManager.Models;

public sealed record ServerConsoleParseResult(
    ServerConsoleSignal Signal,
    ServerLogEntry Entry);
