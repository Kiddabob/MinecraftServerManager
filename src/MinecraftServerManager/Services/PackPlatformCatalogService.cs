using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class PackPlatformCatalogService : IPackPlatformCatalogService
{
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

    public IReadOnlyList<PackPlatformOption> GetClientPlatforms() => ClientPlatforms;

    public IReadOnlyList<PackPlatformOption> GetServerPlatforms() => ServerPlatforms;

    public IReadOnlyList<PackCategoryOption> GetCategories(ServerContentKind kind) =>
        kind == ServerContentKind.Mod ? ModCategories : PluginCategories;
}
