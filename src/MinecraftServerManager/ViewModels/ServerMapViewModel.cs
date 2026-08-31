using System.Collections.ObjectModel;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class ServerMapViewModel : BindableBase
{
    private readonly IWorldMapService _worldMapService;
    private readonly IPlayerPlaytimeService _playerPlaytimeService;
    private readonly IPlayerAvatarService _avatarService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private ServerSessionViewModel? _session;
    private WorldMapDescriptor? _world;
    private WorldMapDimension? _selectedDimension;
    private WorldMapRadiusOption _selectedRadius;
    private WorldMapRefreshOption _selectedRefreshInterval;
    private WorldMapPlayerMarker? _selectedPlayerMarker;
    private CancellationTokenSource? _profileCancellation;
    private CancellationTokenSource? _timerCancellation;
    private bool _isActive;
    private bool _isBusy;
    private bool _isLiveRefreshEnabled = true;
    private bool _showOfflinePlayers;
    private string _statusText = "Open a server profile to discover its world.";
    private string _worldSummary = "No world selected";
    private string _imagePath = string.Empty;
    private double _mapWidth = 768;
    private double _mapHeight = 768;
    private double _spawnLeft;
    private double _spawnTop;
    private bool _hasSpawnMarker;
    private int _centerX;
    private int _centerZ;
    private int _generation;

    public ServerMapViewModel(
        IWorldMapService worldMapService,
        IPlayerPlaytimeService playerPlaytimeService,
        IPlayerAvatarService avatarService,
        IUiDispatcher uiDispatcher)
    {
        _worldMapService = worldMapService;
        _playerPlaytimeService = playerPlaytimeService;
        _avatarService = avatarService;
        _uiDispatcher = uiDispatcher;

        RadiusOptions =
        [
            new(256, "512 × 512 blocks"),
            new(512, "1,024 × 1,024 blocks"),
            new(1_024, "2,048 × 2,048 blocks")
        ];
        RefreshIntervalOptions =
        [
            new(5, "Every 5 seconds"),
            new(15, "Every 15 seconds"),
            new(30, "Every 30 seconds"),
            new(60, "Every minute")
        ];
        _selectedRadius = RadiusOptions[1];
        _selectedRefreshInterval = RefreshIntervalOptions[1];

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(forceRefresh: true), CanRefresh);
        CenterOnSpawnCommand = new AsyncRelayCommand(CenterOnSpawnAsync, () => _world is not null);
        CenterOnSelectedPlayerCommand = new AsyncRelayCommand(
            CenterOnSelectedPlayerAsync,
            () => SelectedPlayerMarker is not null);
    }

    public ObservableCollection<WorldMapDimension> Dimensions { get; } = [];

    public ObservableCollection<WorldMapPlayerMarker> PlayerMarkers { get; } = [];

    public IReadOnlyList<WorldMapRadiusOption> RadiusOptions { get; }

    public IReadOnlyList<WorldMapRefreshOption> RefreshIntervalOptions { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand CenterOnSpawnCommand { get; }

    public AsyncRelayCommand CenterOnSelectedPlayerCommand { get; }

    public WorldMapDimension? SelectedDimension
    {
        get => _selectedDimension;
        set
        {
            if (SetProperty(ref _selectedDimension, value) && value is not null)
            {
                OnPropertyChanged(nameof(SelectedDimensionDetails));
                _ = RefreshAsync(forceRefresh: false);
            }
        }
    }

    public string SelectedDimensionDetails => SelectedDimension is null
        ? "No compatible dimension"
        : $"{SelectedDimension.DisplayName} • {SelectedDimension.DirectoryPath}";

    public WorldMapRadiusOption SelectedRadius
    {
        get => _selectedRadius;
        set
        {
            if (value is not null && SetProperty(ref _selectedRadius, value))
            {
                _ = RefreshAsync(forceRefresh: false);
            }
        }
    }

    public WorldMapRefreshOption SelectedRefreshInterval
    {
        get => _selectedRefreshInterval;
        set
        {
            if (value is not null && SetProperty(ref _selectedRefreshInterval, value))
            {
                RestartTimer();
            }
        }
    }

    public WorldMapPlayerMarker? SelectedPlayerMarker
    {
        get => _selectedPlayerMarker;
        set
        {
            if (SetProperty(ref _selectedPlayerMarker, value))
            {
                CenterOnSelectedPlayerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsLiveRefreshEnabled
    {
        get => _isLiveRefreshEnabled;
        set
        {
            if (SetProperty(ref _isLiveRefreshEnabled, value))
            {
                RestartTimer();
                OnPropertyChanged(nameof(LiveRefreshSummary));
            }
        }
    }

    public string LiveRefreshSummary => IsLiveRefreshEnabled
        ? $"Checks changed region headers {SelectedRefreshInterval.DisplayName.ToLowerInvariant()}."
        : "Automatic refresh is paused.";

    public bool ShowOfflinePlayers
    {
        get => _showOfflinePlayers;
        set
        {
            if (SetProperty(ref _showOfflinePlayers, value))
            {
                _ = RefreshPlayerMarkersAsync(_generation, CancellationToken.None);
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string WorldSummary
    {
        get => _worldSummary;
        private set => SetProperty(ref _worldSummary, value);
    }

    public string ImagePath
    {
        get => _imagePath;
        private set => SetProperty(ref _imagePath, value);
    }

    public double MapWidth
    {
        get => _mapWidth;
        private set => SetProperty(ref _mapWidth, value);
    }

    public double MapHeight
    {
        get => _mapHeight;
        private set => SetProperty(ref _mapHeight, value);
    }

    public double SpawnLeft
    {
        get => _spawnLeft;
        private set => SetProperty(ref _spawnLeft, value);
    }

    public double SpawnTop
    {
        get => _spawnTop;
        private set => SetProperty(ref _spawnTop, value);
    }

    public bool HasSpawnMarker
    {
        get => _hasSpawnMarker;
        private set => SetProperty(ref _hasSpawnMarker, value);
    }

    public string SpawnToolTip => _world is null
        ? "World spawn"
        : $"World spawn\nX {_world.SpawnX}  Y {_world.SpawnY}  Z {_world.SpawnZ}";

    public int CenterX
    {
        get => _centerX;
        private set => SetProperty(ref _centerX, value);
    }

    public int CenterZ
    {
        get => _centerZ;
        private set => SetProperty(ref _centerZ, value);
    }

    public string CenterText => $"Centre X {CenterX:N0}, Z {CenterZ:N0}";

    public async Task SelectProfileAsync(ServerSessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var generation = ++_generation;
        _profileCancellation?.Cancel();
        _profileCancellation?.Dispose();
        _profileCancellation = new CancellationTokenSource();
        _session = session;
        _world = null;
        ImagePath = string.Empty;
        Dimensions.Clear();
        PlayerMarkers.Clear();
        SelectedPlayerMarker = null;
        HasSpawnMarker = false;
        StatusText = $"Discovering {session.DisplayName}'s world…";
        WorldSummary = "Scanning server.properties and region folders";
        RefreshCommand.NotifyCanExecuteChanged();

        try
        {
            var world = await _worldMapService.DiscoverAsync(session.Profile, _profileCancellation.Token);
            if (generation != _generation)
            {
                return;
            }

            _world = world;
            foreach (var dimension in world.Dimensions)
            {
                Dimensions.Add(dimension);
            }

            CenterX = world.SpawnX;
            CenterZ = world.SpawnZ;
            OnPropertyChanged(nameof(CenterText));
            OnPropertyChanged(nameof(SpawnToolTip));
            WorldSummary = $"{world.LevelName} • {world.Dimensions.Count} compatible dimension{(world.Dimensions.Count == 1 ? string.Empty : "s")}";
            StatusText = world.Dimensions.Count == 0
                ? "No legacy Anvil region folders were found in this world. Modern palette worlds are planned for a later renderer."
                : "World discovered. Open Map to render the selected area.";
            _selectedDimension = world.Dimensions.FirstOrDefault(dimension => dimension.NumericId == 0)
                ?? world.Dimensions.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedDimension));
            OnPropertyChanged(nameof(SelectedDimensionDetails));
            CenterOnSpawnCommand.NotifyCanExecuteChanged();
            RefreshCommand.NotifyCanExecuteChanged();
            RestartTimer();
            if (_isActive && SelectedDimension is not null)
            {
                await RefreshAsync(forceRefresh: false);
            }
        }
        catch (OperationCanceledException) when (generation != _generation)
        {
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or NotSupportedException
                or ArgumentException)
        {
            if (generation == _generation)
            {
                StatusText = exception.Message;
                WorldSummary = "World map unavailable";
            }
        }
    }

    public void SetActive(bool isActive)
    {
        _isActive = isActive;
        RestartTimer();
        if (isActive && SelectedDimension is not null && string.IsNullOrWhiteSpace(ImagePath))
        {
            _ = RefreshAsync(forceRefresh: false);
        }
    }

    private bool CanRefresh() => _session is not null && SelectedDimension is not null && !IsBusy;

    private async Task RefreshAsync(bool forceRefresh)
    {
        var session = _session;
        var world = _world;
        var dimension = SelectedDimension;
        var generation = _generation;
        if (session is null || world is null || dimension is null
            || !await _refreshGate.WaitAsync(0))
        {
            return;
        }

        IsBusy = true;
        StatusText = forceRefresh ? "Re-reading world regions…" : "Rendering the selected world area…";
        try
        {
            var cancellationToken = _profileCancellation?.Token ?? CancellationToken.None;
            var result = await _worldMapService.RenderAsync(
                new WorldMapRenderRequest(
                    session.Profile,
                    dimension,
                    CenterX,
                    CenterZ,
                    SelectedRadius.RadiusBlocks,
                    forceRefresh),
                cancellationToken);
            if (generation != _generation)
            {
                return;
            }

            MapWidth = result.PixelWidth;
            MapHeight = result.PixelHeight;
            if (forceRefresh && string.Equals(ImagePath, result.ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                ImagePath = string.Empty;
            }

            ImagePath = result.ImagePath;
            UpdateSpawnMarker(result, dimension);
            await RefreshPlayerMarkersAsync(generation, cancellationToken, result);
            StatusText = result.HasTerrain
                ? $"{result.LoadedChunkCount:N0} chunks shown • {result.ChangedChunkCount:N0} read from disk • {result.BlocksPerPixel} block{(result.BlocksPerPixel == 1 ? string.Empty : "s")} per pixel • {result.RenderedUtc.ToLocalTime():HH:mm:ss}"
                : "No generated chunks were found in the selected area.";
        }
        catch (OperationCanceledException) when (generation != _generation)
        {
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ArgumentException)
        {
            if (generation == _generation)
            {
                StatusText = $"Map refresh failed: {exception.Message}";
            }
        }
        finally
        {
            IsBusy = false;
            _refreshGate.Release();
        }
    }

    private async Task RefreshPlayerMarkersAsync(
        int generation,
        CancellationToken cancellationToken,
        WorldMapRenderResult? renderResult = null)
    {
        var session = _session;
        var world = _world;
        var dimension = SelectedDimension;
        if (session is null || world is null || dimension is null)
        {
            return;
        }

        renderResult ??= CurrentRenderBounds();
        if (renderResult is null)
        {
            return;
        }

        var snapshots = _playerPlaytimeService.GetSnapshots()
            .Where(snapshot => snapshot.ProfileId.Equals(session.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var onlineNames = snapshots
            .Where(snapshot => snapshot.IsOnline)
            .Select(snapshot => snapshot.PlayerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyCollection<string>? requestedNames = ShowOfflinePlayers ? null : onlineNames;
        var positions = await _worldMapService.ReadPlayerPositionsAsync(
            session.Profile,
            world,
            requestedNames,
            cancellationToken);
        if (generation != _generation)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var markers = positions
            .Where(position => position.DimensionId == dimension.NumericId)
            .Where(position => position.X >= renderResult.MinimumX && position.X <= renderResult.MaximumX)
            .Where(position => position.Z >= renderResult.MinimumZ && position.Z <= renderResult.MaximumZ)
            .Select(position =>
            {
                var online = onlineNames.Contains(position.PlayerName);
                var age = now - position.SavedUtc;
                var freshness = age < TimeSpan.FromMinutes(1)
                    ? $"Saved {Math.Max(0, (int)age.TotalSeconds)} seconds ago"
                    : $"Last saved {FormatAge(age)} ago";
                return new WorldMapPlayerMarker(
                    position.PlayerName,
                    position.PlayerId,
                    online,
                    position.X,
                    position.Y,
                    position.Z,
                    position.Yaw,
                    (position.X - renderResult.MinimumX) / renderResult.BlocksPerPixel - 16,
                    (position.Z - renderResult.MinimumZ) / renderResult.BlocksPerPixel - 16,
                    position.SavedUtc,
                    freshness,
                    online ? 1 : 0.7);
            })
            .OrderByDescending(marker => marker.IsOnline)
            .ThenBy(marker => marker.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        PlayerMarkers.Clear();
        foreach (var marker in markers)
        {
            PlayerMarkers.Add(marker);
            _ = LoadAvatarAsync(marker, generation);
        }

        SelectedPlayerMarker = PlayerMarkers.FirstOrDefault();
    }

    private WorldMapRenderResult? CurrentRenderBounds()
    {
        if (string.IsNullOrWhiteSpace(ImagePath))
        {
            return null;
        }

        var radius = SelectedRadius.RadiusBlocks;
        var blocksPerPixel = Math.Max(1, (int)Math.Ceiling(radius * 2d / MapWidth));
        return new WorldMapRenderResult(
            ImagePath,
            (int)MapWidth,
            (int)MapHeight,
            CenterX - radius,
            CenterZ - radius,
            CenterX + radius - 1,
            CenterZ + radius - 1,
            blocksPerPixel,
            0,
            0,
            DateTimeOffset.UtcNow,
            true,
            string.Empty);
    }

    private async Task LoadAvatarAsync(WorldMapPlayerMarker marker, int generation)
    {
        var path = await _avatarService.GetAvatarPathAsync(marker.PlayerName, marker.PlayerId);
        if (generation == _generation && !string.IsNullOrWhiteSpace(path))
        {
            _uiDispatcher.TryEnqueue(() => marker.SetAvatarPath(path));
        }
    }

    private void UpdateSpawnMarker(WorldMapRenderResult result, WorldMapDimension dimension)
    {
        var world = _world;
        HasSpawnMarker = world is not null
            && dimension.NumericId == 0
            && world.SpawnX >= result.MinimumX
            && world.SpawnX <= result.MaximumX
            && world.SpawnZ >= result.MinimumZ
            && world.SpawnZ <= result.MaximumZ;
        if (!HasSpawnMarker || world is null)
        {
            return;
        }

        SpawnLeft = (world.SpawnX - result.MinimumX) / (double)result.BlocksPerPixel - 10;
        SpawnTop = (world.SpawnZ - result.MinimumZ) / (double)result.BlocksPerPixel - 10;
    }

    private Task CenterOnSpawnAsync()
    {
        if (_world is null)
        {
            return Task.CompletedTask;
        }

        CenterX = _world.SpawnX;
        CenterZ = _world.SpawnZ;
        OnPropertyChanged(nameof(CenterText));
        return RefreshAsync(forceRefresh: false);
    }

    private Task CenterOnSelectedPlayerAsync()
    {
        if (SelectedPlayerMarker is null)
        {
            return Task.CompletedTask;
        }

        CenterX = (int)Math.Floor(SelectedPlayerMarker.X);
        CenterZ = (int)Math.Floor(SelectedPlayerMarker.Z);
        OnPropertyChanged(nameof(CenterText));
        return RefreshAsync(forceRefresh: false);
    }

    private void RestartTimer()
    {
        _timerCancellation?.Cancel();
        _timerCancellation?.Dispose();
        _timerCancellation = null;
        OnPropertyChanged(nameof(LiveRefreshSummary));
        if (!_isActive || !IsLiveRefreshEnabled || SelectedDimension is null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _timerCancellation = cancellation;
        _ = RunTimerAsync(SelectedRefreshInterval.Seconds, cancellation.Token);
    }

    private async Task RunTimerAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                _uiDispatcher.TryEnqueue(() => _ = RefreshAsync(forceRefresh: false));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            return "a moment";
        }

        if (age < TimeSpan.FromMinutes(2))
        {
            return "1 minute";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} minutes";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours} hours";
        }

        return $"{(int)age.TotalDays} days";
    }
}
