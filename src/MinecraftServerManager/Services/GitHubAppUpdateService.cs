using System.Text.Json;
using MinecraftServerManager.Models;
using Velopack;
using Velopack.Sources;

namespace MinecraftServerManager.Services;

public sealed class GitHubAppUpdateService : IAppUpdateService
{
    private const string SettingsFileName = "UpdateSettings.json";
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly CancellationTokenSource _monitorCancellation = new();

    private UpdateManager? _updateManager;
    private UpdateInfo? _pendingUpdate;
    private int _monitorStarted;

    public event EventHandler<AppUpdateStatusChangedEventArgs>? StatusChanged;

    public bool IsUpdateReady => _pendingUpdate is not null;

    public void StartMonitoring()
    {
        if (Interlocked.Exchange(ref _monitorStarted, 1) != 0)
        {
            return;
        }

        _ = MonitorAsync(_monitorCancellation.Token);
    }

    public void ApplyUpdateAndRestart()
    {
        if (_updateManager is null || _pendingUpdate is null)
        {
            throw new InvalidOperationException("No downloaded update is ready to apply.");
        }

        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        AppUpdateSettings settings;
        try
        {
            settings = await LoadSettingsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Report(AppUpdateState.Failed, $"Updater configuration could not be read: {exception.Message}");
            return;
        }

        if (!TryGetRepositoryUrl(settings.GitHubRepositoryUrl, out var repositoryUrl))
        {
            Report(AppUpdateState.Disabled, "GitHub updates are awaiting a repository URL.");
            return;
        }

        _updateManager = new UpdateManager(
            new GithubSource(repositoryUrl, accessToken: null, prerelease: false));

        if (!_updateManager.IsInstalled)
        {
            Report(AppUpdateState.Disabled, "Automatic updates become active after installing the app.");
            return;
        }

        var intervalMinutes = Math.Clamp(settings.CheckIntervalMinutes, 5, 1_440);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        await CheckAndDownloadAsync(cancellationToken);
        while (!IsUpdateReady && await timer.WaitForNextTickAsync(cancellationToken))
        {
            await CheckAndDownloadAsync(cancellationToken);
        }
    }

    private async Task CheckAndDownloadAsync(CancellationToken cancellationToken)
    {
        if (_updateManager is null || IsUpdateReady)
        {
            return;
        }

        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            Report(AppUpdateState.Checking, "Checking GitHub Releases for an update…");
            var update = await _updateManager.CheckForUpdatesAsync();
            if (update is null)
            {
                var version = _updateManager.CurrentVersion?.ToString() ?? "current";
                Report(AppUpdateState.UpToDate, $"Version {version} is up to date.");
                return;
            }

            var targetVersion = update.TargetFullRelease.Version.ToString();
            Report(AppUpdateState.Downloading, $"Downloading version {targetVersion}…", 0);
            await _updateManager.DownloadUpdatesAsync(
                update,
                progress => Report(
                    AppUpdateState.Downloading,
                    $"Downloading version {targetVersion}: {progress}%",
                    progress),
                cancellationToken);

            _pendingUpdate = update;
            Report(AppUpdateState.ReadyToApply, $"Version {targetVersion} is downloaded and ready to install.", 100);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception exception)
        {
            Report(AppUpdateState.Failed, $"Update check failed: {exception.Message}");
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private static async Task<AppUpdateSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            throw new FileNotFoundException($"Updater settings were not found: {settingsPath}", settingsPath);
        }

        await using var stream = File.OpenRead(settingsPath);
        var settings = await JsonSerializer.DeserializeAsync<AppUpdateSettings>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);

        return settings ?? throw new InvalidDataException("Updater settings are empty.");
    }

    private static bool TryGetRepositoryUrl(string configuredUrl, out string repositoryUrl)
    {
        repositoryUrl = configuredUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Segments.Length < 3
            || repositoryUrl.Contains("OWNER", StringComparison.OrdinalIgnoreCase))
        {
            repositoryUrl = string.Empty;
            return false;
        }

        return true;
    }

    private void Report(AppUpdateState state, string message, int? progressPercent = null)
    {
        StatusChanged?.Invoke(this, new AppUpdateStatusChangedEventArgs(state, message, progressPercent));
    }
}
