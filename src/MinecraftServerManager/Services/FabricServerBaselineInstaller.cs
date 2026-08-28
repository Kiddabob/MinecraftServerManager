using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class FabricServerBaselineInstaller : IServerBaselineInstaller
{
    private const long MaximumLauncherBytes = 64L * 1024 * 1024;
    private const int MaximumManifestBytes = 256 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public bool CanInstall(string loaderId) =>
        loaderId.Equals("fabric-loader", StringComparison.OrdinalIgnoreCase);

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

        const string launcherFileName = "fabric-server-launch.jar";
        var launcherPath = Path.Combine(serverDirectory, launcherFileName);
        if (File.Exists(launcherPath))
        {
            ValidateLauncherJar(launcherPath);
            return new ServerBaselineInstallResult(
                false,
                launcherFileName,
                "The pack already supplied a valid Fabric server launcher.");
        }

        progress?.Report("Finding the current stable Fabric server-installer format…");
        var installerVersion = await GetStableInstallerVersionAsync(cancellationToken);
        var requestUri = new Uri(
            "https://meta.fabricmc.net/v2/versions/loader/"
            + $"{Uri.EscapeDataString(request.MinecraftVersion)}/"
            + $"{Uri.EscapeDataString(request.LoaderVersion)}/"
            + $"{Uri.EscapeDataString(installerVersion)}/server/jar");

        progress?.Report(
            $"Downloading the official Fabric {request.LoaderVersion} server launcher for Minecraft {request.MinecraftVersion}…");
        try
        {
            using var response = await HttpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            ValidateFinalFabricUri(response.RequestMessage?.RequestUri);

            if (response.Content.Headers.ContentLength is > MaximumLauncherBytes)
            {
                throw new InvalidDataException("The Fabric server launcher exceeds the safe download limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                launcherPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long totalBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalBytes = checked(totalBytes + read);
                    if (totalBytes > MaximumLauncherBytes)
                    {
                        throw new InvalidDataException("The Fabric server launcher exceeds the safe download limit.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
            }

            ValidateLauncherJar(launcherPath);
            return new ServerBaselineInstallResult(
                true,
                launcherFileName,
                $"Installed the official Fabric {request.LoaderVersion} server launcher.");
        }
        catch
        {
            TryDeleteFile(launcherPath);
            throw;
        }
    }

    internal static string ParseStableInstallerVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Fabric returned invalid installer metadata.");
        }

        foreach (var stableOnly in new[] { true, false })
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("version", out var versionElement)
                    || versionElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(versionElement.GetString()))
                {
                    continue;
                }

                var stable = item.TryGetProperty("stable", out var stableElement)
                    && stableElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && stableElement.GetBoolean();
                if (!stableOnly || stable)
                {
                    return versionElement.GetString()!;
                }
            }
        }

        throw new InvalidDataException("Fabric did not return an installer version.");
    }

    internal static void ValidateLauncherJar(string launcherPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(launcherPath);
            var manifest = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
            if (manifest is null || manifest.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("The Fabric server launcher has no valid JAR manifest.");
            }

            using var reader = new StreamReader(manifest.Open());
            var manifestText = reader.ReadToEnd();
            if (!manifestText.Contains("Main-Class:", StringComparison.OrdinalIgnoreCase)
                || !manifestText.Contains("fabric", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded JAR is not a Fabric server launcher.");
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The Fabric server launcher could not be validated.", exception);
        }
    }

    private static async Task<string> GetStableInstallerVersionAsync(CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync("v2/versions/installer", cancellationToken);
        response.EnsureSuccessStatusCode();
        ValidateFinalFabricUri(response.RequestMessage?.RequestUri);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { MaxDepth = 32 },
            cancellationToken);
        return ParseStableInstallerVersion(document.RootElement);
    }

    private static void ValidateFinalFabricUri(Uri? uri)
    {
        if (uri is null
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("meta.fabricmc.net", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Fabric redirected the server launcher request to an unexpected host.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://meta.fabricmc.net/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.1"));
        return client;
    }
}
