using System.Net;
using System.Security.Cryptography;

namespace MinecraftServerManager.Services;

internal static class ModpackImportUtilities
{
    public static string CreateServerFolderName(string packName, string versionNumber)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var combined = $"{packName} {versionNumber}".Trim();
        var safe = new string(combined
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray())
            .Trim(' ', '.');
        if (safe.Length == 0)
        {
            safe = "Minecraft Modpack Server";
        }

        return safe.Length <= 100 ? safe : safe[..100].TrimEnd(' ', '.');
    }

    public static string ResolveSafePath(string rootDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var normalizedRoot = Path.GetFullPath(rootDirectory);
        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        var candidate = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootedPrefix = Path.TrimEndingDirectorySeparator(normalizedRoot)
            + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The package path leaves the server folder: {relativePath}");
        }

        return candidate;
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!relativePath.Equals(relativePath.Trim(), StringComparison.Ordinal)
            || normalized.StartsWith('/')
            || Path.IsPathRooted(normalized)
            || normalized.Contains(':')
            || segments.Length == 0
            || segments.Any(segment => segment is "." or ".."
                || !segment.Equals(segment.TrimEnd(' ', '.'), StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"The package contains an unsafe path: {relativePath}");
        }

        return string.Join('/', segments);
    }

    public static async Task DownloadFileAsync(
        HttpClient client,
        Uri source,
        string destinationPath,
        long expectedSize,
        long maximumSize,
        string expectedSha512,
        Func<Uri, bool> isApprovedSource,
        Action<long, long?>? reportBytes,
        CancellationToken cancellationToken)
    {
        if (!isApprovedSource(source))
        {
            throw new InvalidDataException($"The download source is not approved: {source.Host}");
        }

        using var response = await client.GetAsync(
            source,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("The download did not return a final address.");
        if (!isApprovedSource(finalUri))
        {
            throw new InvalidDataException(
                $"The download redirected to an unapproved address: {finalUri.Host}");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength > maximumSize)
        {
            throw new InvalidDataException("The download is larger than the safe size limit.");
        }

        if (expectedSize >= 0 && contentLength is >= 0 && contentLength != expectedSize)
        {
            throw new InvalidDataException("The download size does not match its published metadata.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destinationStream = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = expectedSha512.Length > 0
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA512)
            : null;
        var buffer = new byte[81920];
        long completed = 0;
        int read;
        while ((read = await sourceStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            completed = checked(completed + read);
            if (completed > maximumSize || expectedSize >= 0 && completed > expectedSize)
            {
                throw new InvalidDataException("The download expanded beyond its safe size.");
            }

            hash?.AppendData(buffer, 0, read);
            await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            reportBytes?.Invoke(completed, expectedSize >= 0 ? expectedSize : contentLength);
        }

        await destinationStream.FlushAsync(cancellationToken);
        if (expectedSize >= 0 && completed != expectedSize)
        {
            throw new InvalidDataException("The download is incomplete.");
        }

        if (hash is not null)
        {
            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!actualHash.Equals(expectedSha512, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded file failed SHA-512 verification.");
            }
        }
    }

    public static bool IsPublicHttpsUri(Uri uri, IReadOnlySet<string> approvedHosts) =>
        uri.Scheme == Uri.UriSchemeHttps
        && !uri.IsLoopback
        && approvedHosts.Contains(uri.Host)
        && !IPAddress.TryParse(uri.Host, out _);

    public static bool IsHexHash(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit);

    public static string GetLoaderDisplayName(string loaderId) => loaderId.ToLowerInvariant() switch
    {
        "fabric" or "fabric-loader" => "Fabric",
        "quilt" or "quilt-loader" => "Quilt",
        "neoforge" => "NeoForge",
        "forge" => "Forge",
        "minecraft" => "Vanilla Minecraft",
        "technic" => "Technic",
        _ => loaderId
    };

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

    public static void TryDeleteDirectory(string path)
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
