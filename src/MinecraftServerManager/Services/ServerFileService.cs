using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ServerFileService : IServerFileService
{
    public Task<IReadOnlyList<ServerFileItem>> GetItemsAsync(
        string serverRoot,
        string directory,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = NormalizeDirectory(serverRoot);
        var normalizedDirectory = NormalizeDirectory(directory);
        EnsureWithinRoot(normalizedRoot, normalizedDirectory);

        return Task.Run<IReadOnlyList<ServerFileItem>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = new DirectoryInfo(normalizedDirectory)
                .EnumerateFileSystemInfos()
                .Select(info =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isDirectory = info is DirectoryInfo;
                    long? size = info is FileInfo file ? file.Length : null;
                    return new ServerFileItem(
                        info.Name,
                        info.FullName,
                        isDirectory,
                        size,
                        info.LastWriteTime);
                })
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return items;
        }, cancellationToken);
    }

    public string? GetParentWithinRoot(string serverRoot, string directory)
    {
        var normalizedRoot = NormalizeDirectory(serverRoot);
        var normalizedDirectory = NormalizeDirectory(directory);
        EnsureWithinRoot(normalizedRoot, normalizedDirectory);

        if (string.Equals(normalizedRoot, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parent = Directory.GetParent(normalizedDirectory)?.FullName;
        if (parent is null)
        {
            return null;
        }

        var normalizedParent = NormalizeDirectory(parent);
        return IsWithinRoot(normalizedRoot, normalizedParent) ? normalizedParent : null;
    }

    private static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void EnsureWithinRoot(string root, string candidate)
    {
        if (!IsWithinRoot(root, candidate))
        {
            throw new InvalidOperationException("The requested folder is outside the selected server directory.");
        }
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
