namespace MinecraftServerManager.Models;

public sealed record ServerResourceUsage(
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int ThreadCount,
    TimeSpan Uptime);
