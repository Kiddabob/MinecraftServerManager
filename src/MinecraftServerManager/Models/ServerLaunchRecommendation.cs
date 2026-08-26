namespace MinecraftServerManager.Models;

public sealed record ServerLaunchRecommendation(
    int InitialMemoryMb,
    int MaximumMemoryMb,
    int? JavaMajorVersion,
    int ModCount,
    int PluginCount,
    long TotalSystemMemoryMb,
    int LogicalProcessorCount)
{
    public string Summary => JavaMajorVersion is null
        ? $"Recommended {InitialMemoryMb:N0}–{MaximumMemoryMb:N0} MB memory for {ModCount} mods and {PluginCount} plugins."
        : $"Recommended Java {JavaMajorVersion} with {InitialMemoryMb:N0}–{MaximumMemoryMb:N0} MB memory for {ModCount} mods and {PluginCount} plugins.";
}
