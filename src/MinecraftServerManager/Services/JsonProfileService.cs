using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class JsonProfileService : IProfileService
{
    private const string ProfilesDirectoryName = "Profiles";
    private const string UserProfilesDirectoryName = "UserProfiles";
    private const int CurrentProfileFormatVersion = 5;
    private readonly IJavaRuntimeService _javaRuntimeService;
    private readonly IServerLaunchRecommendationService _launchRecommendationService;

    private static readonly IReadOnlyList<string> DefaultFailurePatterns =
    [
        @"LoaderException",
        @"UnsupportedClassVersionError",
        @"Could not reserve enough space for object heap",
        @"Unable to access jarfile",
        @"A problem occurred running the Server launcher",
        @"Failed to start the minecraft server"
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    private static string PackagedProfilesDirectory =>
        Path.Combine(AppContext.BaseDirectory, ProfilesDirectoryName);

    private static string UserProfilesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kidda.MinecraftServerManager",
        UserProfilesDirectoryName);

    public JsonProfileService(
        IJavaRuntimeService javaRuntimeService,
        IServerLaunchRecommendationService launchRecommendationService)
    {
        _javaRuntimeService = javaRuntimeService;
        _launchRecommendationService = launchRecommendationService;
    }

    public async Task<ServerProfile> LoadAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var profilePath = Path.Combine(PackagedProfilesDirectory, fileName);
        if (!File.Exists(profilePath))
        {
            throw new FileNotFoundException($"Server profile was not found: {profilePath}", profilePath);
        }


        return await LoadProfileFileAsync(profilePath, cancellationToken);
    }

    public async Task<IReadOnlyList<ServerProfile>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        var profilesById = new Dictionary<string, ServerProfile>(StringComparer.OrdinalIgnoreCase);
        var packagedProfiles = new List<ServerProfile>();

        foreach (var profilePath in EnumerateProfileFiles(PackagedProfilesDirectory))
        {
            var profile = await LoadProfileFileAsync(profilePath, cancellationToken);
            packagedProfiles.Add(profile);
            profilesById[profile.Id] = profile;
        }

        foreach (var profilePath in EnumerateProfileFiles(UserProfilesDirectory))
        {
            var profile = await LoadProfileFileAsync(profilePath, cancellationToken);
            InheritMissingProfileSettings(profile, packagedProfiles);
            if (profile.ProfileFormatVersion < CurrentProfileFormatVersion)
            {
                MigrateProfile(profile, packagedProfiles.FirstOrDefault());
                await SaveProfileFileAsync(profile, cancellationToken);
            }
            profilesById[profile.Id] = profile;
        }

        return profilesById.Values
            .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<ProfileImportResult> ImportFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var normalizedFolder = Path.GetFullPath(folderPath);
        if (!Directory.Exists(normalizedFolder))
        {
            return new ProfileImportResult(null, false, $"The selected folder does not exist: {normalizedFolder}");
        }

        var existingProfiles = await LoadAllAsync(cancellationToken);
        var existing = existingProfiles.FirstOrDefault(profile =>
            PathsEqual(profile.ServerDirectory, normalizedFolder));
        if (existing is not null)
        {
            return new ProfileImportResult(existing, false, $"Opened existing profile '{existing.DisplayName}'.");
        }

        var templates = new List<ServerProfile>();
        foreach (var templatePath in EnumerateProfileFiles(PackagedProfilesDirectory))
        {
            templates.Add(await LoadProfileFileAsync(templatePath, cancellationToken));
        }

        var template = templates
            .Where(candidate => File.Exists(Path.Combine(normalizedFolder, candidate.ServerJar)))
            .OrderByDescending(candidate => DetectionScore(normalizedFolder, candidate))
            .FirstOrDefault();

        var folderName = new DirectoryInfo(normalizedFolder).Name;
        ServerProfile importedProfile;
        string message;
        if (template is not null)
        {
            importedProfile = CloneForFolder(template, normalizedFolder, folderName);
            message = $"Created a {template.DisplayName} profile for '{folderName}'.";
        }
        else
        {
            var detection = ServerFolderDetector.Detect(normalizedFolder);
            if (detection is null)
            {
                return new ProfileImportResult(
                    null,
                    false,
                    "No supported Java launch command or top-level server JAR was found in this folder.");
            }

            importedProfile = CreateGenericProfile(
                normalizedFolder,
                folderName,
                detection,
                templates.FirstOrDefault());
            var launcher = string.IsNullOrWhiteSpace(detection.ServerJar)
                ? "the detected Forge argument files"
                : detection.ServerJar;
            message = $"Created a {detection.ServerType} profile for '{folderName}' using {launcher}.";
        }

        await SaveProfileFileAsync(importedProfile, cancellationToken);

        return new ProfileImportResult(
            importedProfile,
            true,
            message);
    }

    public Task SaveAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.ProfileFormatVersion = CurrentProfileFormatVersion;
        return SaveProfileFileAsync(profile, cancellationToken);
    }

    private static async Task<ServerProfile> LoadProfileFileAsync(
        string profilePath,
        CancellationToken cancellationToken)
    {

        await using var stream = File.OpenRead(profilePath);
        var profile = await JsonSerializer.DeserializeAsync<ServerProfile>(
            stream,
            SerializerOptions,
            cancellationToken);

        if (profile is null)
        {
            throw new InvalidDataException($"Server profile is empty: {profilePath}");
        }

        profile.ServerDirectory = Environment.ExpandEnvironmentVariables(profile.ServerDirectory);
        profile.JavaExecutable = Environment.ExpandEnvironmentVariables(profile.JavaExecutable);
        profile.IconPath = FindProfileIcon(profile.ServerDirectory);
        return profile;
    }

    private static IEnumerable<string> EnumerateProfileFiles(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            : [];
    }

    private static int DetectionScore(string folder, ServerProfile template)
    {
        return template.RequiredFiles.Count(file => File.Exists(Path.Combine(folder, file)))
            + template.RequiredDirectories.Count(directory => Directory.Exists(Path.Combine(folder, directory)));
    }

    private static ServerProfile CloneForFolder(
        ServerProfile template,
        string folderPath,
        string folderName)
    {
        var pathHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(folderPath.ToUpperInvariant())))[..10]
            .ToLowerInvariant();

        return new ServerProfile
        {
            ProfileFormatVersion = CurrentProfileFormatVersion,
            Id = $"{template.Id}-{pathHash}",
            DisplayName = string.Equals(folderName, template.DisplayName, StringComparison.OrdinalIgnoreCase)
                ? template.DisplayName
                : $"{template.DisplayName} — {folderName}",
            ServerType = template.ServerType,
            MinecraftVersion = template.MinecraftVersion,
            ForgeVersion = template.ForgeVersion,
            JavaVersion = template.JavaVersion,
            ServerDirectory = folderPath,
            IconPath = FindProfileIcon(folderPath),
            JavaExecutable = template.JavaExecutable,
            ServerJar = template.ServerJar,
            LaunchScript = template.LaunchScript,
            JavaArguments = template.JavaArguments,
            ServerArguments = template.ServerArguments,
            DirectLaunchArguments = template.DirectLaunchArguments,
            RequiredFiles = template.RequiredFiles,
            RequiredDirectories = template.RequiredDirectories,
            ConfigurationSources = template.ConfigurationSources,
            ConfigurationSchemas = template.ConfigurationSchemas,
            MapPointsOfInterest = template.MapPointsOfInterest,
            ReadyPatterns = template.ReadyPatterns,
            FailurePatterns = template.FailurePatterns.Count == 0
                ? DefaultFailurePatterns
                : template.FailurePatterns,
            PlayerJoinPatterns = template.PlayerJoinPatterns,
            PlayerLeavePatterns = template.PlayerLeavePatterns,
            ListPlayersCommand = template.ListPlayersCommand,
            BroadcastCommandPrefix = template.BroadcastCommandPrefix,
            SaveCommand = template.SaveCommand,
            StopCommand = template.StopCommand,
            StopTimeoutSeconds = template.StopTimeoutSeconds
        };
    }

    private ServerProfile CreateGenericProfile(
        string folderPath,
        string folderName,
        ServerFolderDetection detection,
        ServerProfile? sharedMinecraftDefaults)
    {
        var pathHash = CreatePathHash(folderPath);
        var javaExecutable = SelectJavaExecutable(
            folderPath,
            detection,
            sharedMinecraftDefaults);
        var recommendation = _launchRecommendationService.Recommend(
            folderPath,
            detection.ServerType,
            detection.MinecraftVersion,
            detection.RequiredJavaMajorVersion);
        var recommendedJava = recommendation.JavaMajorVersion;
        return new ServerProfile
        {
            ProfileFormatVersion = CurrentProfileFormatVersion,
            Id = $"generic-{pathHash}",
            DisplayName = folderName,
            ServerType = detection.ServerType,
            MinecraftVersion = detection.MinecraftVersion,
            ForgeVersion = string.Empty,
            JavaVersion = recommendedJava is null ? "Automatic" : $"Java {recommendedJava}",
            ServerDirectory = folderPath,
            IconPath = FindProfileIcon(folderPath),
            JavaExecutable = javaExecutable,
            ServerJar = detection.ServerJar,
            LaunchScript = detection.LaunchScript,
            JavaArguments = JavaArgumentUtilities.ReplaceMemoryArguments(
                [],
                recommendation.InitialMemoryMb,
                recommendation.MaximumMemoryMb),
            ServerArguments = detection.EffectiveServerArguments,
            DirectLaunchArguments = detection.EffectiveDirectLaunchArguments,
            RequiredFiles = string.IsNullOrWhiteSpace(detection.ServerJar) ? [] : [detection.ServerJar],
            RequiredDirectories = [],
            ConfigurationSources = sharedMinecraftDefaults?.ConfigurationSources ?? [],
            ConfigurationSchemas = [],
            ReadyPatterns = sharedMinecraftDefaults?.ReadyPatterns ?? [@"Done \([^)]+\)!"],
            FailurePatterns = DefaultFailurePatterns,
            PlayerJoinPatterns = sharedMinecraftDefaults?.PlayerJoinPatterns ?? [],
            PlayerLeavePatterns = sharedMinecraftDefaults?.PlayerLeavePatterns ?? [],
            ListPlayersCommand = sharedMinecraftDefaults?.ListPlayersCommand ?? "list",
            BroadcastCommandPrefix = sharedMinecraftDefaults?.BroadcastCommandPrefix ?? "say ",
            SaveCommand = sharedMinecraftDefaults?.SaveCommand ?? "save-all",
            StopCommand = sharedMinecraftDefaults?.StopCommand ?? "stop",
            StopTimeoutSeconds = sharedMinecraftDefaults?.StopTimeoutSeconds ?? 60
        };
    }

    private void MigrateProfile(ServerProfile profile, ServerProfile? sharedMinecraftDefaults)
    {
        if (profile.Id.StartsWith("generic-", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(profile.ServerDirectory)
            && ServerFolderDetector.Detect(profile.ServerDirectory) is { } detection)
        {
            ApplyDetection(profile, detection);
            profile.JavaExecutable = SelectJavaExecutable(
                profile.ServerDirectory,
                detection,
                sharedMinecraftDefaults);
            var recommendation = _launchRecommendationService.Recommend(
                profile.ServerDirectory,
                detection.ServerType,
                detection.MinecraftVersion,
                detection.RequiredJavaMajorVersion);
            profile.JavaArguments = JavaArgumentUtilities.ReplaceMemoryArguments(
                [],
                recommendation.InitialMemoryMb,
                recommendation.MaximumMemoryMb);
        }

        if (profile.FailurePatterns.Count == 0)
        {
            profile.FailurePatterns = DefaultFailurePatterns;
        }

        profile.ProfileFormatVersion = CurrentProfileFormatVersion;
    }

    private void ApplyDetection(ServerProfile profile, ServerFolderDetection detection)
    {
        profile.ServerJar = detection.ServerJar;
        profile.LaunchScript = detection.LaunchScript;
        profile.ServerType = detection.ServerType;
        profile.MinecraftVersion = detection.MinecraftVersion;
        profile.JavaArguments = detection.EffectiveJavaArguments.Count == 0
            && detection.EffectiveDirectLaunchArguments.Count == 0
            ? profile.JavaArguments
            : detection.EffectiveJavaArguments;
        profile.ServerArguments = detection.EffectiveServerArguments;
        profile.DirectLaunchArguments = detection.EffectiveDirectLaunchArguments;
        profile.RequiredFiles = string.IsNullOrWhiteSpace(detection.ServerJar)
            ? []
            : [detection.ServerJar];

        if (Path.IsPathFullyQualified(detection.JavaExecutable)
            && File.Exists(detection.JavaExecutable))
        {
            profile.JavaExecutable = detection.JavaExecutable;
        }

        var recommendedJava = _javaRuntimeService.GetRecommendedJavaMajor(detection.MinecraftVersion)
            ?? detection.RequiredJavaMajorVersion;
        profile.JavaVersion = recommendedJava is null ? profile.JavaVersion : $"Java {recommendedJava}";
    }

    private string SelectJavaExecutable(
        string folderPath,
        ServerFolderDetection detection,
        ServerProfile? sharedMinecraftDefaults)
    {
        if (Path.IsPathFullyQualified(detection.JavaExecutable)
            && File.Exists(detection.JavaExecutable))
        {
            return detection.JavaExecutable;
        }

        var recommendedJava = _javaRuntimeService.GetRecommendedJavaMajor(detection.MinecraftVersion)
            ?? detection.RequiredJavaMajorVersion;
        var managed = recommendedJava is null
            ? null
            : FindManagedJavaExecutable(recommendedJava.Value);
        if (managed is not null)
        {
            return managed;
        }

        if (recommendedJava == 8
            && sharedMinecraftDefaults is not null
            && File.Exists(sharedMinecraftDefaults.JavaExecutable))
        {
            return sharedMinecraftDefaults.JavaExecutable;
        }

        var discovered = FindJavaExecutable(folderPath);
        var resolved = _javaRuntimeService.ResolveExecutablePath(
            discovered,
            recommendedJava is null ? string.Empty : $"Java {recommendedJava}");
        return resolved;
    }

    private static async Task SaveProfileFileAsync(
        ServerProfile profile,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(UserProfilesDirectory);
        var profilePath = Path.Combine(UserProfilesDirectory, $"{profile.Id}.json");
        var temporaryPath = $"{profilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    profile,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, profilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string FindJavaExecutable(string folderPath)
    {
        string[] relativeCandidates =
        [
            @"runtime\bin\java.exe",
            @"jre\bin\java.exe",
            @"java\bin\java.exe",
            @"jdk\bin\java.exe"
        ];

        foreach (var relativePath in relativeCandidates)
        {
            var candidate = Path.Combine(folderPath, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var candidate = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "java";
    }

    private static string? FindManagedJavaExecutable(int majorVersion)
    {
        var runtimeDirectory = ManagedJavaRuntimeService.GetRuntimeDirectory(majorVersion);
        if (!Directory.Exists(runtimeDirectory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(runtimeDirectory, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => string.Equals(
                    new DirectoryInfo(Path.GetDirectoryName(path)!).Name,
                    "bin",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return null;
        }
    }

    private static string CreatePathHash(string folderPath)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(folderPath.ToUpperInvariant())))[..10]
            .ToLowerInvariant();
    }

    private static void InheritMissingProfileSettings(
        ServerProfile profile,
        IReadOnlyList<ServerProfile> packagedProfiles)
    {
        var template = packagedProfiles.FirstOrDefault(candidate =>
            profile.Id.StartsWith($"{candidate.Id}-", StringComparison.OrdinalIgnoreCase));
        if (template is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.ListPlayersCommand))
        {
            profile.ListPlayersCommand = template.ListPlayersCommand;
        }

        if (string.IsNullOrWhiteSpace(profile.BroadcastCommandPrefix))
        {
            profile.BroadcastCommandPrefix = template.BroadcastCommandPrefix;
        }

        if (string.IsNullOrWhiteSpace(profile.SaveCommand))
        {
            profile.SaveCommand = template.SaveCommand;
        }

        if (profile.PlayerJoinPatterns.Count == 0)
        {
            profile.PlayerJoinPatterns = template.PlayerJoinPatterns;
        }

        if (profile.PlayerLeavePatterns.Count == 0)
        {
            profile.PlayerLeavePatterns = template.PlayerLeavePatterns;
        }

        if (profile.ConfigurationSources.Count == 0)
        {
            profile.ConfigurationSources = template.ConfigurationSources;
        }

        if (profile.ConfigurationSchemas.Count == 0)
        {
            profile.ConfigurationSchemas = template.ConfigurationSchemas;
        }

        if (profile.FailurePatterns.Count == 0 && template.FailurePatterns.Count > 0)
        {
            profile.FailurePatterns = template.FailurePatterns;
        }
    }

    private static string? FindProfileIcon(string serverDirectory)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory) || !Directory.Exists(serverDirectory))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(serverDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    var extension = Path.GetExtension(path);
                    return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(extension, ".ico", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(ProfileIconPriority)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return null;
        }
    }

    private static int ProfileIconPriority(string path)
    {
        return Path.GetFileName(path).ToLowerInvariant() switch
        {
            "server-icon.png" => 0,
            "server-icon.ico" => 1,
            "icon.png" => 2,
            "icon.ico" => 3,
            _ => 4
        };
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
