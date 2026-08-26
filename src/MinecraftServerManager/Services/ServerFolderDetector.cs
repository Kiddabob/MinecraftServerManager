using System.Text;
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
    int Score = 0)
{
    public IReadOnlyList<string> EffectiveJavaArguments => JavaArguments ?? [];

    public IReadOnlyList<string> EffectiveServerArguments => ServerArguments ?? [];

    public IReadOnlyList<string> EffectiveDirectLaunchArguments => DirectLaunchArguments ?? [];

    public string DisplayName
    {
        get
        {
            var launcher = string.IsNullOrWhiteSpace(LaunchScript)
                ? ServerJar
                : $"{LaunchScript} → {(string.IsNullOrWhiteSpace(ServerJar) ? "argument file launch" : ServerJar)}";
            return $"{launcher}  •  {ServerType}  •  Minecraft {MinecraftVersion}";
        }
    }
}

public static partial class ServerFolderDetector
{
    private const int MaximumScriptLength = 256 * 1024;
    private const int MaximumLogLength = 2 * 1024 * 1024;

    public static ServerFolderDetection? Detect(string folderPath) =>
        DetectCandidates(folderPath).FirstOrDefault();

    public static IReadOnlyList<ServerFolderDetection> DetectCandidates(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Directory.Exists(folderPath))
        {
            return [];
        }

        var candidates = new List<ServerFolderDetection>();
        foreach (var scriptPath in EnumerateLaunchScripts(folderPath))
        {
            candidates.AddRange(DetectScriptCandidates(folderPath, scriptPath));
        }

        foreach (var jarPath in EnumerateServerJars(folderPath))
        {
            var jarName = Path.GetFileName(jarPath);
            candidates.Add(CreateDetection(
                folderPath,
                jarName,
                launchScript: string.Empty,
                javaExecutable: "java",
                javaArguments: ["-Xms1G", "-Xmx2G"],
                serverArguments: ["nogui"],
                directLaunchArguments: [],
                score: LauncherScore(jarName)));
        }

        return candidates
            .GroupBy(
                candidate => $"{candidate.LaunchScript}\0{candidate.ServerJar}\0{CommandLineArgumentParser.Join(candidate.EffectiveDirectLaunchArguments)}",
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

    private static IEnumerable<ServerFolderDetection> DetectScriptCandidates(
        string folderPath,
        string scriptPath)
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
        var lineNumber = 0;
        foreach (var rawLine in scriptText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("::", StringComparison.Ordinal)
                || line.StartsWith('#'))
            {
                continue;
            }

            var tokens = CommandLineArgumentParser.Split(line).ToList();
            var javaIndex = tokens.FindIndex(IsJavaExecutableToken);
            if (javaIndex < 0)
            {
                continue;
            }

            var executable = NormalizeJavaExecutable(tokens[javaIndex]);
            var launchTokens = tokens.Skip(javaIndex + 1)
                .TakeWhile(token => token is not "&" and not "&&" and not "||")
                .Where(token => token is not "%*" and not "$@")
                .ToArray();
            var jarIndex = Array.FindIndex(
                launchTokens,
                token => token.Equals("-jar", StringComparison.OrdinalIgnoreCase));

            string serverJar;
            IReadOnlyList<string> javaArguments;
            IReadOnlyList<string> serverArguments;
            IReadOnlyList<string> directLaunchArguments;
            if (jarIndex >= 0 && jarIndex + 1 < launchTokens.Length)
            {
                serverJar = launchTokens[jarIndex + 1].Trim('"', '\'');
                if (IsNonServerJar(Path.GetFileNameWithoutExtension(serverJar))
                    || launchTokens.Any(token => token.Contains("installServer", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                javaArguments = launchTokens.Take(jarIndex).ToArray();
                serverArguments = launchTokens.Skip(jarIndex + 2).ToArray();
                directLaunchArguments = [];
            }
            else
            {
                var directArguments = launchTokens
                    .Where(token => token.StartsWith('@') || token.StartsWith('-'))
                    .ToArray();
                if (!directArguments.Any(token => token.StartsWith('@')))
                {
                    continue;
                }

                serverJar = string.Empty;
                javaArguments = [];
                serverArguments = [];
                directLaunchArguments = directArguments;
            }

            var score = 1000
                + (10 - Math.Min(lineNumber, 10))
                + (ScriptPriorityScore(scriptName) * 10)
                + (string.IsNullOrWhiteSpace(serverJar) || File.Exists(Path.Combine(folderPath, serverJar)) ? 60 : 0);
            yield return CreateDetection(
                folderPath,
                serverJar,
                scriptName,
                executable,
                javaArguments,
                serverArguments,
                directLaunchArguments,
                score);
        }
    }

    private static ServerFolderDetection CreateDetection(
        string folderPath,
        string serverJar,
        string launchScript,
        string javaExecutable,
        IReadOnlyList<string> javaArguments,
        IReadOnlyList<string> serverArguments,
        IReadOnlyList<string> directLaunchArguments,
        int score)
    {
        var minecraftVersion = DetectMinecraftVersion(
            $"{serverJar} {string.Join(' ', directLaunchArguments)}");
        if (minecraftVersion == "Unknown")
        {
            minecraftVersion = DetectVersionFromFolderJars(folderPath);
        }

        if (minecraftVersion == "Unknown")
        {
            minecraftVersion = DetectVersionFromLogs(folderPath);
        }

        return new ServerFolderDetection(
            serverJar,
            DetectServerType(folderPath, serverJar, directLaunchArguments),
            minecraftVersion,
            launchScript,
            javaExecutable,
            javaArguments,
            serverArguments,
            directLaunchArguments,
            score);
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

    private static bool IsJavaExecutableToken(string token)
    {
        var normalized = token.Trim('"', '\'').Replace('/', '\\');
        var fileName = Path.GetFileName(normalized);
        return fileName.Equals("java", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("java.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("javaw", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeJavaExecutable(string token)
    {
        var executable = Environment.ExpandEnvironmentVariables(token.Trim('"', '\''));
        return executable.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase)
            ? executable[..^"javaw.exe".Length] + "java.exe"
            : executable.EndsWith("javaw", StringComparison.OrdinalIgnoreCase)
                ? executable[..^"javaw".Length] + "java"
                : executable;
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
        IReadOnlyList<string> directLaunchArguments)
    {
        var name = $"{fileName} {string.Join(' ', directLaunchArguments)}".ToLowerInvariant();
        var hasMods = Directory.Exists(Path.Combine(folderPath, "mods"));
        var hasPlugins = Directory.Exists(Path.Combine(folderPath, "plugins"));
        if (hasMods && hasPlugins) return "Hybrid";
        if (name.Contains("neoforge", StringComparison.Ordinal)) return "NeoForge";
        if (name.Contains("forge", StringComparison.Ordinal)) return "Forge";
        if (name.Contains("fabric", StringComparison.Ordinal)) return "Fabric";
        if (name.Contains("quilt", StringComparison.Ordinal)) return "Quilt";
        if (name.Contains("paper", StringComparison.Ordinal)) return "Paper";
        if (name.Contains("purpur", StringComparison.Ordinal)) return "Purpur";
        if (name.Contains("spigot", StringComparison.Ordinal)) return "Spigot";
        if (name.Contains("craftbukkit", StringComparison.Ordinal)
            || name.Contains("bukkit", StringComparison.Ordinal)) return "Bukkit";
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
