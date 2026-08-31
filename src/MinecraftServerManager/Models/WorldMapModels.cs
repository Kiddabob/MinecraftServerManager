using MinecraftServerManager.Infrastructure;

namespace MinecraftServerManager.Models;

public enum WorldMapFormat
{
    LegacyAnvil,
    Unsupported
}

public sealed record WorldMapDimension(
    string Id,
    string DisplayName,
    string DirectoryPath,
    int NumericId,
    WorldMapFormat Format,
    int SurfaceMaximumY);

public sealed record WorldMapDescriptor(
    string WorldRoot,
    string LevelName,
    IReadOnlyList<WorldMapDimension> Dimensions,
    int SpawnX,
    int SpawnY,
    int SpawnZ);

public sealed record WorldMapRenderRequest(
    ServerProfile Profile,
    WorldMapDimension Dimension,
    int CenterX,
    int CenterZ,
    int RadiusBlocks,
    bool ForceRefresh = false);

public sealed record WorldMapRenderResult(
    string ImagePath,
    int PixelWidth,
    int PixelHeight,
    int MinimumX,
    int MinimumZ,
    int MaximumX,
    int MaximumZ,
    int BlocksPerPixel,
    int LoadedChunkCount,
    int ChangedChunkCount,
    DateTimeOffset RenderedUtc,
    bool HasTerrain,
    string Fingerprint);

public sealed record WorldMapPlayerPosition(
    string PlayerName,
    Guid? PlayerId,
    double X,
    double Y,
    double Z,
    float Yaw,
    int DimensionId,
    DateTimeOffset SavedUtc);

public sealed record WorldMapRadiusOption(int RadiusBlocks, string DisplayName);

public sealed record WorldMapRefreshOption(int Seconds, string DisplayName);

public sealed class WorldMapPlayerMarker : BindableBase
{
    private string? _avatarPath;

    public WorldMapPlayerMarker(
        string playerName,
        Guid? playerId,
        bool isOnline,
        double x,
        double y,
        double z,
        float yaw,
        double canvasLeft,
        double canvasTop,
        DateTimeOffset savedUtc,
        string freshnessText,
        double opacity)
    {
        PlayerName = playerName;
        PlayerId = playerId;
        IsOnline = isOnline;
        X = x;
        Y = y;
        Z = z;
        Yaw = yaw;
        CanvasLeft = canvasLeft;
        CanvasTop = canvasTop;
        SavedUtc = savedUtc;
        FreshnessText = freshnessText;
        Opacity = opacity;
    }

    public string PlayerName { get; }

    public Guid? PlayerId { get; }

    public bool IsOnline { get; }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }

    public float Yaw { get; }

    public double CanvasLeft { get; }

    public double CanvasTop { get; }

    public DateTimeOffset SavedUtc { get; }

    public string FreshnessText { get; }

    public double Opacity { get; }

    public string CoordinatesText => $"X {X:0.0}  Y {Y:0.0}  Z {Z:0.0}";

    public string ToolTipText => $"{PlayerName}\n{CoordinatesText}\n{FreshnessText}";

    public string? AvatarPath
    {
        get => _avatarPath;
        private set => SetProperty(ref _avatarPath, value);
    }

    public void SetAvatarPath(string? avatarPath)
    {
        if (!string.IsNullOrWhiteSpace(avatarPath))
        {
            AvatarPath = avatarPath;
        }
    }
}
