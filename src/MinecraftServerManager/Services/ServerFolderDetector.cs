using System.Text.RegularExpressions;

namespace MinecraftServerManager.Services;

public sealed record ServerFolderDetection(
    string ServerJar,
    string ServerType,
    string MinecraftVersion);

public static partial class ServerFolderDetector
{
    public static ServerFolderDetection? Detect(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var candidates = Directory
            .EnumerateFiles(folderPath, "*.jar", SearchOption.TopDirectoryOnly)
            .Where(path => !IsNonServerJar(Path.GetFileNameWithoutExtension(path)))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => LauncherScore(file.Name))
            .ThenByDescending(file => file.Length)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selected = candidates.FirstOrDefault();
        if (selected is null)
        {
            return null;
        }

        return new ServerFolderDetection(
            selected.Name,
            DetectServerType(selected.Name),
            DetectMinecraftVersion(selected.Name));
    }

    private static bool IsNonServerJar(string name)
    {
        var normalized = name.ToLowerInvariant();
        return normalized.Contains("installer", StringComparison.Ordinal)
            || normalized.Contains("sources", StringComparison.Ordinal)
            || normalized.Contains("javadoc", StringComparison.Ordinal)
            || normalized.EndsWith("-client", StringComparison.Ordinal);
    }

    private static int LauncherScore(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (name is "server" or "fabric-server-launch" or "quilt-server-launch")
        {
            return 100;
        }

        if (name.StartsWith("minecraft_server", StringComparison.Ordinal)
            || name.StartsWith("paper", StringComparison.Ordinal)
            || name.StartsWith("purpur", StringComparison.Ordinal)
            || name.StartsWith("spigot", StringComparison.Ordinal)
            || name.StartsWith("craftbukkit", StringComparison.Ordinal)
            || name.StartsWith("neoforge", StringComparison.Ordinal)
            || name.StartsWith("forge", StringComparison.Ordinal)
            || name.StartsWith("fabric-server", StringComparison.Ordinal)
            || name.StartsWith("quilt-server", StringComparison.Ordinal))
        {
            return 90;
        }

        return name.Contains("server", StringComparison.Ordinal) ? 70 : 10;
    }

    private static string DetectServerType(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (name.Contains("neoforge", StringComparison.Ordinal)) return "NeoForge";
        if (name.Contains("forge", StringComparison.Ordinal)) return "Forge";
        if (name.Contains("fabric", StringComparison.Ordinal)) return "Fabric";
        if (name.Contains("quilt", StringComparison.Ordinal)) return "Quilt";
        if (name.Contains("paper", StringComparison.Ordinal)) return "Paper";
        if (name.Contains("purpur", StringComparison.Ordinal)) return "Purpur";
        if (name.Contains("spigot", StringComparison.Ordinal)) return "Spigot";
        if (name.Contains("craftbukkit", StringComparison.Ordinal)
            || name.Contains("bukkit", StringComparison.Ordinal)) return "Bukkit";
        if (name.StartsWith("minecraft_server", StringComparison.Ordinal)
            || string.Equals(name, "server.jar", StringComparison.Ordinal)) return "Vanilla";
        return "Minecraft";
    }

    private static string DetectMinecraftVersion(string fileName)
    {
        var match = MinecraftVersionPattern().Match(fileName);
        return match.Success ? match.Groups["version"].Value : "Unknown";
    }

    [GeneratedRegex(@"(?<!\d)(?<version>1\.\d{1,2}(?:\.\d{1,2})?)(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex MinecraftVersionPattern();
}
