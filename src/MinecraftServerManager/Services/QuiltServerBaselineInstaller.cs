using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed record QuiltInstallerArtifact(string Version, Uri DownloadUri);

public sealed class QuiltServerBaselineInstaller : IServerBaselineInstaller
{
    private const string MetadataHost = "meta.quiltmc.org";
    private const string MavenHost = "maven.quiltmc.org";
    private static readonly Uri InstallerMetadataUri = new(
        "https://meta.quiltmc.org/v3/versions/installer");
    private readonly IJavaRuntimeService _javaRuntimeService;

    public QuiltServerBaselineInstaller(IJavaRuntimeService javaRuntimeService)
    {
        _javaRuntimeService = javaRuntimeService;
    }

    public bool CanInstall(string loaderId) =>
        loaderId.Equals("quilt-loader", StringComparison.OrdinalIgnoreCase);

    public async Task<ServerBaselineInstallResult> InstallAsync(
        ServerBaselineInstallRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanInstall(request.LoaderId))
        {
            throw new NotSupportedException($"This installer does not support '{request.LoaderId}'.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LoaderVersion);
        var serverDirectory = Path.GetFullPath(request.ServerDirectory);
        if (!Directory.Exists(serverDirectory))
        {
            throw new DirectoryNotFoundException($"The staged server folder does not exist: {serverDirectory}");
        }

        if (ServerFolderDetector.Detect(serverDirectory) is { ServerType: "Quilt" } existing)
        {
            return new ServerBaselineInstallResult(
                false,
                existing.ServerJar,
                "The pack already supplied a runnable Quilt server launcher.");
        }

        progress?.Report("Checking the exact Quilt loader and official installer metadata…");
        var loaderMetadataUri = new Uri(
            $"https://{MetadataHost}/v3/versions/loader/{Uri.EscapeDataString(request.MinecraftVersion)}");
        var loaderMetadata = await JavaServerInstallerUtilities.DownloadMetadataAsync(
            loaderMetadataUri,
            MetadataHost,
            cancellationToken);
        using (var loaderDocument = JsonDocument.Parse(
            loaderMetadata,
            new JsonDocumentOptions { MaxDepth = 64 }))
        {
            ValidateLoaderVersion(loaderDocument.RootElement, request.LoaderVersion);
        }

        var installerMetadata = await JavaServerInstallerUtilities.DownloadMetadataAsync(
            InstallerMetadataUri,
            MetadataHost,
            cancellationToken);
        QuiltInstallerArtifact artifact;
        using (var installerDocument = JsonDocument.Parse(
            installerMetadata,
            new JsonDocumentOptions { MaxDepth = 32 }))
        {
            artifact = ParseInstallerArtifact(installerDocument.RootElement);
        }

        var workingDirectory = Path.Combine(
            serverDirectory,
            $".quilt-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        var installerPath = Path.Combine(
            workingDirectory,
            $"quilt-installer-{artifact.Version}.jar");
        try
        {
            // Quilt's metadata hash can lag a republished Maven artifact. The adjacent
            // Maven .sha256 is fetched and verified with the installer as one source of truth.
            await JavaServerInstallerUtilities.DownloadVerifiedInstallerAsync(
                artifact.DownloadUri,
                installerPath,
                MavenHost,
                progress,
                cancellationToken);
            var javaExecutable = await JavaServerInstallerUtilities.ResolveJavaExecutableAsync(
                _javaRuntimeService,
                request.MinecraftVersion,
                cancellationToken);
            progress?.Report(
                $"Installing Quilt {request.LoaderVersion} for Minecraft {request.MinecraftVersion}…");
            await JavaServerInstallerUtilities.RunInstallerAsync(
                javaExecutable,
                installerPath,
                workingDirectory,
                [
                    "install",
                    "server",
                    request.MinecraftVersion,
                    request.LoaderVersion,
                    "--download-server"
                ],
                progress,
                cancellationToken);

            var generatedServerDirectory = Path.Combine(workingDirectory, "server");
            JavaServerInstallerUtilities.ValidateInstalledServer(generatedServerDirectory, "Quilt");
            MergeInstalledServer(generatedServerDirectory, serverDirectory);
            JavaServerInstallerUtilities.ValidateInstalledServer(serverDirectory, "Quilt");
            var detection = ServerFolderDetector.Detect(serverDirectory)!;
            return new ServerBaselineInstallResult(
                true,
                detection.ServerJar,
                $"Installed Quilt {request.LoaderVersion} using the official checksum-verified installer.");
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    internal static QuiltInstallerArtifact ParseInstallerArtifact(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Quilt returned invalid installer metadata.");
        }

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryReadString(item, "version", out var version)
                || !TryReadString(item, "url", out var urlText)
                || !Uri.TryCreate(urlText, UriKind.Absolute, out var downloadUri)
                || downloadUri.Scheme != Uri.UriSchemeHttps
                || !downloadUri.Host.Equals(MavenHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expectedFileName = $"quilt-installer-{version}.jar";
            if (!Path.GetFileName(downloadUri.AbsolutePath)
                    .Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new QuiltInstallerArtifact(version, downloadUri);
        }

        throw new InvalidDataException("Quilt returned no valid official installer artifact.");
    }

    internal static void ValidateLoaderVersion(JsonElement root, string requestedLoaderVersion)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Quilt returned invalid loader metadata.");
        }

        var found = root.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("loader", out var loader)
            && loader.ValueKind == JsonValueKind.Object
            && TryReadString(loader, "version", out var version)
            && version.Equals(requestedLoaderVersion, StringComparison.OrdinalIgnoreCase));
        if (!found)
        {
            throw new InvalidDataException(
                $"Quilt loader {requestedLoaderVersion} is not published for this Minecraft version.");
        }
    }

    internal static void MergeInstalledServer(string sourceDirectory, string destinationDirectory)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException("The Quilt installer did not create its server folder.");
        }

        var directories = Directory.EnumerateDirectories(
                sourceRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(path => (Source: path, Destination: ResolveContainedPath(
                destinationRoot,
                Path.GetRelativePath(sourceRoot, path))))
            .OrderBy(item => item.Destination.Length)
            .ToArray();
        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Select(path => (Source: path, Destination: ResolveContainedPath(
                destinationRoot,
                Path.GetRelativePath(sourceRoot, path))))
            .ToArray();

        foreach (var directory in directories)
        {
            if (File.Exists(directory.Destination))
            {
                throw new InvalidDataException(
                    $"Quilt tried to replace a pack file with a directory: {Path.GetRelativePath(destinationRoot, directory.Destination)}");
            }
        }

        foreach (var file in files)
        {
            if (Directory.Exists(file.Destination))
            {
                throw new InvalidDataException(
                    $"Quilt tried to replace a pack directory with a file: {Path.GetRelativePath(destinationRoot, file.Destination)}");
            }

            if (File.Exists(file.Destination) && !FilesMatch(file.Source, file.Destination))
            {
                throw new InvalidDataException(
                    $"Quilt generated a different file that the pack already supplied: {Path.GetRelativePath(destinationRoot, file.Destination)}");
            }
        }

        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory.Destination);
        }

        foreach (var file in files.Where(item => !File.Exists(item.Destination)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
            File.Move(file.Source, file.Destination);
        }
    }

    private static bool TryReadString(JsonElement parent, string name, out string value)
    {
        if (parent.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(element.GetString()))
        {
            value = element.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Quilt generated a path outside the staged server folder.");
        }

        return resolved;
    }

    private static bool FilesMatch(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
