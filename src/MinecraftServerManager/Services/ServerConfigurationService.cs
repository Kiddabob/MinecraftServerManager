using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ServerConfigurationService : IServerConfigurationService
{
    private const int MaximumConfigurationFiles = 5_000;
    private const int MaximumConfigurationBytes = 2 * 1024 * 1024;

    private static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kidda.MinecraftServerManager",
        "ConfigurationBackups");

    private readonly string _backupRoot;

    public ServerConfigurationService()
        : this(BackupRoot)
    {
    }

    public ServerConfigurationService(string backupRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        _backupRoot = Path.GetFullPath(backupRoot);
    }

    public Task<ServerConfigurationDiscoveryResult> DiscoverAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return Task.Run(() => Discover(profile, cancellationToken), cancellationToken);
    }

    public async Task<ServerConfigurationDocument> ReadAsync(
        ServerProfile profile,
        ServerConfigurationFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(file);

        var path = ValidateConfigurationPath(profile.ServerDirectory, file.FullPath);
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("The configuration file no longer exists.", path);
        }

        if (fileInfo.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException("This configuration file is larger than the 2 MB editing limit.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var decoded = Decode(bytes);
        EnsureTextContent(decoded.Content);
        return new ServerConfigurationDocument(
            file with
            {
                SizeBytes = bytes.LongLength,
                LastWriteTimeUtc = fileInfo.LastWriteTimeUtc
            },
            decoded.Content,
            Hash(bytes),
            decoded.EncodingKind,
            decoded.HasByteOrderMark);
    }

    public async Task<ServerConfigurationSaveResult> SaveAsync(
        ServerProfile profile,
        ServerConfigurationDocument original,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(content);

        EnsureTextContent(content);
        ValidateStructuredContent(original.File.Name, content);

        var targetPath = ValidateConfigurationPath(profile.ServerDirectory, original.File.FullPath);
        var targetInfo = new FileInfo(targetPath);
        if (!targetInfo.Exists)
        {
            throw new FileNotFoundException("The configuration file was removed before it could be saved.", targetPath);
        }

        var currentBytes = await File.ReadAllBytesAsync(targetPath, cancellationToken);
        if (!string.Equals(Hash(currentBytes), original.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This file changed on disk after it was opened. Reload it before saving so another change is not overwritten.");
        }

        var outputBytes = Encode(content, original.EncodingKind, original.HasByteOrderMark);
        if (outputBytes.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException("The edited configuration is larger than the 2 MB editing limit.");
        }

        var backupPath = GetBackupPath(profile, original.File.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        await File.WriteAllBytesAsync(backupPath, currentBytes, cancellationToken);

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.msm-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, outputBytes, cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        var savedInfo = new FileInfo(targetPath);
        var savedFile = original.File with
        {
            SizeBytes = outputBytes.LongLength,
            LastWriteTimeUtc = savedInfo.LastWriteTimeUtc
        };
        var document = new ServerConfigurationDocument(
            savedFile,
            content,
            Hash(outputBytes),
            original.EncodingKind,
            original.HasByteOrderMark);
        return new ServerConfigurationSaveResult(document, backupPath);
    }

    private static ServerConfigurationDiscoveryResult Discover(
        ServerProfile profile,
        CancellationToken cancellationToken)
    {
        var serverRoot = NormalizeDirectory(profile.ServerDirectory);
        if (!Directory.Exists(serverRoot))
        {
            return new ServerConfigurationDiscoveryResult([], []);
        }

        var files = new List<ServerConfigurationFile>();
        var sources = new List<ServerConfigurationSourceStatus>();
        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in profile.ConfigurationSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.GetFullPath(Path.Combine(serverRoot, source.RelativePath));
            EnsureWithinRoot(serverRoot, sourcePath);

            var sourceIsFile = File.Exists(sourcePath);
            var sourceIsDirectory = Directory.Exists(sourcePath);
            var countBefore = files.Count;

            if (sourceIsFile)
            {
                TryAddFile(source, sourcePath, serverRoot, discoveredPaths, files);
            }
            else if (sourceIsDirectory)
            {
                foreach (var path in EnumerateSourceFiles(
                    sourcePath,
                    source.FilePatterns,
                    source.Recursive,
                    cancellationToken))
                {
                    TryAddFile(source, path, serverRoot, discoveredPaths, files);
                    if (files.Count >= MaximumConfigurationFiles)
                    {
                        break;
                    }
                }
            }

            sources.Add(new ServerConfigurationSourceStatus(
                source.Id,
                source.DisplayName,
                source.Category,
                source.RelativePath,
                sourceIsFile || sourceIsDirectory,
                files.Count - countBefore));

            if (files.Count >= MaximumConfigurationFiles)
            {
                break;
            }
        }

        return new ServerConfigurationDiscoveryResult(
            files
                .OrderBy(file => CategoryPriority(file.Category))
                .ThenBy(file => file.SourceName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(file => file.RelativePath, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            sources);
    }

    private static IEnumerable<string> EnumerateSourceFiles(
        string sourceRoot,
        IReadOnlyList<string> patterns,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var effectivePatterns = patterns.Count == 0 ? ["*"] : patterns;
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or PathTooLongException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or PathTooLongException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (recursive)
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                var name = Path.GetFileName(entry);
                if (effectivePatterns.Any(pattern =>
                    FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)))
                {
                    yield return entry;
                }
            }
        }
    }

    private static void TryAddFile(
        ServerConfigurationSource source,
        string path,
        string serverRoot,
        HashSet<string> discoveredPaths,
        List<ServerConfigurationFile> files)
    {
        try
        {
            var normalizedPath = ValidateConfigurationPath(serverRoot, path);
            var info = new FileInfo(normalizedPath);
            if (!info.Exists
                || (info.Attributes & FileAttributes.ReparsePoint) != 0
                || info.Length > MaximumConfigurationBytes
                || !discoveredPaths.Add(normalizedPath))
            {
                return;
            }

            files.Add(new ServerConfigurationFile(
                source.Id,
                source.DisplayName,
                source.Category,
                info.Name,
                Path.GetRelativePath(serverRoot, normalizedPath),
                normalizedPath,
                info.Length,
                info.LastWriteTimeUtc));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or PathTooLongException)
        {
            // A single inaccessible configuration file must not hide the rest of the dashboard.
        }
    }

    private static string ValidateConfigurationPath(string serverRoot, string path)
    {
        var normalizedRoot = NormalizeDirectory(serverRoot);
        var normalizedPath = Path.GetFullPath(path);
        EnsureWithinRoot(normalizedRoot, normalizedPath);
        return normalizedPath;
    }

    private static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void EnsureWithinRoot(string root, string candidate)
    {
        if (!string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The configuration path is outside the selected server directory.");
        }
    }

    private static void EnsureTextContent(string content)
    {
        if (content.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("This file appears to contain binary data and cannot be edited safely.");
        }
    }

    private static void ValidateStructuredContent(string fileName, string content)
    {
        var extension = Path.GetExtension(fileName);
        try
        {
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var _ = JsonDocument.Parse(content, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            }
            else if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                _ = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
            }
        }
        catch (Exception exception) when (exception is JsonException or System.Xml.XmlException)
        {
            throw new InvalidDataException(
                $"The edited {extension.TrimStart('.').ToUpperInvariant()} is not valid: {exception.Message}",
                exception);
        }
    }

    private static DecodedText Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return new DecodedText(Encoding.UTF8.GetString(bytes, Encoding.UTF8.Preamble.Length, bytes.Length - Encoding.UTF8.Preamble.Length), "utf-8", true);
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return new DecodedText(Encoding.Unicode.GetString(bytes, Encoding.Unicode.Preamble.Length, bytes.Length - Encoding.Unicode.Preamble.Length), "utf-16le", true);
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return new DecodedText(Encoding.BigEndianUnicode.GetString(bytes, Encoding.BigEndianUnicode.Preamble.Length, bytes.Length - Encoding.BigEndianUnicode.Preamble.Length), "utf-16be", true);
        }

        var strictUtf8 = new UTF8Encoding(false, true);
        try
        {
            return new DecodedText(strictUtf8.GetString(bytes), "utf-8", false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "This file is not UTF-8 or BOM-marked UTF-16, so it cannot be edited without risking character corruption.",
                exception);
        }
    }

    private static byte[] Encode(string content, string encodingKind, bool includePreamble)
    {
        var encoding = encodingKind switch
        {
            "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            _ => new UTF8Encoding(includePreamble, true)
        };
        var contentBytes = encoding.GetBytes(content);
        if (!includePreamble)
        {
            return contentBytes;
        }

        var preamble = encoding.GetPreamble();
        var output = new byte[preamble.Length + contentBytes.Length];
        preamble.CopyTo(output, 0);
        contentBytes.CopyTo(output, preamble.Length);
        return output;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private string GetBackupPath(ServerProfile profile, string relativePath)
    {
        var safeProfileId = string.Concat(profile.Id.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        return Path.Combine(_backupRoot, safeProfileId, timestamp, relativePath);
    }

    private static int CategoryPriority(string category) => category.ToLowerInvariant() switch
    {
        "core" => 0,
        "mods" => 1,
        "plugins" => 2,
        _ => 3
    };

    private sealed record DecodedText(string Content, string EncodingKind, bool HasByteOrderMark);
}
