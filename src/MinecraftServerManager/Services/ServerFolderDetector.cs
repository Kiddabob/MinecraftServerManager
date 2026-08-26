using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Services;

public sealed record ServerFolderDetection(
    string ServerJar,
    string ServerType,
    string MinecraftVersion,
    string LaunchScript = "",
    string JavaExecutable = "java",
    IReadOnlyList<string>? JavaArguments = null,
    IReadOnlyList<string>? ServerArguments = null,
    IReadOnlyList<string>? DirectLaunchArguments = null,
    int Score = 0,
    int? RequiredJavaMajorVersion = null)
{
    public IReadOnlyList<string> EffectiveJavaArguments => JavaArguments ?? [];

    public IReadOnlyList<string> EffectiveServerArguments => ServerArguments ?? [];

    public IReadOnlyList<string> EffectiveDirectLaunchArguments => DirectLaunchArguments ?? [];

    public string DisplayName
    {
        get
        {
            var launcher = string.IsNullOrWhiteSpace(ServerJar)
                ? "Forge argument-file launch"
                : ServerJar;
            var hint = string.IsNullOrWhiteSpace(LaunchScript)
                ? string.Empty
                : $"  •  hinted by {LaunchScript}";
            return $"{launcher}  •  {ServerType}  •  Minecraft {MinecraftVersion}{hint}";
        }
    }
}

internal sealed record ServerJarInspection(
    string MainClass,
    string MinecraftVersion,
    int? RequiredJavaMajorVersion,
    bool HasForge,
    bool HasNeoForge,
    bool HasFabric,
    bool HasQuilt,
    bool HasCraftBukkit,
    bool HasPaper,
    bool HasPurpur,
    bool HasSpigot)
{
    public bool HasModLoader => HasForge || HasNeoForge || HasFabric || HasQuilt;

    public bool HasPluginPlatform => HasCraftBukkit || HasPaper || HasPurpur || HasSpigot;
}

internal sealed record ServerScriptHint(
    string ScriptName,
    string ServerJar,
    IReadOnlyList<string> DirectLaunchArguments,
    int Score);

public static partial class ServerFolderDetector
{
    private const int MaximumScriptLength = 256 * 1024;
    private const int MaximumLogLength = 2 * 1024 * 1024;
    private const int MaximumManifestLength = 256 * 1024;
    private const int MaximumVersionJsonLength = 1024 * 1024;

    public static ServerFolderDetection? Detect(string folderPath) =>
        DetectCandidates(folderPath).FirstOrDefault();

    public static IReadOnlyList<ServerFolderDetection> DetectCandidates(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Directory.Exists(folderPath))
        {
            return [];
        }

        var hints = EnumerateLaunchScripts(folderPath)
            .SelectMany(path => DetectScriptHints(folderPath, path))
            .ToArray();
        var jarPaths = EnumerateServerJars(folderPath)
            .Concat(hints
                .Where(hint => !string.IsNullOrWhiteSpace(hint.ServerJar))
                .Select(hint => Path.Combine(folderPath, hint.ServerJar))
                .Where(File.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidates = new List<ServerFolderDetection>();
        foreach (var jarPath in jarPaths)
        {
            var relativeJar = Path.GetRelativePath(folderPath, jarPath);
            var hint = hints
                .Where(candidate => PathsEqual(candidate.ServerJar, relativeJar))
                .OrderByDescending(candidate => candidate.Score)
                .FirstOrDefault();
            var inspection = InspectJar(jarPath);
            var score = LauncherScore(relativeJar)
                + (inspection is { MainClass.Length: > 0 } ? 150 : 0)
                + (inspection?.HasModLoader == true || inspection?.HasPluginPlatform == true ? 100 : 0)
                + (hint?.Score ?? 0);
            candidates.Add(CreateDetection(
                folderPath,
                relativeJar,
                hint?.ScriptName ?? string.Empty,
                javaArguments: [],
                serverArguments: ["nogui"],
                directLaunchArguments: [],
                score,
                inspection));
        }

        foreach (var hint in hints.Where(candidate => candidate.DirectLaunchArguments.Count > 0))
        {
            candidates.Add(CreateDetection(
                folderPath,
                serverJar: string.Empty,
                hint.ScriptName,
                javaArguments: [],
                serverArguments: ["nogui"],
                hint.DirectLaunchArguments,
                hint.Score + 100,
                inspection: null));
        }

        return candidates
            .GroupBy(
                candidate => $"{candidate.ServerJar}\0{CommandLineArgumentParser.Join(candidate.EffectiveDirectLaunchArguments)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateLaunchScripts(string folderPath)
    {
        return Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".bat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".sh", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => ScriptPriority(Path.GetFileName(path)))
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<ServerScriptHint> DetectScriptHints(string folderPath, string scriptPath)
    {
        string scriptText;
        try
        {
            var info = new FileInfo(scriptPath);
            if (info.Length > MaximumScriptLength)
            {
                yield break;
            }

            scriptText = File.ReadAllText(scriptPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            yield break;
        }

        var scriptName = Path.GetFileName(scriptPath);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var logicalLine in EnumerateLogicalLines(scriptText))
        {
            var lineNumber = logicalLine.LineNumber;
            var line = logicalLine.Text.Trim();
            if (line.Length == 0 || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("::", StringComparison.Ordinal)
                || line.StartsWith('#'))
            {
                continue;
            }

            if (TryReadVariableAssignment(line, variables))
            {
                continue;
            }

            line = ExpandScriptVariables(line, variables);

            var tokens = CommandLineArgumentParser.Split(line).ToList();
            var javaIndex = tokens.FindIndex(IsJavaExecutableToken);
            if (javaIndex < 0)
            {
                continue;
            }

            var launchTokens = tokens.Skip(javaIndex + 1)
                .TakeWhile(token => token is not "&" and not "&&" and not "||")
                .Where(token => token is not "%*" and not "$@")
                .ToArray();
            var jarIndex = Array.FindIndex(
                launchTokens,
                token => token.Equals("-jar", StringComparison.OrdinalIgnoreCase));
            var score = 1000
                + (10 - Math.Min(lineNumber, 10))
                + (ScriptPriorityScore(scriptName) * 10);
            if (jarIndex >= 0 && jarIndex + 1 < launchTokens.Length)
            {
                if (!TryNormalizeContainedPath(
                        folderPath,
                        launchTokens[jarIndex + 1],
                        out var serverJar)
                    || !File.Exists(Path.Combine(folderPath, serverJar)))
                {
                    continue;
                }

                if (IsNonServerJar(Path.GetFileNameWithoutExtension(serverJar))
                    || launchTokens.Any(token => token.Contains("installServer", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                score += 60;
                yield return new ServerScriptHint(scriptName, serverJar, [], score);
                continue;
            }

            var directArguments = launchTokens
                .Where(IsMainArgumentFile)
                .Select(token => TryNormalizeArgumentFile(folderPath, token))
                .Where(argument => argument is not null)
                .Select(argument => argument!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (directArguments.Length > 0)
            {
                yield return new ServerScriptHint(scriptName, string.Empty, directArguments, score + 60);
            }
        }
    }

    private static IEnumerable<(int LineNumber, string Text)> EnumerateLogicalLines(string scriptText)
    {
        var physicalLines = scriptText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        var startLine = 1;
        for (var index = 0; index < physicalLines.Length; index++)
        {
            var part = physicalLines[index].TrimEnd();
            if (builder.Length == 0)
            {
                startLine = index + 1;
            }

            var continues = part.EndsWith('^')
                || (part.EndsWith('\\') && !part.EndsWith("\\\\", StringComparison.Ordinal));
            builder.Append(continues ? part[..^1] : part);
            if (continues)
            {
                builder.Append(' ');
                continue;
            }

            yield return (startLine, builder.ToString());
            builder.Clear();
        }

        if (builder.Length > 0)
        {
            yield return (startLine, builder.ToString());
        }
    }

    private static bool TryReadVariableAssignment(
        string line,
        Dictionary<string, string> variables)
    {
        var assignment = line.StartsWith("set ", StringComparison.OrdinalIgnoreCase)
            ? line[4..].Trim().Trim('"')
            : line.StartsWith("export ", StringComparison.OrdinalIgnoreCase)
                ? line[7..].Trim()
                : string.Empty;
        var separator = assignment.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        var name = assignment[..separator].Trim();
        if (name.Length == 0 || name.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            return false;
        }

        variables[name] = ExpandScriptVariables(
            assignment[(separator + 1)..].Trim().Trim('"', '\''),
            variables);
        return true;
    }

    private static string ExpandScriptVariables(
        string value,
        IReadOnlyDictionary<string, string> variables)
    {
        foreach (var variable in variables.OrderByDescending(pair => pair.Key.Length))
        {
            value = value.Replace($"%{variable.Key}%", variable.Value, StringComparison.OrdinalIgnoreCase)
                .Replace($"!{variable.Key}!", variable.Value, StringComparison.OrdinalIgnoreCase)
                .Replace($"${{{variable.Key}}}", variable.Value, StringComparison.Ordinal)
                .Replace($"${variable.Key}", variable.Value, StringComparison.Ordinal);
        }

        return Environment.ExpandEnvironmentVariables(value);
    }

    private static ServerFolderDetection CreateDetection(
        string folderPath,
        string serverJar,
        string launchScript,
        IReadOnlyList<string> javaArguments,
        IReadOnlyList<string> serverArguments,
        IReadOnlyList<string> directLaunchArguments,
        int score,
        ServerJarInspection? inspection)
    {
        var minecraftVersion = inspection?.MinecraftVersion ?? "Unknown";
        if (minecraftVersion == "Unknown")
        {
            minecraftVersion = DetectMinecraftVersion(
                $"{serverJar} {string.Join(' ', directLaunchArguments)}");
        }

        if (minecraftVersion == "Unknown")
        {
            minecraftVersion = DetectVersionFromFolderJars(folderPath);
        }

        if (minecraftVersion == "Unknown")
        {
            minecraftVersion = DetectVersionFromLogs(folderPath);
        }

        if (minecraftVersion == "Unknown")
        {
            minecraftVersion = DetectMinecraftVersion(new DirectoryInfo(folderPath).Name);
        }

        return new ServerFolderDetection(
            serverJar,
            DetectServerType(folderPath, serverJar, directLaunchArguments, inspection),
            minecraftVersion,
            launchScript,
            "java",
            javaArguments,
            serverArguments,
            directLaunchArguments,
            score,
            inspection?.RequiredJavaMajorVersion);
    }

    private static IEnumerable<string> EnumerateServerJars(string folderPath)
    {
        return Directory
            .EnumerateFiles(folderPath, "*.jar", SearchOption.TopDirectoryOnly)
            .Where(path => !IsNonServerJar(Path.GetFileNameWithoutExtension(path)))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => LauncherScore(file.Name))
            .ThenByDescending(file => file.Length)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName);
    }

    private static ServerJarInspection? InspectJar(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var entries = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            var manifest = ReadEntryText(
                archive.Entries.FirstOrDefault(entry => entry.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase)),
                MaximumManifestLength);
            var mainClass = ManifestValue(manifest, "Main-Class");
            var version = DetectVersionFromJarMetadata(archive, manifest, Path.GetFileName(jarPath));
            var joinedIdentity = $"{Path.GetFileName(jarPath)} {mainClass}".ToLowerInvariant();
            return new ServerJarInspection(
                mainClass,
                version,
                DetectRequiredJavaMajor(archive, mainClass),
                HasEntryPrefix(entries, "net/minecraftforge/")
                    || HasEntryPrefix(entries, "cpw/mods/fml/")
                    || joinedIdentity.Contains("forge", StringComparison.Ordinal),
                HasEntryPrefix(entries, "net/neoforged/")
                    || joinedIdentity.Contains("neoforge", StringComparison.Ordinal),
                HasEntryPrefix(entries, "net/fabricmc/")
                    || joinedIdentity.Contains("fabric", StringComparison.Ordinal),
                HasEntryPrefix(entries, "org/quiltmc/")
                    || joinedIdentity.Contains("quilt", StringComparison.Ordinal),
                HasEntryPrefix(entries, "org/bukkit/craftbukkit/")
                    || joinedIdentity.Contains("craftbukkit", StringComparison.Ordinal)
                    || joinedIdentity.Contains("mcpc", StringComparison.Ordinal)
                    || joinedIdentity.Contains("cauldron", StringComparison.Ordinal)
                    || joinedIdentity.Contains("mohist", StringComparison.Ordinal)
                    || joinedIdentity.Contains("arclight", StringComparison.Ordinal),
                HasEntryPrefix(entries, "io/papermc/")
                    || joinedIdentity.Contains("paper", StringComparison.Ordinal),
                HasEntryPrefix(entries, "org/purpurmc/")
                    || joinedIdentity.Contains("purpur", StringComparison.Ordinal),
                joinedIdentity.Contains("spigot", StringComparison.Ordinal));
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string DetectVersionFromJarMetadata(
        ZipArchive archive,
        string manifest,
        string fileName)
    {
        var version = DetectMinecraftVersion(fileName);
        if (version != "Unknown")
        {
            return version;
        }

        var versionEntry = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals("version.json", StringComparison.OrdinalIgnoreCase));
        var versionJson = ReadEntryText(versionEntry, MaximumVersionJsonLength);
        if (!string.IsNullOrWhiteSpace(versionJson))
        {
            try
            {
                using var document = JsonDocument.Parse(versionJson);
                foreach (var propertyName in new[] { "id", "name" })
                {
                    if (document.RootElement.TryGetProperty(propertyName, out var property))
                    {
                        version = DetectMinecraftVersion(property.GetString() ?? string.Empty);
                        if (version != "Unknown")
                        {
                            return version;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed optional metadata is ignored; other evidence is still considered.
            }
        }

        foreach (var key in new[] { "Minecraft-Version", "Implementation-Version", "Class-Path" })
        {
            version = DetectMinecraftVersion(ManifestValue(manifest, key));
            if (version != "Unknown")
            {
                return version;
            }
        }

        return "Unknown";
    }

    private static string ReadEntryText(ZipArchiveEntry? entry, int maximumLength)
    {
        if (entry is null || entry.Length > maximumLength)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maximumLength];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, count);
    }

    private static int? DetectRequiredJavaMajor(ZipArchive archive, string mainClass)
    {
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            return null;
        }

        var classPath = mainClass.Replace('.', '/') + ".class";
        var entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.Equals(classPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length < 8)
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            Span<byte> header = stackalloc byte[8];
            stream.ReadExactly(header);
            if (header[0] != 0xCA
                || header[1] != 0xFE
                || header[2] != 0xBA
                || header[3] != 0xBE)
            {
                return null;
            }

            var classMajor = (header[6] << 8) | header[7];
            return classMajor switch
            {
                52 => 8,
                >= 53 and <= 100 => classMajor - 44,
                _ => null
            };
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string ManifestValue(string manifest, string key)
    {
        if (string.IsNullOrWhiteSpace(manifest))
        {
            return string.Empty;
        }

        var unfolded = manifest.Replace("\r\n ", string.Empty, StringComparison.Ordinal)
            .Replace("\n ", string.Empty, StringComparison.Ordinal);
        foreach (var line in unfolded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator > 0 && line[..separator].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    private static bool HasEntryPrefix(IEnumerable<string> entries, string prefix) =>
        entries.Any(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsJavaExecutableToken(string token)
    {
        var normalized = token.Trim('"', '\'').Replace('/', '\\');
        var fileName = Path.GetFileName(normalized);
        return fileName.Equals("java", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("java.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("javaw", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMainArgumentFile(string token)
    {
        if (!token.StartsWith('@'))
        {
            return false;
        }

        var normalized = token.TrimStart('@').Trim('"', '\'').Replace('\\', '/');
        return normalized.EndsWith("win_args.txt", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("unix_args.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryNormalizeArgumentFile(string folderPath, string token)
    {
        return TryNormalizeContainedPath(folderPath, token.TrimStart('@'), out var path)
            && File.Exists(Path.Combine(folderPath, path))
            ? $"@{path.Replace('\\', '/')}"
            : null;
    }

    private static string NormalizeRelativePath(string token)
    {
        var normalized = token.Trim('"', '\'').Replace('/', Path.DirectorySeparatorChar);
        while (normalized.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static bool TryNormalizeContainedPath(
        string folderPath,
        string token,
        out string relativePath)
    {
        try
        {
            var root = Path.GetFullPath(folderPath);
            var normalized = NormalizeRelativePath(token);
            var resolved = Path.GetFullPath(Path.Combine(root, normalized));
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = string.Empty;
                return false;
            }

            relativePath = Path.GetRelativePath(root, resolved);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            relativePath = string.Empty;
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeRelativePath(left),
            NormalizeRelativePath(right),
            StringComparison.OrdinalIgnoreCase);

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
        if (name is "server" or "fabric-server-launch" or "quilt-server-launch") return 100;
        if (name.StartsWith("minecraft_server", StringComparison.Ordinal)
            || name.StartsWith("paper", StringComparison.Ordinal)
            || name.StartsWith("purpur", StringComparison.Ordinal)
            || name.StartsWith("spigot", StringComparison.Ordinal)
            || name.StartsWith("craftbukkit", StringComparison.Ordinal)
            || name.StartsWith("neoforge", StringComparison.Ordinal)
            || name.StartsWith("forge", StringComparison.Ordinal)
            || name.StartsWith("fabric-server", StringComparison.Ordinal)
            || name.StartsWith("quilt-server", StringComparison.Ordinal)) return 90;
        return name.Contains("server", StringComparison.Ordinal) ? 70 : 10;
    }

    private static int ScriptPriority(string fileName) => fileName.ToLowerInvariant() switch
    {
        "run.bat" => 0,
        "start.bat" => 1,
        "launch.bat" => 2,
        "server.bat" => 3,
        "run.cmd" => 4,
        "start.cmd" => 5,
        "run.sh" => 6,
        "start.sh" => 7,
        _ => 20
    };

    private static int ScriptPriorityScore(string fileName) => Math.Max(0, 20 - ScriptPriority(fileName));

    private static string DetectServerType(
        string folderPath,
        string fileName,
        IReadOnlyList<string> directLaunchArguments,
        ServerJarInspection? inspection)
    {
        if (inspection is { HasModLoader: true, HasPluginPlatform: true }) return "Hybrid";
        if (inspection?.HasPurpur == true) return "Purpur";
        if (inspection?.HasPaper == true) return "Paper";
        if (inspection?.HasSpigot == true) return "Spigot";
        if (inspection?.HasCraftBukkit == true) return "Bukkit";
        if (inspection?.HasNeoForge == true) return "NeoForge";
        if (inspection?.HasFabric == true) return "Fabric";
        if (inspection?.HasQuilt == true) return "Quilt";
        if (inspection?.HasForge == true) return "Forge";

        var name = $"{fileName} {string.Join(' ', directLaunchArguments)}".ToLowerInvariant();
        if (name.Contains("neoforge", StringComparison.Ordinal)) return "NeoForge";
        if (name.Contains("forge", StringComparison.Ordinal)) return "Forge";
        if (name.Contains("fabric", StringComparison.Ordinal)) return "Fabric";
        if (name.Contains("quilt", StringComparison.Ordinal)) return "Quilt";
        if (name.Contains("paper", StringComparison.Ordinal)) return "Paper";
        if (name.Contains("purpur", StringComparison.Ordinal)) return "Purpur";
        if (name.Contains("spigot", StringComparison.Ordinal)) return "Spigot";
        if (name.Contains("craftbukkit", StringComparison.Ordinal)
            || name.Contains("bukkit", StringComparison.Ordinal)) return "Bukkit";

        var hasMods = Directory.Exists(Path.Combine(folderPath, "mods"));
        var hasPlugins = Directory.Exists(Path.Combine(folderPath, "plugins"));
        if (inspection is null && hasMods && hasPlugins) return "Hybrid";
        if (hasMods) return "Forge";
        if (hasPlugins) return "Plugin server";
        if (name.StartsWith("minecraft_server", StringComparison.Ordinal)
            || string.Equals(fileName, "server.jar", StringComparison.OrdinalIgnoreCase)) return "Vanilla";
        return "Minecraft";
    }

    private static string DetectVersionFromFolderJars(string folderPath)
    {
        foreach (var jarPath in Directory.EnumerateFiles(folderPath, "*.jar", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var version = DetectMinecraftVersion(Path.GetFileName(jarPath));
            if (version != "Unknown")
            {
                return version;
            }
        }

        return "Unknown";
    }

    private static string DetectVersionFromLogs(string folderPath)
    {
        var logCandidates = new[]
        {
            Path.Combine(folderPath, "logs", "latest.log"),
            Path.Combine(folderPath, "server.log"),
            Path.Combine(folderPath, "ForgeModLoader-server-0.log"),
            Path.Combine(folderPath, "logs", "fml-server-latest.log")
        };

        foreach (var logPath in logCandidates.Where(File.Exists))
        {
            try
            {
                using var stream = new FileStream(
                    logPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[MaximumLogLength];
                var charactersRead = reader.ReadBlock(buffer, 0, buffer.Length);
                var match = LogVersionPattern().Match(new string(buffer, 0, charactersRead));
                if (match.Success)
                {
                    return match.Groups["version"].Value;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // A locked or malformed historic log should not prevent importing the server.
            }
        }

        return "Unknown";
    }

    private static string DetectMinecraftVersion(string fileName)
    {
        var match = MinecraftVersionPattern().Match(fileName ?? string.Empty);
        return match.Success ? match.Groups["version"].Value : "Unknown";
    }

    [GeneratedRegex(@"(?<!\d)(?<version>1\.\d{1,2}(?:\.\d{1,2})?)(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex MinecraftVersionPattern();

    [GeneratedRegex(@"Starting (?:minecraft )?server version\s+(?<version>1\.\d{1,2}(?:\.\d{1,2})?)", RegexOptions.IgnoreCase)]
    private static partial Regex LogVersionPattern();
}
