using MinecraftServerManager.Infrastructure;
using System.Text.Json.Serialization;

namespace MinecraftServerManager.Models;

public enum WorldMapFormat
{
    Anvil,
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
    string DimensionKey,
    DateTimeOffset SavedUtc);

public sealed record WorldMapRadiusOption(int RadiusBlocks, string DisplayName);

public sealed record WorldMapRefreshOption(int Seconds, string DisplayName);

public sealed class WorldMapPointOfInterest : BindableBase
{
    private double _canvasLeft;
    private double _canvasTop;
    private double _markerScale = 1;
    private bool _isVisibleOnMap;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; } = 64;

    public double Z { get; set; }

    public int DimensionId { get; set; }

    public string DimensionKey { get; set; } = "overworld";

    [JsonIgnore]
    public string CoordinatesText => $"X {X:0.0}  Y {Y:0.0}  Z {Z:0.0}";

    [JsonIgnore]
    public string ToolTipText => $"{Name}\n{CoordinatesText}";

    [JsonIgnore]
    public double CanvasLeft
    {
        get => _canvasLeft;
        private set => SetProperty(ref _canvasLeft, value);
    }

    [JsonIgnore]
    public double CanvasTop
    {
        get => _canvasTop;
        private set => SetProperty(ref _canvasTop, value);
    }

    [JsonIgnore]
    public double MarkerScale
    {
        get => _markerScale;
        set => SetProperty(ref _markerScale, value);
    }

    [JsonIgnore]
    public bool IsVisibleOnMap
    {
        get => _isVisibleOnMap;
        private set => SetProperty(ref _isVisibleOnMap, value);
    }

    public void UpdateMapPlacement(double canvasLeft, double canvasTop, bool isVisible)
    {
        CanvasLeft = canvasLeft;
        CanvasTop = canvasTop;
        IsVisibleOnMap = isVisible;
    }
}

public sealed class WorldMapPlayerMarker : BindableBase
{
    private string? _avatarPath;
    private string _playerName;
    private bool _isOnline;
    private double _x;
    private double _y;
    private double _z;
    private float _yaw;
    private int _dimensionId;
    private string _dimensionKey;
    private string _dimensionDisplayName;
    private double _canvasLeft;
    private double _canvasTop;
    private DateTimeOffset _savedUtc;
    private string _freshnessText;
    private double _opacity;
    private double _markerScale = 1;
    private bool _isVisibleOnMap;

    public WorldMapPlayerMarker(
        string playerName,
        Guid? playerId,
        bool isOnline,
        double x,
        double y,
        double z,
        float yaw,
        int dimensionId,
        string dimensionKey,
        string dimensionDisplayName,
        double canvasLeft,
        double canvasTop,
        DateTimeOffset savedUtc,
        string freshnessText,
        double opacity,
        bool isVisibleOnMap)
    {
        _playerName = playerName;
        PlayerId = playerId;
        _isOnline = isOnline;
        _x = x;
        _y = y;
        _z = z;
        _yaw = yaw;
        _dimensionId = dimensionId;
        _dimensionKey = dimensionKey;
        _dimensionDisplayName = dimensionDisplayName;
        _canvasLeft = canvasLeft;
        _canvasTop = canvasTop;
        _savedUtc = savedUtc;
        _freshnessText = freshnessText;
        _opacity = opacity;
        _isVisibleOnMap = isVisibleOnMap;
    }

    public string StableKey => PlayerId?.ToString("N") ?? PlayerName.ToUpperInvariant();

    public string PlayerName
    {
        get => _playerName;
        private set => SetProperty(ref _playerName, value);
    }

    public Guid? PlayerId { get; }

    public bool IsOnline
    {
        get => _isOnline;
        private set => SetProperty(ref _isOnline, value);
    }

    public double X
    {
        get => _x;
        private set => SetProperty(ref _x, value);
    }

    public double Y
    {
        get => _y;
        private set => SetProperty(ref _y, value);
    }

    public double Z
    {
        get => _z;
        private set => SetProperty(ref _z, value);
    }

    public float Yaw
    {
        get => _yaw;
        private set => SetProperty(ref _yaw, value);
    }

    public int DimensionId
    {
        get => _dimensionId;
        private set => SetProperty(ref _dimensionId, value);
    }

    public string DimensionKey
    {
        get => _dimensionKey;
        private set => SetProperty(ref _dimensionKey, value);
    }

    public string DimensionDisplayName
    {
        get => _dimensionDisplayName;
        private set => SetProperty(ref _dimensionDisplayName, value);
    }

    public double CanvasLeft
    {
        get => _canvasLeft;
        private set => SetProperty(ref _canvasLeft, value);
    }

    public double CanvasTop
    {
        get => _canvasTop;
        private set => SetProperty(ref _canvasTop, value);
    }

    public DateTimeOffset SavedUtc
    {
        get => _savedUtc;
        private set => SetProperty(ref _savedUtc, value);
    }

    public string FreshnessText
    {
        get => _freshnessText;
        private set => SetProperty(ref _freshnessText, value);
    }

    public double Opacity
    {
        get => _opacity;
        private set => SetProperty(ref _opacity, value);
    }

    public double MarkerScale
    {
        get => _markerScale;
        set => SetProperty(ref _markerScale, value);
    }

    public bool IsVisibleOnMap
    {
        get => _isVisibleOnMap;
        private set => SetProperty(ref _isVisibleOnMap, value);
    }

    public string CoordinatesText => $"X {X:0.0}  Y {Y:0.0}  Z {Z:0.0}";

    public string ToolTipText => $"{PlayerName}\n{DimensionDisplayName} • {CoordinatesText}\n{FreshnessText}";

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

    public void Update(
        string playerName,
        bool isOnline,
        double x,
        double y,
        double z,
        float yaw,
        int dimensionId,
        string dimensionKey,
        string dimensionDisplayName,
        DateTimeOffset savedUtc,
        string freshnessText,
        double opacity)
    {
        PlayerName = playerName;
        IsOnline = isOnline;
        X = x;
        Y = y;
        Z = z;
        Yaw = yaw;
        DimensionId = dimensionId;
        DimensionKey = dimensionKey;
        DimensionDisplayName = dimensionDisplayName;
        SavedUtc = savedUtc;
        FreshnessText = freshnessText;
        Opacity = opacity;
        OnPropertyChanged(nameof(CoordinatesText));
        OnPropertyChanged(nameof(ToolTipText));
    }

    public void UpdateMapPlacement(double canvasLeft, double canvasTop, bool isVisible)
    {
        CanvasLeft = canvasLeft;
        CanvasTop = canvasTop;
        IsVisibleOnMap = isVisible;
    }
}
