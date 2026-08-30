using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class PackPlatformCatalogService : IPackPlatformCatalogService
{
    // Keep this allowlist explicit: Forge intentionally skips some Minecraft releases,
    // so a broad version range would offer combinations for which Forge has no build.
    private static readonly HashSet<string> ForgeMinecraftVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        "26.2", "26.1.2", "26.1.1", "26.1",
        "1.21.11", "1.21.10", "1.21.9", "1.21.8", "1.21.7", "1.21.6", "1.21.5",
        "1.21.4", "1.21.3", "1.21.1", "1.21",
        "1.20.6", "1.20.4", "1.20.3", "1.20.2", "1.20.1", "1.20",
        "1.19.4", "1.19.3", "1.19.2", "1.19.1", "1.19",
        "1.18.2", "1.18.1", "1.18", "1.17.1",
        "1.16.5", "1.16.4", "1.16.3", "1.16.2", "1.16.1",
        "1.15.2", "1.15.1", "1.15", "1.14.4", "1.14.3", "1.14.2", "1.13.2",
        "1.12.2", "1.12.1", "1.12", "1.11.2", "1.11", "1.10.2", "1.10",
        "1.9.4", "1.9", "1.8.9", "1.8.8", "1.8", "1.7.10", "1.7.2",
        "1.6.4", "1.6.3", "1.6.2", "1.6.1", "1.5.2", "1.5.1", "1.5",
        "1.4.7", "1.4.6", "1.4.5", "1.4.4", "1.4.3", "1.4.2", "1.4.1", "1.4.0",
        "1.3.2", "1.2.5", "1.2.4", "1.2.3", "1.1"
    };

    private static readonly PackBuildTargetOption[] Targets =
    [
        new(PackBuildTarget.Client, "Client pack", "Build a playable client instance. Server-only files stay out."),
        new(PackBuildTarget.Server, "Server pack", "Build a dedicated server. Client-only files stay out."),
        new(PackBuildTarget.ClientAndServer, "Client + server", "Plan a linked pair and place each file on the correct side.")
    ];

    private static readonly PackPlatformOption[] ClientPlatforms =
    [
        new("vanilla-client", "Vanilla", PackPlatformKind.Vanilla,
            "Best for an unmodified client", "No community mods or plugins", [], true, false, false, false),
        new("fabric-client", "Fabric", PackPlatformKind.ModLoader,
            "Best for lightweight and performance-focused packs", "Broad modern mod catalogue and fast loader", ["fabric"], true, false, true, false),
        new("quilt-client", "Quilt", PackPlatformKind.ModLoader,
            "Best for Fabric-compatible experimentation", "Smaller ecosystem with many Fabric-compatible projects", ["quilt", "fabric"], true, false, true, false),
        new("forge-client", "Forge", PackPlatformKind.ModLoader,
            "Best for established and large content packs", "Very broad historical mod catalogue", ["forge"], true, false, true, false),
        new("neoforge-client", "NeoForge", PackPlatformKind.ModLoader,
            "Best for newer Forge-style packs", "Modern continuation used by many current mods", ["neoforge"], true, false, true, false)
    ];

    private static readonly PackPlatformOption[] ServerPlatforms =
    [
        new("vanilla-server", "Vanilla", PackPlatformKind.Vanilla,
            "Best for the standard Minecraft server", "No community mods or plugins", [], false, true, false, false),
        new("paper-server", "Paper", PackPlatformKind.PluginPlatform,
            "Best for high-performance plugin servers", "Bukkit, Spigot, and Paper plugin ecosystem", ["paper", "spigot", "bukkit"], false, true, false, true),
        new("fabric-server", "Fabric", PackPlatformKind.ModLoader,
            "Best for lightweight modded servers", "Use the same Fabric-compatible mods as the client where required", ["fabric"], false, true, true, false),
        new("quilt-server", "Quilt", PackPlatformKind.ModLoader,
            "Best for Quilt and compatible Fabric server mods", "Smaller ecosystem; verify each project's declared loaders", ["quilt", "fabric"], false, true, true, false),
        new("forge-server", "Forge", PackPlatformKind.ModLoader,
            "Best for established modded servers", "Matches the broad historical Forge catalogue", ["forge"], false, true, true, false),
        new("neoforge-server", "NeoForge", PackPlatformKind.ModLoader,
            "Best for newer Forge-style servers", "Matches modern NeoForge client packs", ["neoforge"], false, true, true, false),
        new("hybrid-forge-server", "Forge + plugins (hybrid)", PackPlatformKind.HybridPlatform,
            "For packs that genuinely require mods and plugins", "Compatibility varies by hybrid implementation and Minecraft version", ["forge", "paper", "spigot", "bukkit"], false, true, true, true, true)
    ];

    private static readonly PackCategoryOption[] ModCategories =
    [
        new("", "All mod types"),
        new("adventure", "Adventure"),
        new("decoration", "Decoration"),
        new("equipment", "Equipment"),
        new("food", "Food"),
        new("game-mechanics", "Game mechanics"),
        new("library", "Libraries"),
        new("magic", "Magic"),
        new("management", "Management"),
        new("optimization", "Optimization"),
        new("social", "Social"),
        new("storage", "Storage"),
        new("technology", "Technology"),
        new("transportation", "Transportation"),
        new("utility", "Utility"),
        new("worldgen", "World generation")
    ];

    private static readonly PackCategoryOption[] PluginCategories =
    [
        new("", "All plugin types"),
        new("adventure", "Adventure"),
        new("economy", "Economy"),
        new("game-mechanics", "Game mechanics"),
        new("management", "Management"),
        new("minigame", "Minigames"),
        new("social", "Social"),
        new("storage", "Storage"),
        new("utility", "Utility")
    ];

    public IReadOnlyList<PackBuildTargetOption> GetBuildTargets() => Targets;

    public IReadOnlyList<PackPlatformOption> GetClientPlatforms(string? minecraftVersion = null) =>
        FilterByMinecraftVersion(ClientPlatforms, minecraftVersion);

    public IReadOnlyList<PackPlatformOption> GetServerPlatforms(string? minecraftVersion = null) =>
        FilterByMinecraftVersion(ServerPlatforms, minecraftVersion);

    public IReadOnlyList<PackCategoryOption> GetCategories(ServerContentKind kind) =>
        kind == ServerContentKind.Mod ? ModCategories : PluginCategories;

    private static IReadOnlyList<PackPlatformOption> FilterByMinecraftVersion(
        IReadOnlyList<PackPlatformOption> platforms,
        string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return platforms;
        }

        return platforms
            .Where(platform => SupportsMinecraftVersion(platform.Id, minecraftVersion))
            .ToArray();
    }

    private static bool SupportsMinecraftVersion(string platformId, string minecraftVersion) =>
        platformId switch
        {
            "vanilla-client" or "vanilla-server" => true,
            "fabric-client" or "fabric-server" => IsAtLeast(minecraftVersion, "1.14"),
            "quilt-client" or "quilt-server" => IsAtLeast(minecraftVersion, "1.14.4"),
            "forge-client" or "forge-server" => ForgeMinecraftVersions.Contains(minecraftVersion),
            "neoforge-client" or "neoforge-server" => IsAtLeast(minecraftVersion, "1.20.2"),
            "paper-server" => IsAtLeast(minecraftVersion, "1.7.10"),
            "hybrid-forge-server" => IsAtLeast(minecraftVersion, "1.7.10")
                && ForgeMinecraftVersions.Contains(minecraftVersion),
            _ => false
        };

    private static bool IsAtLeast(string minecraftVersion, string minimumVersion)
    {
        if (!TryParseVersion(minecraftVersion, out var version)
            || !TryParseVersion(minimumVersion, out var minimum))
        {
            return false;
        }

        var partCount = Math.Max(version.Length, minimum.Length);
        for (var index = 0; index < partCount; index++)
        {
            var versionPart = index < version.Length ? version[index] : 0;
            var minimumPart = index < minimum.Length ? minimum[index] : 0;
            if (versionPart != minimumPart)
            {
                return versionPart > minimumPart;
            }
        }

        return true;
    }

    private static bool TryParseVersion(string value, out int[] parts)
    {
        var segments = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        parts = new int[segments.Length];
        if (segments.Length == 0)
        {
            return false;
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (!int.TryParse(segments[index], out parts[index]) || parts[index] < 0)
            {
                parts = [];
                return false;
            }
        }

        return true;
    }
}
