using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ProfileValidator : IProfileValidator
{
    private readonly IJavaRuntimeService _javaRuntimeService;

    public ProfileValidator(IJavaRuntimeService javaRuntimeService)
    {
        _javaRuntimeService = javaRuntimeService;
    }

    public ProfileValidationResult Validate(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<string>();
        var warnings = new List<string>();

        RequireValue(profile.Id, "Profile ID", errors);
        RequireValue(profile.DisplayName, "Display name", errors);
        RequireValue(profile.ServerDirectory, "Server directory", errors);
        RequireValue(profile.JavaExecutable, "Java executable", errors);
        if (profile.DirectLaunchArguments.Count == 0)
        {
            RequireValue(profile.ServerJar, "Server JAR", errors);
        }

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

        ValidateJavaExecutable(profile, errors);

        if (!string.IsNullOrWhiteSpace(profile.ServerJar))
        {
            ValidateRequiredFile(serverDirectory, profile.ServerJar, "Server JAR", errors);
        }

        foreach (var argumentFile in profile.DirectLaunchArguments
                     .Where(argument => argument.StartsWith('@'))
                     .Select(argument => argument[1..].Trim('"'))
                     .Where(argument => !string.IsNullOrWhiteSpace(argument)))
        {
            ValidateRequiredFile(serverDirectory, argumentFile, "Java argument file", errors);
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

        var runtime = _javaRuntimeService.FindKnownRuntime(profile.JavaExecutable);
        var compatibility = _javaRuntimeService.GetCompatibilityMessage(
            profile.MinecraftVersion,
            runtime);
        var minecraftRecommendedJava = _javaRuntimeService.GetRecommendedJavaMajor(profile.MinecraftVersion);
        var recommendedJava = minecraftRecommendedJava;
        recommendedJava ??= ParseJavaMajor(profile.JavaVersion);
        if (runtime is not null
            && recommendedJava is not null
            && runtime.MajorVersion < recommendedJava)
        {
            errors.Add(minecraftRecommendedJava is null
                ? $"The selected server JAR requires Java {recommendedJava} or newer, but Java {runtime.MajorVersion} is selected."
                : compatibility);
        }
        else if (compatibility.StartsWith("Compatibility warning:", StringComparison.Ordinal)
            || runtime is null)
        {
            warnings.Add(compatibility);
        }

        return new ProfileValidationResult(errors, warnings);
    }

    private static int? ParseJavaMajor(string value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var majorVersion) ? majorVersion : null;
    }

    private static void RequireValue(string value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is required.");
        }
    }

    private void ValidateJavaExecutable(ServerProfile profile, ICollection<string> errors)
    {
        var executable = profile.JavaExecutable;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var resolvedExecutable = _javaRuntimeService.ResolveExecutablePath(
            executable,
            profile.JavaVersion);
        if (Path.IsPathFullyQualified(resolvedExecutable))
        {
            if (!File.Exists(resolvedExecutable))
            {
                errors.Add($"Java executable does not exist: {resolvedExecutable}");
            }

            return;
        }

        if (!CanResolveFromPath(resolvedExecutable))
        {
            errors.Add($"Java executable could not be resolved from PATH: {resolvedExecutable}");
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
            var root = Path.GetFullPath(serverDirectory);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!resolvedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "the path must remain inside the server folder";
                resolvedPath = string.Empty;
                return false;
            }

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
