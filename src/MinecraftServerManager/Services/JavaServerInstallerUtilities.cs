using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MinecraftServerManager.Services;

internal static partial class JavaServerInstallerUtilities
{
    private const long MaximumInstallerBytes = 256L * 1024 * 1024;
    private const int MaximumMetadataBytes = 8 * 1024 * 1024;
    private const int MaximumChecksumBytes = 4096;
    private const int MaximumManifestBytes = 256 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static async Task<string> ResolveJavaExecutableAsync(
        IJavaRuntimeService javaRuntimeService,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var requiredMajor = javaRuntimeService.GetRecommendedJavaMajor(minecraftVersion)
            ?? throw new InvalidOperationException(
                $"The Java version required by Minecraft {minecraftVersion} could not be determined.");
        var runtimes = await javaRuntimeService.DiscoverAsync(["java"], cancellationToken);
        var runtime = runtimes
            .Where(candidate => candidate.MajorVersion == requiredMajor)
            .OrderByDescending(candidate =>
                candidate.Architecture.Contains("64", StringComparison.OrdinalIgnoreCase))
            .ThenBy(candidate => candidate.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return runtime?.ExecutablePath
            ?? throw new InvalidOperationException(
                $"Java {requiredMajor} is required to install this Minecraft {minecraftVersion} server. "
                + $"Install managed Java {requiredMajor} from Settings, then retry the modpack import.");
    }

    public static async Task DownloadVerifiedInstallerAsync(
        Uri installerUri,
        string destinationPath,
        string expectedHost,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        EnsureOfficialUri(installerUri, expectedHost);
        var checksumUri = new Uri(installerUri.AbsoluteUri + ".sha256");
        progress?.Report("Retrieving the installer's published SHA-256 checksum…");
        var checksumText = await DownloadSmallTextAsync(
            checksumUri,
            expectedHost,
            MaximumChecksumBytes,
            cancellationToken);
        var match = Sha256Pattern().Match(checksumText);
        if (!match.Success)
        {
            throw new InvalidDataException("The installer repository returned an invalid SHA-256 checksum.");
        }

        await DownloadVerifiedInstallerAsync(
            installerUri,
            destinationPath,
            expectedHost,
            match.Value,
            progress,
            cancellationToken);
    }

    public static async Task DownloadVerifiedInstallerAsync(
        Uri installerUri,
        string destinationPath,
        string expectedHost,
        string expectedSha256,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        EnsureOfficialUri(installerUri, expectedHost);
        if (!Sha256ExactPattern().IsMatch(expectedSha256))
        {
            throw new InvalidDataException("The installer metadata contained an invalid SHA-256 checksum.");
        }

        progress?.Report("Downloading and verifying the official server installer…");
        try
        {
            using var response = await HttpClient.GetAsync(
                installerUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            EnsureOfficialUri(response.RequestMessage?.RequestUri, expectedHost);
            if (response.Content.Headers.ContentLength is > MaximumInstallerBytes)
            {
                throw new InvalidDataException("The server installer exceeds the safe download limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await WriteVerifiedInstallerAsync(
                source,
                destinationPath,
                expectedSha256,
                cancellationToken);
        }
        catch
        {
            TryDeleteFile(destinationPath);
            throw;
        }
    }

    internal static async Task WriteVerifiedInstallerAsync(
        Stream source,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!Sha256ExactPattern().IsMatch(expectedSha256))
        {
            throw new InvalidDataException("The installer metadata contained an invalid SHA-256 checksum.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long completed = 0;
        int read;
        await using (var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                completed = checked(completed + read);
                if (completed > MaximumInstallerBytes)
                {
                    throw new InvalidDataException("The server installer exceeds the safe download limit.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await destination.FlushAsync(cancellationToken);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The server installer failed SHA-256 verification.");
        }

        ValidateInstallerJar(destinationPath);
    }

    public static async Task<string> DownloadMavenMetadataAsync(
        Uri metadataUri,
        string expectedHost,
        CancellationToken cancellationToken) => await DownloadSmallTextAsync(
            metadataUri,
            expectedHost,
            MaximumMetadataBytes,
            cancellationToken);

    public static async Task<string> DownloadMetadataAsync(
        Uri metadataUri,
        string expectedHost,
        CancellationToken cancellationToken) => await DownloadSmallTextAsync(
            metadataUri,
            expectedHost,
            MaximumMetadataBytes,
            cancellationToken);

    public static IReadOnlyList<string> ParseMavenVersions(string metadataXml)
    {
        using var stringReader = new StringReader(metadataXml);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumMetadataBytes
        });
        var document = XDocument.Load(xmlReader, LoadOptions.None);
        var versions = document
            .Descendants("version")
            .Select(element => element.Value.Trim())
            .Where(version => version.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (versions.Length == 0)
        {
            throw new InvalidDataException("The installer repository returned no published versions.");
        }

        return versions;
    }

    public static string ResolveForgeArtifactVersion(
        IReadOnlyList<string> publishedVersions,
        string minecraftVersion,
        string loaderVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderVersion);
        var exactCoordinate = loaderVersion.StartsWith(
            $"{minecraftVersion}-",
            StringComparison.OrdinalIgnoreCase)
            ? loaderVersion
            : $"{minecraftVersion}-{loaderVersion}";
        var exact = publishedVersions.FirstOrDefault(version =>
            version.Equals(exactCoordinate, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var loaderComponent = loaderVersion.StartsWith(
            $"{minecraftVersion}-",
            StringComparison.OrdinalIgnoreCase)
            ? loaderVersion[(minecraftVersion.Length + 1)..]
            : loaderVersion;
        var prefix = $"{minecraftVersion}-{loaderComponent}-";
        var historical = publishedVersions.LastOrDefault(version =>
            version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return historical
            ?? throw new InvalidDataException(
                $"Forge {loaderVersion} for Minecraft {minecraftVersion} is not published in the official Maven repository.");
    }

    public static string ResolveExactArtifactVersion(
        IReadOnlyList<string> publishedVersions,
        string loaderName,
        string loaderVersion) => publishedVersions.FirstOrDefault(version =>
            version.Equals(loaderVersion, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException(
            $"{loaderName} {loaderVersion} is not published in the official Maven repository.");

    public static async Task RunInstallerAsync(
        string javaExecutable,
        string installerPath,
        string workingDirectory,
        IReadOnlyList<string> installerArguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = javaExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        foreach (var argument in installerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The Java server installer could not be started.");
        }

        var tail = new ConcurrentQueue<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var standardOutput = PumpOutputAsync(process.StandardOutput, progress, tail, timeout.Token);
        var standardError = PumpOutputAsync(process.StandardError, progress, tail, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(standardOutput, standardError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await ObserveOutputTasksAsync(standardOutput, standardError);
            throw new TimeoutException("The Java server installer did not finish within 15 minutes.");
        }
        catch
        {
            TryKill(process);
            await ObserveOutputTasksAsync(standardOutput, standardError);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, tail.TakeLast(12));
            throw new InvalidOperationException(
                $"The Java server installer exited with code {process.ExitCode}."
                + (details.Length == 0 ? string.Empty : $"{Environment.NewLine}{details}"));
        }
    }

    public static void ValidateInstalledServer(string directory, string expectedServerType)
    {
        var detection = ServerFolderDetector.Detect(directory);
        if (detection is null
            || !detection.ServerType.Equals(expectedServerType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The installer completed, but no runnable {expectedServerType} server launcher was detected.");
        }
    }

    public static void TryDeleteFile(string path)
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

    private static async Task<string> DownloadSmallTextAsync(
        Uri uri,
        string expectedHost,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        EnsureOfficialUri(uri, expectedHost);
        using var response = await HttpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureOfficialUri(response.RequestMessage?.RequestUri, expectedHost);
        if (response.Content.Headers.ContentLength is > 0
            && response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("The installer repository metadata exceeds the safe limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var text = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (text.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The installer repository metadata exceeds the safe limit.");
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        IProgress<string>? progress,
        ConcurrentQueue<string> tail,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Length > 1000)
            {
                line = line[..1000] + "…";
            }

            tail.Enqueue(line);
            while (tail.Count > 40)
            {
                tail.TryDequeue(out _);
            }

            progress?.Report(line);
        }
    }

    private static async Task ObserveOutputTasksAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static void ValidateInstallerJar(string installerPath)
    {
        using var archive = ZipFile.OpenRead(installerPath);
        var manifest = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
        if (manifest is null || manifest.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The downloaded server installer has no valid JAR manifest.");
        }

        using var reader = new StreamReader(manifest.Open());
        var text = reader.ReadToEnd();
        if (!text.Contains("Main-Class:", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("installer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded JAR is not a server installer.");
        }
    }

    private static void EnsureOfficialUri(Uri? uri, string expectedHost)
    {
        if (uri is null
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The installer repository redirected to an unexpected host.");
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
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
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
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kidda.MinecraftServerManager", "0.1"));
        return client;
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256ExactPattern();
}
