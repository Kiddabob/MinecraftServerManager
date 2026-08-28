namespace MinecraftServerManager.Models;

public sealed record ModpackCatalogSearchPage(
    IReadOnlyList<ModpackCatalogItem> Items,
    int Offset,
    int Limit,
    int TotalHits);

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
    public string ProviderName => ProviderId.Equals("modrinth", StringComparison.OrdinalIgnoreCase)
        ? "Modrinth"
        : ProviderId;

    public string DownloadsText => $"{Downloads:N0} downloads";

    public string MetadataText => $"By {Author} • {DownloadsText}";

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

public sealed record ModpackCatalogFile(
    string FileName,
    Uri DownloadUri,
    long Size,
    string Sha1,
    string Sha512)
{
    public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1d, Size / 1024d):0.0} KB";
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

    public string PackageText => PackFile is null
        ? "No .mrpack download is available for this version."
        : $"{PackFile.FileName} • {PackFile.SizeText} • SHA-512 available";

    public string ImportReadinessText => PackFile is null
        ? "Choose another version before importing."
        : IsServerCompatible
            ? "Ready for verified server-pack installation."
            : Environment is "client_only" or "singleplayer_only"
                ? "This client-only version will not be offered for server installation."
                : "The publisher has not confirmed server support, so import is unavailable.";

    private static string ToDisplayText(string value) => string.Join(
        " ",
        value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
