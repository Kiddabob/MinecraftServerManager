using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class MainViewModel : BindableBase
{
    private readonly IProfileService _profileService;
    private readonly IProfileValidator _profileValidator;
    private readonly IServerLaunchRequestFactory _launchRequestFactory;
    private readonly IServerConsoleParserFactory _consoleParserFactory;
    private readonly IServerProcessServiceFactory _processServiceFactory;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly IServerFileService _serverFileService;
    private readonly IUiDispatcher _uiDispatcher;

    private ServerSessionViewModel? _selectedProfile;
    private bool _initialized;
    private bool _changingProfile;
    private string _profileImportStatus = "Choose a Tekkit server folder to detect or create a profile.";
    private string _updateStatus = "Updater is starting…";
    private string _currentFilesPath = string.Empty;
    private string _filesStatus = "Select a profile to browse its server files.";
    private string _settingsStatus = "Settings are stored for this Windows account.";
    private bool _canNavigateUp;
    private bool _isUpdateReady;
    private AppThemeOption? _selectedThemeOption;
    private AccentColorOption? _selectedAccentOption;
    private UpdateIntervalOption? _selectedUpdateIntervalOption;

    public MainViewModel(
        IProfileService profileService,
        IProfileValidator profileValidator,
        IServerLaunchRequestFactory launchRequestFactory,
        IServerConsoleParserFactory consoleParserFactory,
        IServerProcessServiceFactory processServiceFactory,
        IAppUpdateService appUpdateService,
        IAppSettingsService appSettingsService,
        IServerFileService serverFileService,
        IUiDispatcher uiDispatcher)
    {
        _profileService = profileService;
        _profileValidator = profileValidator;
        _launchRequestFactory = launchRequestFactory;
        _consoleParserFactory = consoleParserFactory;
        _processServiceFactory = processServiceFactory;
        _appUpdateService = appUpdateService;
        _appSettingsService = appSettingsService;
        _serverFileService = serverFileService;
        _uiDispatcher = uiDispatcher;

        ThemeOptions =
        [
            new("System", "Use Windows setting"),
            new("Light", "Light"),
            new("Dark", "Dark")
        ];
        AccentOptions =
        [
            new("System", "Windows accent", "#60CDFF"),
            new("Blue", "Blue", "#60CDFF"),
            new("Emerald", "Emerald", "#2CCB70"),
            new("Amethyst", "Amethyst", "#A78BFA"),
            new("Amber", "Amber", "#F5B942")
        ];
        UpdateIntervalOptions =
        [
            new(5, "Every 5 minutes"),
            new(15, "Every 15 minutes"),
            new(30, "Every 30 minutes"),
            new(60, "Every hour"),
            new(360, "Every 6 hours"),
            new(1_440, "Daily")
        ];

        StartSelectedCommand = new AsyncRelayCommand(StartSelectedAsync, CanStartSelected);
        StopSelectedCommand = new AsyncRelayCommand(StopSelectedAsync, CanStopSelected);
        ApplyUpdateCommand = new AsyncRelayCommand(ApplyUpdateAsync, CanApplyUpdate);
        RefreshFilesCommand = new AsyncRelayCommand(RefreshServerFilesAsync, CanBrowseFiles);
        NavigateUpCommand = new AsyncRelayCommand(NavigateUpAsync, () => CanNavigateUp);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);

        _appUpdateService.StatusChanged += OnUpdateStatusChanged;
    }

    public ObservableCollection<ServerSessionViewModel> Profiles { get; } = [];

    public ObservableCollection<ServerFileItem> ServerFiles { get; } = [];

    public IReadOnlyList<AppThemeOption> ThemeOptions { get; }

    public IReadOnlyList<AccentColorOption> AccentOptions { get; }

    public IReadOnlyList<UpdateIntervalOption> UpdateIntervalOptions { get; }

    public AsyncRelayCommand StartSelectedCommand { get; }

    public AsyncRelayCommand StopSelectedCommand { get; }

    public AsyncRelayCommand ApplyUpdateCommand { get; }

    public AsyncRelayCommand RefreshFilesCommand { get; }

    public AsyncRelayCommand NavigateUpCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public ServerSessionViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_changingProfile || value is null || ReferenceEquals(_selectedProfile, value))
            {
                return;
            }

            _selectedProfile = value;
            CurrentFilesPath = value.ServerDirectory;
            OnPropertyChanged();
            RefreshFilesCommand.NotifyCanExecuteChanged();
            _ = RefreshServerFilesAsync();
            _ = PersistPreferencesAsync("Profile selection saved.");
        }
    }

    public AppThemeOption? SelectedThemeOption
    {
        get => _selectedThemeOption;
        set => SetProperty(ref _selectedThemeOption, value);
    }

    public AccentColorOption? SelectedAccentOption
    {
        get => _selectedAccentOption;
        set => SetProperty(ref _selectedAccentOption, value);
    }

    public UpdateIntervalOption? SelectedUpdateIntervalOption
    {
        get => _selectedUpdateIntervalOption;
        set => SetProperty(ref _selectedUpdateIntervalOption, value);
    }

    public string ProfileImportStatus
    {
        get => _profileImportStatus;
        private set => SetProperty(ref _profileImportStatus, value);
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public string CurrentFilesPath
    {
        get => _currentFilesPath;
        private set => SetProperty(ref _currentFilesPath, value);
    }

    public string FilesStatus
    {
        get => _filesStatus;
        private set => SetProperty(ref _filesStatus, value);
    }

    public string SettingsStatus
    {
        get => _settingsStatus;
        private set => SetProperty(ref _settingsStatus, value);
    }

    public bool CanNavigateUp
    {
        get => _canNavigateUp;
        private set
        {
            if (SetProperty(ref _canNavigateUp, value))
            {
                NavigateUpCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsUpdateReady
    {
        get => _isUpdateReady;
        private set
        {
            if (SetProperty(ref _isUpdateReady, value))
            {
                ApplyUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsServerActive => Profiles.Any(profile => profile.IsServerActive);

    public string ProfileCountText => Profiles.Count == 1
        ? "1 server profile"
        : $"{Profiles.Count} server profiles";

    public string SelectedProfileCountText
    {
        get
        {
            var count = Profiles.Count(profile => profile.IsSelectedForBulk);
            return count == 1 ? "1 selected" : $"{count} selected";
        }
    }

    public string ActiveServerCountText
    {
        get
        {
            var count = Profiles.Count(profile => profile.IsServerActive);
            return count == 1 ? "1 server active" : $"{count} servers active";
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            var preferences = await _appSettingsService.LoadAsync();
            SelectedThemeOption = ThemeOptions.First(option => option.Id == preferences.Theme);
            SelectedAccentOption = AccentOptions.First(option => option.Id == preferences.AccentColor);
            SelectedUpdateIntervalOption = UpdateIntervalOptions.First(
                option => option.Minutes == preferences.UpdateCheckIntervalMinutes);

            _appUpdateService.SetCheckIntervalMinutes(preferences.UpdateCheckIntervalMinutes);
            _appUpdateService.StartMonitoring();

            foreach (var profile in await _profileService.LoadAllAsync())
            {
                AddProfile(profile);
            }

            OnPropertyChanged(nameof(ProfileCountText));
            var selected = Profiles.FirstOrDefault(profile => profile.Id == preferences.LastProfileId)
                ?? Profiles.FirstOrDefault(profile => profile.Id.Equals("tekkit-1.6.4", StringComparison.OrdinalIgnoreCase))
                ?? Profiles.FirstOrDefault();

            if (selected is null)
            {
                ProfileImportStatus = "No packaged server profiles were found.";
                return;
            }

            selected.IsSelectedForBulk = true;
            SetSelectedProfileWithoutCallback(selected);
            CurrentFilesPath = selected.ServerDirectory;
            await RefreshServerFilesAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ProfileImportStatus = $"The server profiles could not be loaded: {exception.Message}";
        }
    }

    public async Task ImportServerFolderAsync(string folderPath)
    {
        try
        {
            var result = await _profileService.ImportFolderAsync(folderPath);
            ProfileImportStatus = result.Message;
            if (result.Profile is null)
            {
                return;
            }

            var profile = Profiles.FirstOrDefault(item => item.Id == result.Profile.Id);
            if (profile is null)
            {
                profile = AddProfile(result.Profile);
                OnPropertyChanged(nameof(ProfileCountText));
            }

            profile.IsSelectedForBulk = true;
            SetSelectedProfileWithoutCallback(profile);
            CurrentFilesPath = profile.ServerDirectory;
            await RefreshServerFilesAsync();
            await PersistPreferencesAsync("Profile selection saved.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ProfileImportStatus = $"The folder could not be opened: {exception.Message}";
        }
    }

    public async Task NavigateIntoAsync(ServerFileItem item)
    {
        if (!item.IsDirectory || SelectedProfile is null)
        {
            return;
        }

        CurrentFilesPath = item.FullPath;
        await RefreshServerFilesAsync();
    }

    public async Task<bool> StopForAppExitAsync() => await StopAllActiveAsync();

    private ServerSessionViewModel AddProfile(ServerProfile profile)
    {
        var session = new ServerSessionViewModel(
            profile,
            _profileValidator,
            _launchRequestFactory,
            _consoleParserFactory,
            _processServiceFactory.Create(),
            _uiDispatcher);
        session.PropertyChanged += Session_PropertyChanged;
        Profiles.Add(session);
        return session;
    }

    private void SetSelectedProfileWithoutCallback(ServerSessionViewModel profile)
    {
        _changingProfile = true;
        _selectedProfile = profile;
        OnPropertyChanged(nameof(SelectedProfile));
        _changingProfile = false;
    }

    private bool CanStartSelected() => Profiles.Any(profile => profile.IsSelectedForBulk && profile.CanStart);

    private bool CanStopSelected() => Profiles.Any(profile => profile.IsSelectedForBulk && profile.CanStop);

    private bool CanApplyUpdate() => IsUpdateReady;

    private bool CanBrowseFiles() => SelectedProfile is not null
        && Directory.Exists(SelectedProfile.ServerDirectory);

    private async Task StartSelectedAsync()
    {
        var selected = Profiles
            .Where(profile => profile.IsSelectedForBulk && profile.CanStart)
            .ToArray();
        await Task.WhenAll(selected.Select(profile => profile.StartAsync()));
    }

    private async Task StopSelectedAsync()
    {
        var selected = Profiles
            .Where(profile => profile.IsSelectedForBulk && profile.CanStop)
            .ToArray();
        await Task.WhenAll(selected.Select(profile => profile.StopAsync()));
    }

    private async Task<bool> StopAllActiveAsync()
    {
        var active = Profiles.Where(profile => profile.IsServerActive).ToArray();
        if (active.Length == 0)
        {
            return true;
        }

        var results = await Task.WhenAll(active.Select(profile => profile.StopAsync()));
        return results.All(stopped => stopped);
    }

    private async Task ApplyUpdateAsync()
    {
        if (!IsUpdateReady)
        {
            return;
        }

        if (!await StopAllActiveAsync())
        {
            UpdateStatus = "The update is downloaded, but every active server must stop safely before it can be installed.";
            return;
        }

        UpdateStatus = "Installing the update and restarting…";
        try
        {
            _appUpdateService.ApplyUpdateAndRestart();
        }
        catch (InvalidOperationException exception)
        {
            UpdateStatus = exception.Message;
        }
    }

    private async Task RefreshServerFilesAsync()
    {
        ServerFiles.Clear();
        var selected = SelectedProfile;
        if (selected is null || !Directory.Exists(selected.ServerDirectory))
        {
            FilesStatus = "The selected server folder is unavailable.";
            CanNavigateUp = false;
            RefreshFilesCommand.NotifyCanExecuteChanged();
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(CurrentFilesPath))
            {
                CurrentFilesPath = selected.ServerDirectory;
            }

            var items = await _serverFileService.GetItemsAsync(
                selected.ServerDirectory,
                CurrentFilesPath);
            foreach (var item in items)
            {
                ServerFiles.Add(item);
            }

            CanNavigateUp = _serverFileService.GetParentWithinRoot(
                selected.ServerDirectory,
                CurrentFilesPath) is not null;
            FilesStatus = items.Count == 1 ? "1 item" : $"{items.Count} items";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            FilesStatus = $"This folder could not be read: {exception.Message}";
            CanNavigateUp = false;
        }

        RefreshFilesCommand.NotifyCanExecuteChanged();
    }

    private async Task NavigateUpAsync()
    {
        var selected = SelectedProfile;
        if (selected is null)
        {
            return;
        }

        var parent = _serverFileService.GetParentWithinRoot(
            selected.ServerDirectory,
            CurrentFilesPath);
        if (parent is null)
        {
            return;
        }

        CurrentFilesPath = parent;
        await RefreshServerFilesAsync();
    }

    private Task SaveSettingsAsync() => PersistPreferencesAsync("Settings saved.");

    private async Task PersistPreferencesAsync(string successMessage)
    {
        var preferences = new AppPreferences
        {
            Theme = SelectedThemeOption?.Id ?? "System",
            AccentColor = SelectedAccentOption?.Id ?? "System",
            UpdateCheckIntervalMinutes = SelectedUpdateIntervalOption?.Minutes ?? 15,
            LastProfileId = SelectedProfile?.Id
        };

        try
        {
            await _appSettingsService.SaveAsync(preferences);
            _appUpdateService.SetCheckIntervalMinutes(preferences.UpdateCheckIntervalMinutes);
            SettingsStatus = successMessage;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SettingsStatus = $"Settings could not be saved: {exception.Message}";
        }
    }

    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ServerSessionViewModel.IsSelectedForBulk))
        {
            OnPropertyChanged(nameof(SelectedProfileCountText));
        }

        if (args.PropertyName is nameof(ServerSessionViewModel.State)
            or nameof(ServerSessionViewModel.CanStart)
            or nameof(ServerSessionViewModel.CanStop)
            or nameof(ServerSessionViewModel.IsSelectedForBulk))
        {
            OnPropertyChanged(nameof(IsServerActive));
            OnPropertyChanged(nameof(ActiveServerCountText));
            StartSelectedCommand.NotifyCanExecuteChanged();
            StopSelectedCommand.NotifyCanExecuteChanged();
        }

        if (args.PropertyName == nameof(ServerSessionViewModel.State)
            && ReferenceEquals(sender, SelectedProfile)
            && sender is ServerSessionViewModel session
            && !session.IsServerActive)
        {
            _ = RefreshServerFilesAsync();
        }
    }

    private void OnUpdateStatusChanged(object? sender, AppUpdateStatusChangedEventArgs args)
    {
        _uiDispatcher.TryEnqueue(() =>
        {
            UpdateStatus = args.Message;
            IsUpdateReady = args.State == AppUpdateState.ReadyToApply;
        });
    }
}
