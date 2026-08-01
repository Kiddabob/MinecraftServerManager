using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class JsonProfileService : IProfileService
{
    private const string ProfilesDirectoryName = "Profiles";
    private const string UserProfilesDirectoryName = "UserProfiles";

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

        foreach (var profilePath in EnumerateProfileFiles(PackagedProfilesDirectory))
        {
            var profile = await LoadProfileFileAsync(profilePath, cancellationToken);
            profilesById[profile.Id] = profile;
        }

        foreach (var profilePath in EnumerateProfileFiles(UserProfilesDirectory))
        {
            var profile = await LoadProfileFileAsync(profilePath, cancellationToken);
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

        if (template is null)
        {
            return new ProfileImportResult(
                null,
                false,
                "This folder was not recognised as a supported Tekkit server. Select the folder containing TekkitServer.jar.");
        }

        var folderName = new DirectoryInfo(normalizedFolder).Name;
        var importedProfile = CloneForFolder(template, normalizedFolder, folderName);

        Directory.CreateDirectory(UserProfilesDirectory);
        var profilePath = Path.Combine(UserProfilesDirectory, $"{importedProfile.Id}.json");
        await using (var stream = File.Create(profilePath))
        {
            await JsonSerializer.SerializeAsync(stream, importedProfile, SerializerOptions, cancellationToken);
        }

        return new ProfileImportResult(
            importedProfile,
            true,
            $"Created a Tekkit profile for '{folderName}'.");
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
            Id = $"{template.Id}-{pathHash}",
            DisplayName = string.Equals(folderName, template.DisplayName, StringComparison.OrdinalIgnoreCase)
                ? template.DisplayName
                : $"{template.DisplayName} — {folderName}",
            ServerType = template.ServerType,
            MinecraftVersion = template.MinecraftVersion,
            ForgeVersion = template.ForgeVersion,
            JavaVersion = template.JavaVersion,
            ServerDirectory = folderPath,
            JavaExecutable = template.JavaExecutable,
            ServerJar = template.ServerJar,
            JavaArguments = template.JavaArguments,
            ServerArguments = template.ServerArguments,
            RequiredFiles = template.RequiredFiles,
            RequiredDirectories = template.RequiredDirectories,
            ReadyPatterns = template.ReadyPatterns,
            StopCommand = template.StopCommand,
            StopTimeoutSeconds = template.StopTimeoutSeconds
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
