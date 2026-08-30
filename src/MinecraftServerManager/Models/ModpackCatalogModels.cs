namespace MinecraftServerManager.Models;

public sealed record ModpackCatalogSearchPage(
    IReadOnlyList<ModpackCatalogItem> Items,
    int Offset,
    int Limit,
    int TotalHits)
{
    public IReadOnlyList<ModpackProviderSearchStatus> ProviderStatuses { get; init; } = [];
}

public sealed record ModpackProviderSearchStatus(
    string ProviderId,
    string ProviderName,
    bool Succeeded,
    int ResultCount,
    string Message);

public sealed record ModpackCatalogItem(
    string ProviderId,
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string Author,
    string IconUrl,
    long Downloads,
    IReadOnlyList<string> MinecraftVersions,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Environments)
{
    public string ProviderName => ProviderId.ToLowerInvariant() switch
    {
        "modrinth" => "Modrinth",
        "technic" => "Technic",
        "ftb" => "Feed The Beast",
        _ => ProviderId
    };

    public string DownloadsText => $"{Downloads:N0} downloads";

    public string MetadataText => $"{ProviderName} • By {Author} • {DownloadsText}";

    public string VersionSummary => MinecraftVersions.Count == 0
        ? "Minecraft versions not supplied"
        : $"Minecraft {string.Join(", ", MinecraftVersions.Take(4))}{(MinecraftVersions.Count > 4 ? "…" : string.Empty)}";

    public string CategorySummary => Categories.Count == 0
        ? "Modpack"
        : string.Join(" • ", Categories.Take(4).Select(ToDisplayText));

    private static string ToDisplayText(string value) => string.Join(
        " ",
        value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}

public enum ModpackPackageKind
{
    ModrinthMrpack,
    TechnicServerArchive,
    FtbManifest
}

public sealed record ModpackCatalogFile(
    string FileName,
    Uri DownloadUri,
    long Size,
    string Sha1,
    string Sha512)
{
    public ModpackPackageKind PackageKind { get; init; } = ModpackPackageKind.ModrinthMrpack;

    public string SizeText => Size <= 0
        ? "size checked while downloading"
        : Size >= 1024 * 1024
        ? $"{Size / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1d, Size / 1024d):0.0} KB";

    public bool HasPublishedSha512 => Sha512.Length == 128 && Sha512.All(Uri.IsHexDigit);
}

public sealed record ModpackCatalogVersion(
    string ProviderId,
    string ProjectId,
    string VersionId,
    string Name,
    string VersionNumber,
    string ReleaseChannel,
    DateTimeOffset PublishedAt,
    IReadOnlyList<string> MinecraftVersions,
    IReadOnlyList<string> Loaders,
    string Environment,
    ModpackCatalogFile? PackFile)
{
    public string MinecraftVersionsText => MinecraftVersions.Count == 0
        ? "Unknown Minecraft version"
        : $"Minecraft {string.Join(", ", MinecraftVersions)}";

    public string LoadersText => Loaders.Count == 0
        ? "Loader not supplied"
        : string.Join(" / ", Loaders.Select(ToDisplayText));

    public string DisplayName => $"{VersionNumber} • {MinecraftVersionsText} • {LoadersText}";

    public bool IsServerCompatible => Environment is
        "dedicated_server_only" or
        "server_only" or
        "server_only_client_optional" or
        "client_and_server" or
        "client_only_server_optional" or
        "client_or_server" or
        "client_or_server_prefers_both";

    public string ServerCompatibilityText => Environment switch
    {
        "dedicated_server_only" => "Published for dedicated servers",
        "server_only" or "server_only_client_optional" => "Published as a server pack",
        "client_and_server" or "client_or_server" or "client_or_server_prefers_both" =>
            "Published for client and server use",
        "client_only_server_optional" => "Published primarily for clients, with optional server support",
        "client_only" or "singleplayer_only" => "Published as client-only; server import is unavailable",
        _ => "Server compatibility is not confirmed by the publisher"
    };

    public string PackageText => PackFile?.PackageKind switch
    {
        null => "No supported server download is published for this version.",
        ModpackPackageKind.ModrinthMrpack =>
            $"{PackFile.FileName} • {PackFile.SizeText} • SHA-512 published",
        ModpackPackageKind.TechnicServerArchive =>
            $"{PackFile.FileName} • official Technic server archive • no checksum published",
        ModpackPackageKind.FtbManifest =>
            "First-party FTB server manifest • every listed file has a published SHA-512 checksum",
        _ => PackFile.FileName
    };

    public string ImportReadinessText => PackFile is null
        ? "This provider has not published a supported server download for this version."
        : IsServerCompatible
            ? PackFile.PackageKind switch
            {
                ModpackPackageKind.TechnicServerArchive =>
                    "Ready for bounded installation from Technic's first-party server host. Technic does not publish an archive checksum.",
                ModpackPackageKind.FtbManifest =>
                    "Ready for a file-by-file verified FTB server installation.",
                _ => "Ready for verified server-pack installation."
            }
            : Environment is "client_only" or "singleplayer_only"
                ? "This client-only version will not be offered for server installation."
                : "The publisher has not confirmed server support, so import is unavailable.";

    private static string ToDisplayText(string value) => string.Join(
        " ",
        value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
