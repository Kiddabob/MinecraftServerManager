using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed partial class JavaRuntimeService : IJavaRuntimeService
{
    private readonly ConcurrentDictionary<string, JavaRuntimeInfo> _knownRuntimes =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<JavaRuntimeInfo>> DiscoverAsync(
        IEnumerable<string> configuredExecutables,
        CancellationToken cancellationToken = default)
    {
        var candidates = EnumerateCandidatePaths(configuredExecutables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_knownRuntimes.ContainsKey(candidate))
            {
                continue;
            }

            var runtime = await ProbeAsync(candidate, GetSource(candidate), cancellationToken);
            if (runtime is not null)
            {
                _knownRuntimes[candidate] = runtime;
            }
        }

        return _knownRuntimes.Values
            .OrderBy(runtime => runtime.MajorVersion)
            .ThenBy(runtime => IsJavaPathShim(runtime.ExecutablePath))
            .ThenBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string ResolveExecutablePath(string configuredExecutable, string versionText)
    {
        if (string.IsNullOrWhiteSpace(configuredExecutable))
        {
            return configuredExecutable;
        }

        var expanded = Environment.ExpandEnvironmentVariables(configuredExecutable.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(expanded) && File.Exists(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        var desiredMajor = ParseMajorVersion(versionText);
        var pathResolved = ResolveFromPath(expanded);
        if (pathResolved is not null
            && _knownRuntimes.TryGetValue(pathResolved, out var pathRuntime)
            && (desiredMajor is null || pathRuntime.MajorVersion == desiredMajor))
        {
            return pathResolved;
        }

        var known = _knownRuntimes.Values
            .Where(runtime => desiredMajor is null || runtime.MajorVersion == desiredMajor)
            .OrderBy(runtime => IsJavaPathShim(runtime.ExecutablePath))
            .FirstOrDefault();
        if (known is not null)
        {
            return known.ExecutablePath;
        }

        return pathResolved ?? configuredExecutable;
    }

    public JavaRuntimeInfo? FindKnownRuntime(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(executablePath.Trim().Trim('"'));
        var resolved = Path.IsPathFullyQualified(expanded)
            ? expanded
            : ResolveFromPath(expanded) ?? expanded;
        return _knownRuntimes.TryGetValue(resolved, out var runtime)
            ? runtime
            : null;
    }

    public int? GetRecommendedJavaMajor(string minecraftVersion)
    {
        if (!Version.TryParse(NormalizeMinecraftVersion(minecraftVersion), out var version))
        {
            return null;
        }

        if (version >= new Version(1, 20, 5)) return 21;
        if (version >= new Version(1, 18)) return 17;
        if (version >= new Version(1, 17)) return 16;
        return 8;
    }

    public string GetCompatibilityMessage(string minecraftVersion, JavaRuntimeInfo? runtime)
    {
        var recommended = GetRecommendedJavaMajor(minecraftVersion);
        if (recommended is null)
        {
            return runtime is null
                ? "Minecraft and Java versions could not be verified. Select the runtime used by this server's original launcher."
                : $"Minecraft version is unknown. Java {runtime.MajorVersion} is selected; verify it against the server's original launcher.";
        }

        if (runtime is null)
        {
            return $"Minecraft {minecraftVersion} normally uses Java {recommended}. Select a detected runtime before starting.";
        }

        if (recommended == 8 && runtime.MajorVersion > 8)
        {
            return $"Compatibility warning: this legacy Minecraft version normally uses Java 8, but Java {runtime.MajorVersion} is selected.";
        }

        if (runtime.MajorVersion < recommended)
        {
            return $"Compatibility warning: Minecraft {minecraftVersion} requires Java {recommended} or newer, but Java {runtime.MajorVersion} is selected.";
        }

        return recommended == 8
            ? "Java 8 matches the normal runtime baseline for this legacy Minecraft version."
            : $"Java {runtime.MajorVersion} meets the normal Java {recommended}-or-newer baseline for Minecraft {minecraftVersion}.";
    }

    private static IEnumerable<string> EnumerateCandidatePaths(IEnumerable<string> configuredExecutables)
    {
        foreach (var configured in configuredExecutables.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (Path.IsPathFullyQualified(expanded) && File.Exists(expanded))
            {
                yield return Path.GetFullPath(expanded);
            }
            else
            {
                var resolved = ResolveFromPath(expanded);
                if (resolved is not null)
                {
                    yield return resolved;
                }
            }
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var javaHomeExecutable = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(javaHomeExecutable))
            {
                yield return Path.GetFullPath(javaHomeExecutable);
            }
        }

        foreach (var programFiles in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var vendorFolder in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto" })
            {
                var vendorRoot = Path.Combine(programFiles, vendorFolder);
                foreach (var executable in EnumerateVendorJavaExecutables(vendorRoot))
                {
                    yield return executable;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateVendorJavaExecutables(string vendorRoot)
    {
        if (!Directory.Exists(vendorRoot))
        {
            yield break;
        }

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(vendorRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            yield break;
        }

        foreach (var directory in directories.Prepend(vendorRoot))
        {
            foreach (var relativePath in new[] { @"bin\java.exe", @"jre\bin\java.exe" })
            {
                var candidate = Path.Combine(directory, relativePath);
                if (File.Exists(candidate))
                {
                    yield return Path.GetFullPath(candidate);
                }
            }
        }
    }

    private static async Task<JavaRuntimeInfo?> ProbeAsync(
        string executablePath,
        string source,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-version");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            var versionOutput = $"{await standardError}\n{await standardOutput}".Trim();
            var version = VersionPattern().Match(versionOutput).Groups["version"].Value;
            var major = ParseMajorVersion(version);
            return major is null
                ? null
                : new JavaRuntimeInfo(Path.GetFullPath(executablePath), version, major.Value, source);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A failed version probe is ignored.
        }
    }

    private static string? ResolveFromPath(string executable)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var fileNames = Path.HasExtension(executable)
            ? [executable]
            : new[] { executable + ".exe", executable };
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var fileName in fileNames)
            {
                try
                {
                    var candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    private static int? ParseMajorVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }

        var numbers = NumberPattern().Matches(versionText)
            .Select(match => int.TryParse(match.Value, out var value) ? value : -1)
            .Where(value => value >= 0)
            .ToArray();
        if (numbers.Length == 0)
        {
            return null;
        }

        return numbers[0] == 1 && numbers.Length > 1 ? numbers[1] : numbers[0];
    }

    private static string NormalizeMinecraftVersion(string value)
    {
        var match = MinecraftVersionPattern().Match(value ?? string.Empty);
        return match.Success ? match.Groups["version"].Value : string.Empty;
    }

    private static bool IsJavaPathShim(string path) =>
        path.Contains(@"Common Files\Oracle\Java\javapath", StringComparison.OrdinalIgnoreCase);

    private static string GetSource(string path) => IsJavaPathShim(path)
        ? "Windows Java PATH shim"
        : path.Contains(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
            ? "Installed runtime"
            : "Configured profile";

    [GeneratedRegex("(?:java|openjdk) version \\\"(?<version>[^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"(?<!\d)(?<version>1\.\d{1,2}(?:\.\d{1,2})?)(?!\d)")]
    private static partial Regex MinecraftVersionPattern();
}
