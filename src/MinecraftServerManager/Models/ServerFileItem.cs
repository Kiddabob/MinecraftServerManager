namespace MinecraftServerManager.Models;

public sealed record ServerFileItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long? SizeBytes,
    DateTime LastWriteTime)
{
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE7C3";

    public string Kind => IsDirectory
        ? "Folder"
        : string.IsNullOrWhiteSpace(Path.GetExtension(Name))
            ? "File"
            : $"{Path.GetExtension(Name).TrimStart('.').ToUpperInvariant()} file";

    public string SizeText => IsDirectory || SizeBytes is null
        ? string.Empty
        : FormatSize(SizeBytes.Value);

    public string ModifiedText => LastWriteTime.ToString("dd MMM yyyy, HH:mm");

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{value:0.#} {units[unitIndex]}";
    }
}
