using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ProfileValidator : IProfileValidator
{
    public ProfileValidationResult Validate(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<string>();

        RequireValue(profile.Id, "Profile ID", errors);
        RequireValue(profile.DisplayName, "Display name", errors);
        RequireValue(profile.ServerDirectory, "Server directory", errors);
        RequireValue(profile.JavaExecutable, "Java executable", errors);
        RequireValue(profile.ServerJar, "Server JAR", errors);

        if (string.IsNullOrWhiteSpace(profile.ServerDirectory))
        {
            return new ProfileValidationResult(errors);
        }

        string serverDirectory;
        try
        {
            serverDirectory = Path.GetFullPath(profile.ServerDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"Server directory is not a valid path: {exception.Message}");
            return new ProfileValidationResult(errors);
        }

        if (!Directory.Exists(serverDirectory))
        {
            errors.Add($"Server directory does not exist: {serverDirectory}");
        }

        ValidateJavaExecutable(profile.JavaExecutable, errors);

        if (!string.IsNullOrWhiteSpace(profile.ServerJar))
        {
            ValidateRequiredFile(serverDirectory, profile.ServerJar, "Server JAR", errors);
        }

        foreach (var requiredFile in profile.RequiredFiles.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            ValidateRequiredFile(serverDirectory, requiredFile, "Required file", errors);
        }

        foreach (var requiredDirectory in profile.RequiredDirectories.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            ValidateRequiredDirectory(serverDirectory, requiredDirectory, errors);
        }

        if (string.IsNullOrWhiteSpace(profile.StopCommand))
        {
            errors.Add("Stop command is required for safe shutdown.");
        }

        if (profile.StopTimeoutSeconds <= 0)
        {
            errors.Add("Stop timeout must be greater than zero seconds.");
        }

        return new ProfileValidationResult(errors);
    }

    private static void RequireValue(string value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is required.");
        }
    }

    private static void ValidateJavaExecutable(string executable, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        if (Path.IsPathFullyQualified(executable))
        {
            if (!File.Exists(executable))
            {
                errors.Add($"Java executable does not exist: {executable}");
            }

            return;
        }

        if (!CanResolveFromPath(executable))
        {
            errors.Add($"Java executable could not be resolved from PATH: {executable}");
        }
    }

    private static bool CanResolveFromPath(string executable)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        var extensions = Path.HasExtension(executable)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim(), executable + extension)))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Ignore malformed PATH entries and continue searching.
                }
            }
        }

        return false;
    }

    private static void ValidateRequiredFile(
        string serverDirectory,
        string configuredPath,
        string label,
        ICollection<string> errors)
    {
        if (!TryResolveServerPath(serverDirectory, configuredPath, out var resolvedPath, out var error))
        {
            errors.Add($"{label} path is invalid: {error}");
            return;
        }

        if (!File.Exists(resolvedPath))
        {
            errors.Add($"{label} is missing: {resolvedPath}");
        }
    }

    private static void ValidateRequiredDirectory(
        string serverDirectory,
        string configuredPath,
        ICollection<string> errors)
    {
        if (!TryResolveServerPath(serverDirectory, configuredPath, out var resolvedPath, out var error))
        {
            errors.Add($"Required directory path is invalid: {error}");
            return;
        }

        if (!Directory.Exists(resolvedPath))
        {
            errors.Add($"Required directory is missing: {resolvedPath}");
        }
    }

    private static bool TryResolveServerPath(
        string serverDirectory,
        string configuredPath,
        out string resolvedPath,
        out string error)
    {
        try
        {
            resolvedPath = Path.IsPathFullyQualified(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(Path.Combine(serverDirectory, configuredPath));
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            resolvedPath = string.Empty;
            error = exception.Message;
            return false;
        }
    }
}
