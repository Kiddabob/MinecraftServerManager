using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed partial class ServerContentInventoryService : IServerContentInventoryService
{
    private const int MaximumMetadataLength = 1024 * 1024;
    private const long MaximumJarInspectionLength = 512L * 1024 * 1024;
    private const int MaximumItems = 10_000;

    public Task<ServerContentInventory> DiscoverAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Task.Run(() => Discover(profile, cancellationToken), cancellationToken);
    }

    internal static ServerContentInventory Discover(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.ServerDirectory))
        {
            throw new ArgumentException("The profile does not have a server directory.", nameof(profile));
        }

        var serverRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(profile.ServerDirectory));
        if (!Directory.Exists(serverRoot))
        {
            throw new DirectoryNotFoundException($"The server directory does not exist: {serverRoot}");
        }

        var targets = DetectTargets(profile, serverRoot);
        var items = new List<ServerContentItem>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contentDirectory = Path.Combine(serverRoot, target.DirectoryName);
            if (!Directory.Exists(contentDirectory))
            {
                continue;
            }

            foreach (var filePath in EnumerateContentFiles(contentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (items.Count >= MaximumItems)
                {
                    throw new InvalidDataException($"More than {MaximumItems:N0} content files were found. Narrow the server folder before rescanning.");
                }

                var file = new FileInfo(filePath);
                if (file.LinkTarget is not null)
                {
                    continue;
                }

                var metadata = Inspect(file, target.Kind, target.LoaderIds);
                items.Add(new ServerContentItem(
                    metadata.Name,
                    metadata.Id,
                    metadata.Version,
                    target.Kind,
                    metadata.Loader,
                    file.Name,
                    Path.GetRelativePath(serverRoot, file.FullName),
                    file.Length,
                    IsEnabledFile(file.Name)));
            }
        }

        return new ServerContentInventory(
            serverRoot,
            string.IsNullOrWhiteSpace(profile.MinecraftVersion) ? "Unknown" : profile.MinecraftVersion,
            string.IsNullOrWhiteSpace(profile.ServerType) ? "Minecraft" : profile.ServerType,
            targets,
            items
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    internal static IReadOnlyList<ServerContentTarget> DetectTargets(
        ServerProfile profile,
        string serverRoot)
    {
        var type = profile.ServerType.Trim();
        var supportsMods = type.Equals("Forge", StringComparison.OrdinalIgnoreCase)
            || type.Equals("NeoForge", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Fabric", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Quilt", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Hybrid", StringComparison.OrdinalIgnoreCase);
        var supportsPlugins = type.Equals("Paper", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Purpur", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Spigot", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Bukkit", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Plugin server", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Hybrid", StringComparison.OrdinalIgnoreCase);

        // Imported servers are not always labelled accurately. Treat actual content as
        // authoritative so a Forge profile with Bukkit-compatible plugin files (or the
        // reverse) exposes both managers without changing its launch profile.
        supportsMods |= ContainsInstalledContent(serverRoot, "mods");
        supportsPlugins |= ContainsInstalledContent(serverRoot, "plugins");

        var targets = new List<ServerContentTarget>();
        if (supportsMods)
        {
            targets.Add(new ServerContentTarget(
                ServerContentKind.Mod,
                "mods",
                DetectModLoaders(type, profile, serverRoot)));
        }

        if (supportsPlugins)
        {
            targets.Add(new ServerContentTarget(
                ServerContentKind.Plugin,
                "plugins",
                DetectPluginLoaders(type)));
        }

        return targets;
    }

    private static bool ContainsInstalledContent(string serverRoot, string directoryName)
    {
        var directory = Path.Combine(serverRoot, directoryName);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(directory, "*", options)
            .Any(path => IsContentFileName(Path.GetFileName(path)));
    }

    private static IReadOnlyList<string> DetectModLoaders(
        string serverType,
        ServerProfile profile,
        string serverRoot)
    {
        if (serverType.Equals("NeoForge", StringComparison.OrdinalIgnoreCase)) return ["neoforge"];
        if (serverType.Equals("Fabric", StringComparison.OrdinalIgnoreCase)) return ["fabric"];
        if (serverType.Equals("Quilt", StringComparison.OrdinalIgnoreCase)) return ["quilt"];
        if (serverType.Equals("Forge", StringComparison.OrdinalIgnoreCase)) return ["forge"];

        var detected = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.ForgeVersion)
            || Directory.Exists(Path.Combine(serverRoot, "libraries", "net", "minecraftforge")))
        {
            detected.Add("forge");
        }

        if (Directory.Exists(Path.Combine(serverRoot, "libraries", "net", "neoforged")))
        {
            detected.Add("neoforge");
        }

        if (Directory.Exists(Path.Combine(serverRoot, "libraries", "net", "fabricmc")))
        {
            detected.Add("fabric");
        }

        if (Directory.Exists(Path.Combine(serverRoot, "libraries", "org", "quiltmc")))
        {
            detected.Add("quilt");
        }

        return detected.Count > 0 ? detected : ["forge"];
    }

    private static IReadOnlyList<string> DetectPluginLoaders(string serverType)
    {
        if (serverType.Equals("Purpur", StringComparison.OrdinalIgnoreCase)) return ["purpur", "paper", "spigot", "bukkit"];
        if (serverType.Equals("Paper", StringComparison.OrdinalIgnoreCase)) return ["paper", "spigot", "bukkit"];
        if (serverType.Equals("Spigot", StringComparison.OrdinalIgnoreCase)) return ["spigot", "bukkit"];
        return ["bukkit", "spigot", "paper"];
    }

    private static IEnumerable<string> EnumerateContentFiles(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        return Directory.EnumerateFiles(directory, "*", options)
            .Where(path => IsContentFileName(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsContentFileName(string fileName) =>
        fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".jar.off", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".zip.off", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnabledFile(string fileName) =>
        fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static ContentMetadata Inspect(
        FileInfo file,
        ServerContentKind kind,
        IReadOnlyList<string> loaderIds)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(file.Name);
        if (!IsEnabledFile(file.Name))
        {
            fallbackName = Path.GetFileNameWithoutExtension(fallbackName);
        }

        var fallback = new ContentMetadata(
            fallbackName,
            string.Empty,
            string.Empty,
            loaderIds.FirstOrDefault() ?? (kind == ServerContentKind.Mod ? "mod" : "plugin"));
        if (file.Length <= 0 || file.Length > MaximumJarInspectionLength)
        {
            return fallback;
        }

        try
        {
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var discovered = kind == ServerContentKind.Plugin
                ? ReadPluginMetadata(archive) ?? ReadModMetadata(archive) ?? fallback
                : ReadModMetadata(archive) ?? ReadPluginMetadata(archive) ?? fallback;
            return NormalizeMetadata(discovered, fallback);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            return fallback;
        }
    }

    private static ContentMetadata? ReadModMetadata(ZipArchive archive)
    {
        var fabric = ReadEntryText(archive, "fabric.mod.json");
        if (fabric.Length > 0)
        {
            using var document = JsonDocument.Parse(fabric);
            var root = document.RootElement;
            return new ContentMetadata(
                GetJsonString(root, "name", GetJsonString(root, "id", "Fabric mod")),
                GetJsonString(root, "id", string.Empty),
                GetJsonString(root, "version", string.Empty),
                "fabric");
        }

        var quilt = ReadEntryText(archive, "quilt.mod.json");
        if (quilt.Length > 0)
        {
            using var document = JsonDocument.Parse(quilt);
            var loader = document.RootElement.TryGetProperty("quilt_loader", out var value)
                && value.ValueKind == JsonValueKind.Object
                    ? value
                    : default;
            var metadata = loader.ValueKind == JsonValueKind.Object
                && loader.TryGetProperty("metadata", out var metadataValue)
                && metadataValue.ValueKind == JsonValueKind.Object
                    ? metadataValue
                    : default;
            var id = GetJsonString(loader, "id", string.Empty);
            return new ContentMetadata(
                GetJsonString(metadata, "name", id.Length > 0 ? id : "Quilt mod"),
                id,
                GetJsonString(loader, "version", string.Empty),
                "quilt");
        }

        var modsToml = ReadEntryText(archive, "META-INF/mods.toml");
        if (modsToml.Length > 0)
        {
            var id = TomlValue(modsToml, "modId");
            var name = TomlValue(modsToml, "displayName");
            var version = TomlValue(modsToml, "version");
            return new ContentMetadata(
                name.Length > 0 ? name : id.Length > 0 ? id : "Forge mod",
                id,
                IsTemplateValue(version) ? string.Empty : version,
                "forge");
        }

        var neoForgeToml = ReadEntryText(archive, "META-INF/neoforge.mods.toml");
        if (neoForgeToml.Length > 0)
        {
            var id = TomlValue(neoForgeToml, "modId");
            var name = TomlValue(neoForgeToml, "displayName");
            var version = TomlValue(neoForgeToml, "version");
            return new ContentMetadata(
                name.Length > 0 ? name : id.Length > 0 ? id : "NeoForge mod",
                id,
                IsTemplateValue(version) ? string.Empty : version,
                "neoforge");
        }

        var legacy = ReadEntryText(archive, "mcmod.info");
        return legacy.Length > 0 ? ReadLegacyForgeMetadata(legacy) : null;
    }

    private static ContentMetadata? ReadPluginMetadata(ZipArchive archive)
    {
        foreach (var metadataPath in new[] { "paper-plugin.yml", "plugin.yml" })
        {
            var yaml = ReadEntryText(archive, metadataPath);
            if (yaml.Length == 0)
            {
                continue;
            }

            var name = YamlValue(yaml, "name");
            var version = YamlValue(yaml, "version");
            var mainClass = YamlValue(yaml, "main");
            if (name.Length > 0 || mainClass.Length > 0)
            {
                return new ContentMetadata(
                    name.Length > 0 ? name : mainClass,
                    mainClass,
                    version,
                    metadataPath.StartsWith("paper", StringComparison.Ordinal) ? "paper" : "bukkit");
            }
        }

        return null;
    }

    private static ContentMetadata? ReadLegacyForgeMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        var candidate = document.RootElement;
        if (candidate.ValueKind == JsonValueKind.Array)
        {
            candidate = candidate.EnumerateArray().FirstOrDefault();
        }
        else if (candidate.ValueKind == JsonValueKind.Object
            && candidate.TryGetProperty("modList", out var modList)
            && modList.ValueKind == JsonValueKind.Array)
        {
            candidate = modList.EnumerateArray().FirstOrDefault();
        }

        if (candidate.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetJsonString(candidate, "modid", string.Empty);
        var name = GetJsonString(candidate, "name", id);
        var version = GetJsonString(candidate, "version", string.Empty);
        return name.Length == 0 && id.Length == 0
            ? null
            : new ContentMetadata(
                name.Length > 0 ? name : id,
                id,
                IsTemplateValue(version) ? string.Empty : version,
                "forge");
    }

    private static ContentMetadata NormalizeMetadata(
        ContentMetadata metadata,
        ContentMetadata fallback)
    {
        var isPlaceholder = metadata.Name.Equals("Example Mod", StringComparison.OrdinalIgnoreCase)
            || metadata.Id.Equals("examplemod", StringComparison.OrdinalIgnoreCase)
            || metadata.Id.Equals("example_mod", StringComparison.OrdinalIgnoreCase);
        return metadata with
        {
            Name = isPlaceholder || string.IsNullOrWhiteSpace(metadata.Name)
                ? fallback.Name
                : metadata.Name,
            Id = isPlaceholder ? string.Empty : metadata.Id,
            Version = isPlaceholder || IsTemplateValue(metadata.Version)
                ? string.Empty
                : metadata.Version
        };
    }

    private static string ReadEntryText(ZipArchive archive, string path)
    {
        var entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length <= 0 || entry.Length > MaximumMetadataLength)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        var buffer = new char[MaximumMetadataLength];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, count);
    }

    private static string GetJsonString(
        JsonElement element,
        string propertyName,
        string fallback)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? fallback,
            JsonValueKind.Number => value.GetRawText(),
            _ => fallback
        };
    }

    private static string TomlValue(string text, string key)
    {
        var match = Regex.Match(
            text,
            $"(?im)^\\s*{Regex.Escape(key)}\\s*=\\s*[\\\"'](?<value>[^\\\"']+)[\\\"']");
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static string YamlValue(string text, string key)
    {
        var match = Regex.Match(
            text,
            $"(?im)^{Regex.Escape(key)}\\s*:\\s*(?<value>[^#\\r\\n]+)");
        return match.Success
            ? match.Groups["value"].Value.Trim().Trim('"', '\'')
            : string.Empty;
    }

    private static bool IsTemplateValue(string value) =>
        value.Contains("${", StringComparison.Ordinal)
        || value.Contains("@", StringComparison.Ordinal);

    private sealed record ContentMetadata(
        string Name,
        string Id,
        string Version,
        string Loader);
}
