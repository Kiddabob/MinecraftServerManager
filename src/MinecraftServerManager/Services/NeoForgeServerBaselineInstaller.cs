using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class NeoForgeServerBaselineInstaller : IServerBaselineInstaller
{
    private const string MavenHost = "maven.neoforged.net";
    private static readonly Uri MetadataUri = new(
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");
    private readonly IJavaRuntimeService _javaRuntimeService;

    public NeoForgeServerBaselineInstaller(IJavaRuntimeService javaRuntimeService)
    {
        _javaRuntimeService = javaRuntimeService;
    }

    public bool CanInstall(string loaderId) =>
        loaderId.Equals("neoforge", StringComparison.OrdinalIgnoreCase);

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

        var serverDirectory = Path.GetFullPath(request.ServerDirectory);
        if (!Directory.Exists(serverDirectory))
        {
            throw new DirectoryNotFoundException($"The staged server folder does not exist: {serverDirectory}");
        }

        if (ServerFolderDetector.Detect(serverDirectory) is { ServerType: "NeoForge" } existing)
        {
            return new ServerBaselineInstallResult(
                false,
                existing.ServerJar,
                "The pack already supplied a runnable NeoForge server launcher.");
        }

        progress?.Report("Resolving the exact NeoForge installer from the official Maven repository…");
        var metadata = await JavaServerInstallerUtilities.DownloadMavenMetadataAsync(
            MetadataUri,
            MavenHost,
            cancellationToken);
        var artifactVersion = JavaServerInstallerUtilities.ResolveExactArtifactVersion(
            JavaServerInstallerUtilities.ParseMavenVersions(metadata),
            "NeoForge",
            request.LoaderVersion);
        var escapedVersion = Uri.EscapeDataString(artifactVersion);
        var installerFileName = $"neoforge-{artifactVersion}-installer.jar";
        var installerUri = new Uri(
            $"https://{MavenHost}/releases/net/neoforged/neoforge/{escapedVersion}/{Uri.EscapeDataString(installerFileName)}");
        var installerPath = Path.Combine(serverDirectory, installerFileName);
        try
        {
            await JavaServerInstallerUtilities.DownloadVerifiedInstallerAsync(
                installerUri,
                installerPath,
                MavenHost,
                progress,
                cancellationToken);
            var javaExecutable = await JavaServerInstallerUtilities.ResolveJavaExecutableAsync(
                _javaRuntimeService,
                request.MinecraftVersion,
                cancellationToken);
            progress?.Report(
                $"Installing NeoForge {request.LoaderVersion} for Minecraft {request.MinecraftVersion}…");
            await JavaServerInstallerUtilities.RunInstallerAsync(
                javaExecutable,
                installerPath,
                serverDirectory,
                ["--installServer"],
                progress,
                cancellationToken);
            JavaServerInstallerUtilities.ValidateInstalledServer(serverDirectory, "NeoForge");
            var detection = ServerFolderDetector.Detect(serverDirectory)!;
            return new ServerBaselineInstallResult(
                true,
                detection.ServerJar,
                $"Installed NeoForge {request.LoaderVersion} from the official checksum-verified installer.");
        }
        finally
        {
            JavaServerInstallerUtilities.TryDeleteFile(installerPath);
        }
    }
}
