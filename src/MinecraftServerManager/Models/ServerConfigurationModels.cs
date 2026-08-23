namespace MinecraftServerManager.Models;

public sealed record ServerConfigurationFile(
    string SourceId,
    string SourceName,
    string Category,
    string Name,
    string RelativePath,
    string FullPath,
    long SizeBytes,
    DateTime LastWriteTimeUtc)
{
    public string ExtensionText => string.IsNullOrWhiteSpace(Path.GetExtension(Name))
        ? "Text"
        : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();

    public string SizeText => FormatSize(SizeBytes);

    public string ModifiedText => LastWriteTimeUtc.ToLocalTime().ToString("dd MMM yyyy, HH:mm");

    public string Glyph => Category.ToLowerInvariant() switch
    {
        "core" => "\uE713",
        "mods" => "\uE74C",
        "plugins" => "\uECAA",
        _ => "\uE8A5"
    };

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} B" : $"{value:0.#} {units[unitIndex]}";
    }
}

public sealed record ServerConfigurationSourceStatus(
    string Id,
    string DisplayName,
    string Category,
    string RelativePath,
    bool IsPresent,
    int FileCount)
{
    public string CountText => FileCount == 1 ? "1 editable file" : $"{FileCount:N0} editable files";

    public string PresenceText => IsPresent ? CountText : "Not detected";

    public string Glyph => Category.ToLowerInvariant() switch
    {
        "core" => "\uE713",
        "mods" => "\uE74C",
        "plugins" => "\uECAA",
        _ => "\uE8A5"
    };
}

public sealed record ServerConfigurationDiscoveryResult(
    IReadOnlyList<ServerConfigurationFile> Files,
    IReadOnlyList<ServerConfigurationSourceStatus> Sources);

public sealed record ServerConfigurationDocument(
    ServerConfigurationFile File,
    string Content,
    string ContentHash,
    string EncodingKind,
    bool HasByteOrderMark);

public sealed record ServerConfigurationSaveResult(
    ServerConfigurationDocument Document,
    string BackupPath);
