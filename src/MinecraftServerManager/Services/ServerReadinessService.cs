using System.Text;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ServerReadinessService : IServerReadinessService
{
    private const long MaximumEulaFileBytes = 64 * 1024;

    private readonly IProfileValidator _profileValidator;

    public ServerReadinessService(IProfileValidator profileValidator)
    {
        _profileValidator = profileValidator;
    }

    public ServerReadinessReport Evaluate(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var validation = _profileValidator.Validate(profile);
        var eula = InspectEula(profile.ServerDirectory);
        var canStart = validation.IsValid && eula.State == ServerEulaState.Accepted;

        var state = !validation.IsValid
                || eula.State != ServerEulaState.Accepted
            ? ServerReadinessState.ActionRequired
            : validation.Warnings.Count > 0
                ? ServerReadinessState.ReadyWithNotice
                : ServerReadinessState.Ready;

        var summary = state switch
        {
            ServerReadinessState.ActionRequired when !validation.IsValid =>
                "Fix the launch validation items below before starting this server.",
            ServerReadinessState.ActionRequired when eula.State == ServerEulaState.Missing =>
                "Review and accept the Minecraft EULA before starting. The manager will create eula.txt only after you confirm.",
            ServerReadinessState.ActionRequired when eula.State == ServerEulaState.NotAccepted =>
                "Review and accept the Minecraft EULA before starting this server.",
            ServerReadinessState.ActionRequired =>
                "The server EULA status could not be verified. Use the review action to confirm and repair eula.txt.",
            ServerReadinessState.ReadyWithNotice =>
                "The launcher can start. Review the validation notes below if this server behaves unexpectedly.",
            _ => "Launcher, Java, memory, and EULA checks are ready."
        };

        return new ServerReadinessReport(
            state,
            summary,
            DescribeLauncher(profile),
            DescribeJava(profile),
            DescribeMemory(profile),
            eula.State,
            eula.Text,
            eula.Path,
            canStart,
            validation.ToDisplayText());
    }

    public async Task<ServerReadinessReport> AcceptEulaAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.ServerDirectory))
        {
            throw new InvalidOperationException("The server folder is not configured.");
        }

        var serverDirectory = Path.GetFullPath(profile.ServerDirectory);
        if (!Directory.Exists(serverDirectory))
        {
            throw new DirectoryNotFoundException($"The server folder does not exist: {serverDirectory}");
        }

        var eulaPath = Path.Combine(serverDirectory, "eula.txt");
        string existingText;
        if (File.Exists(eulaPath))
        {
            var fileInfo = new FileInfo(eulaPath);
            if (fileInfo.LinkTarget is not null)
            {
                throw new InvalidOperationException("eula.txt is a symbolic link and was not changed.");
            }

            if (fileInfo.Length > MaximumEulaFileBytes)
            {
                throw new InvalidDataException("eula.txt is unexpectedly large and was not changed.");
            }

            existingText = await File.ReadAllTextAsync(eulaPath, cancellationToken);
        }
        else
        {
            existingText = string.Empty;
        }

        var acceptedText = SetEulaAccepted(existingText);
        var temporaryPath = Path.Combine(
            serverDirectory,
            $".eula.{Guid.NewGuid():N}.minecraft-server-manager.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                acceptedText,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, eulaPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup only; the requested EULA update has already completed or failed.
            }
        }

        return Evaluate(profile);
    }

    internal static string SetEulaAccepted(string existingText)
    {
        if (string.IsNullOrEmpty(existingText))
        {
            return "# Accepted in Minecraft Server Manager after explicit user confirmation.\r\n"
                + "# https://www.minecraft.net/en-us/eula\r\n"
                + "eula=true\r\n";
        }

        var newLine = existingText.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
        var hasTrailingNewLine = existingText.EndsWith('\n') || existingText.EndsWith('\r');
        var lines = existingText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
        if (hasTrailingNewLine && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var foundEula = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator < 0
                || !trimmed[..separator].Trim().Equals("eula", StringComparison.Ordinal))
            {
                continue;
            }

            lines[index] = "eula=true";
            foundEula = true;
        }

        if (!foundEula)
        {
            lines.Add("eula=true");
        }

        return string.Join(newLine, lines) + (hasTrailingNewLine ? newLine : string.Empty);
    }

    private static string DescribeLauncher(ServerProfile profile)
    {
        if (profile.DirectLaunchArguments.Count > 0)
        {
            return $"Direct Java launch • {string.Join(' ', profile.DirectLaunchArguments)}";
        }

        return string.IsNullOrWhiteSpace(profile.ServerJar)
            ? "No server launcher is selected."
            : $"JAR • {profile.ServerJar}";
    }

    private static string DescribeJava(ServerProfile profile)
    {
        var version = string.IsNullOrWhiteSpace(profile.JavaVersion)
            ? "Java version not identified"
            : profile.JavaVersion;
        var executable = string.IsNullOrWhiteSpace(profile.JavaExecutable)
            ? "executable not selected"
            : profile.JavaExecutable;
        return $"{version} • {executable}";
    }

    private static string DescribeMemory(ServerProfile profile)
    {
        var initial = JavaArgumentUtilities.GetInitialMemoryMegabytes(profile.JavaArguments);
        var maximum = JavaArgumentUtilities.GetMaximumMemoryMegabytes(profile.JavaArguments);
        return (initial, maximum) switch
        {
            ({ } initialMb, { } maximumMb) =>
                $"{initialMb:N0} MB initial • {maximumMb:N0} MB maximum",
            ({ } initialMb, null) => $"{initialMb:N0} MB initial • JVM maximum default",
            (null, { } maximumMb) => $"JVM initial default • {maximumMb:N0} MB maximum",
            _ => "JVM defaults • no memory limits configured"
        };
    }

    private static EulaInspection InspectEula(string serverDirectory)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory))
        {
            return new EulaInspection(
                ServerEulaState.InvalidOrUnreadable,
                "The server folder is not configured, so eula.txt cannot be checked.",
                string.Empty);
        }

        string eulaPath;
        try
        {
            eulaPath = Path.Combine(Path.GetFullPath(serverDirectory), "eula.txt");
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new EulaInspection(
                ServerEulaState.InvalidOrUnreadable,
                $"The EULA path is invalid: {exception.Message}",
                string.Empty);
        }

        if (!File.Exists(eulaPath))
        {
            return new EulaInspection(
                ServerEulaState.Missing,
                "Not accepted yet. The manager will create eula.txt only after explicit confirmation.",
                eulaPath);
        }

        try
        {
            if (new FileInfo(eulaPath).Length > MaximumEulaFileBytes)
            {
                return new EulaInspection(
                    ServerEulaState.InvalidOrUnreadable,
                    "eula.txt is unexpectedly large and was not read.",
                    eulaPath);
            }

            bool? accepted = null;
            foreach (var line in File.ReadLines(eulaPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator < 0
                    || !trimmed[..separator].Trim().Equals("eula", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!bool.TryParse(trimmed[(separator + 1)..].Trim(), out var parsedValue))
                {
                    return new EulaInspection(
                        ServerEulaState.InvalidOrUnreadable,
                        "eula.txt contains an invalid eula value.",
                        eulaPath);
                }

                accepted = parsedValue;
            }

            if (accepted is { } acceptedValue)
            {
                return acceptedValue
                    ? new EulaInspection(
                        ServerEulaState.Accepted,
                        "Accepted in eula.txt.",
                        eulaPath)
                    : new EulaInspection(
                        ServerEulaState.NotAccepted,
                        "Not accepted. Use Review and accept EULA to confirm in the app.",
                        eulaPath);
            }

            return new EulaInspection(
                ServerEulaState.InvalidOrUnreadable,
                "eula.txt does not contain an eula setting.",
                eulaPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new EulaInspection(
                ServerEulaState.InvalidOrUnreadable,
                $"eula.txt could not be read: {exception.Message}",
                eulaPath);
        }
    }

    private sealed record EulaInspection(ServerEulaState State, string Text, string Path);
}
