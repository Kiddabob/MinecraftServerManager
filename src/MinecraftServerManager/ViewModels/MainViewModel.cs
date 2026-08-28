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
    private readonly IPlayerPlaytimeService _playerPlaytimeService;
    private readonly IJavaRuntimeService _javaRuntimeService;
    private readonly IManagedJavaRuntimeService _managedJavaRuntimeService;
    private readonly IServerLaunchRecommendationService _launchRecommendationService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly IServerFileService _serverFileService;
    private readonly IUiDispatcher _uiDispatcher;

    private ServerSessionViewModel? _selectedProfile;
    private bool _initialized;
    private bool _changingProfile;
    private string _profileImportStatus = "Choose a server folder to detect or create a profile.";
    private string _updateStatus = "Updater is starting…";
    private string _currentFilesPath = string.Empty;
    private string _filesStatus = "Select a profile to browse its server files.";
    private string _settingsStatus = "Settings are stored for this Windows account.";
    private string _javaRuntimeSummary = "Scanning for Java installations…";
    private bool _isJavaInstallInProgress;
    private string _javaInstallProgressText = string.Empty;
    private double _javaInstallProgressPercent;
    private bool _isJavaInstallProgressIndeterminate;
    private bool _canNavigateUp;
    private bool _isUpdateReady;
    private AppThemeOption? _selectedThemeOption;
    private AccentColorOption? _selectedAccentOption;
    private UpdateIntervalOption? _selectedUpdateIntervalOption;
    private PlayerScopeOption? _selectedPlayerScope;
    private string _playerSummaryText = "Tracking begins when a player joins a server started here.";
    private string? _renderedPlayerScopeId;
    private bool _loadingProfileEditor;
    private int _profileEditorLoadVersion;
    private string _profileDisplayName = string.Empty;
    private ServerFolderDetection? _selectedProfileLauncher;
    private JavaRuntimeInfo? _selectedJavaRuntime;
    private string _profileJavaExecutable = string.Empty;
    private double _profileInitialMemoryMb = 1024;
    private double _profileMaximumMemoryMb = 2048;
    private string _profileAdditionalJavaArgumentsText = string.Empty;
    private string _profileServerArgumentsText = string.Empty;
    private string _profileCompatibilityText = "Select a profile to review Java compatibility.";
    private bool _isRecommendedJavaInstallVisible;
    private string _recommendedJavaInstallText = "Install recommended Java";
    private string _profileEditorStatus = "Profile changes are stored for this Windows account.";

    public MainViewModel(
        IProfileService profileService,
        IProfileValidator profileValidator,
        IServerLaunchRequestFactory launchRequestFactory,
        IServerConsoleParserFactory consoleParserFactory,
        IServerProcessServiceFactory processServiceFactory,
        IPlayerPlaytimeService playerPlaytimeService,
        IJavaRuntimeService javaRuntimeService,
        IManagedJavaRuntimeService managedJavaRuntimeService,
        IServerLaunchRecommendationService launchRecommendationService,
        IAppUpdateService appUpdateService,
        IAppSettingsService appSettingsService,
        IServerFileService serverFileService,
        ModpackCatalogViewModel modpacks,
        ServerDashboardViewModel dashboard,
        IUiDispatcher uiDispatcher)
    {
        _profileService = profileService;
        _profileValidator = profileValidator;
        _launchRequestFactory = launchRequestFactory;
        _consoleParserFactory = consoleParserFactory;
        _processServiceFactory = processServiceFactory;
        _playerPlaytimeService = playerPlaytimeService;
        _javaRuntimeService = javaRuntimeService;
        _managedJavaRuntimeService = managedJavaRuntimeService;
        _launchRecommendationService = launchRecommendationService;
        _appUpdateService = appUpdateService;
        _appSettingsService = appSettingsService;
        _serverFileService = serverFileService;
        _uiDispatcher = uiDispatcher;
        Modpacks = modpacks;
        Dashboard = dashboard;

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
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, CanEditSelectedProfile);
        RedetectProfileCommand = new AsyncRelayCommand(RedetectProfileAsync, CanEditSelectedProfile);
        ApplyRecommendedSettingsCommand = new AsyncRelayCommand(
            ApplyRecommendedSettingsAsync,
            CanEditSelectedProfile);
        RefreshJavaRuntimesCommand = new AsyncRelayCommand(
            RefreshInstalledJavaAsync,
            () => !IsJavaInstallInProgress);

        _appUpdateService.StatusChanged += OnUpdateStatusChanged;
        _playerPlaytimeService.Changed += OnPlayerPlaytimeChanged;
    }

    public ObservableCollection<ServerSessionViewModel> Profiles { get; } = [];

    public ObservableCollection<ServerFileItem> ServerFiles { get; } = [];

    public ObservableCollection<PlayerScopeOption> PlayerScopeOptions { get; } = [];

    public ObservableCollection<PlayerPlaytimeRow> PlayerPlaytimes { get; } = [];

    public ObservableCollection<ServerFolderDetection> ProfileLauncherOptions { get; } = [];

    public ObservableCollection<JavaRuntimeInfo> JavaRuntimeOptions { get; } = [];

    public ObservableCollection<ManagedJavaRuntimeOption> ManagedJavaRuntimeOptions { get; } = [];

    public ServerDashboardViewModel Dashboard { get; }

    public ModpackCatalogViewModel Modpacks { get; }

    public IReadOnlyList<AppThemeOption> ThemeOptions { get; }

    public IReadOnlyList<AccentColorOption> AccentOptions { get; }

    public IReadOnlyList<UpdateIntervalOption> UpdateIntervalOptions { get; }

    public AsyncRelayCommand StartSelectedCommand { get; }

    public AsyncRelayCommand StopSelectedCommand { get; }

    public AsyncRelayCommand ApplyUpdateCommand { get; }

    public AsyncRelayCommand RefreshFilesCommand { get; }

    public AsyncRelayCommand NavigateUpCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public AsyncRelayCommand SaveProfileCommand { get; }

    public AsyncRelayCommand RedetectProfileCommand { get; }

    public AsyncRelayCommand ApplyRecommendedSettingsCommand { get; }

    public AsyncRelayCommand RefreshJavaRuntimesCommand { get; }

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
            _ = Dashboard.SelectProfileAsync(value);
            _ = LoadProfileEditorAsync(value);
            _ = PersistPreferencesAsync("Profile selection saved.");
        }
    }

    public string ProfileDisplayName
    {
        get => _profileDisplayName;
        set => SetProperty(ref _profileDisplayName, value);
    }

    public ServerFolderDetection? SelectedProfileLauncher
    {
        get => _selectedProfileLauncher;
        set
        {
            if (SetProperty(ref _selectedProfileLauncher, value)
                && !_loadingProfileEditor
                && value is not null)
            {
                ApplyLauncherToEditor(value);
            }
        }
    }

    public JavaRuntimeInfo? SelectedJavaRuntime
    {
        get => _selectedJavaRuntime;
        set
        {
            if (SetProperty(ref _selectedJavaRuntime, value)
                && !_loadingProfileEditor
                && value is not null)
            {
                ProfileJavaExecutable = value.ExecutablePath;
                UpdateProfileCompatibility();
            }
        }
    }

    public string ProfileJavaExecutable
    {
        get => _profileJavaExecutable;
        set
        {
            if (SetProperty(ref _profileJavaExecutable, value))
            {
                if (!_loadingProfileEditor
                    && _selectedJavaRuntime is not null
                    && !PathsEqual(_selectedJavaRuntime.ExecutablePath, value))
                {
                    _selectedJavaRuntime = null;
                    OnPropertyChanged(nameof(SelectedJavaRuntime));
                }

                UpdateProfileCompatibility();
            }
        }
    }

    public double ProfileInitialMemoryMb
    {
        get => _profileInitialMemoryMb;
        set => SetProperty(ref _profileInitialMemoryMb, value);
    }

    public double ProfileMaximumMemoryMb
    {
        get => _profileMaximumMemoryMb;
        set => SetProperty(ref _profileMaximumMemoryMb, value);
    }

    public string ProfileAdditionalJavaArgumentsText
    {
        get => _profileAdditionalJavaArgumentsText;
        set => SetProperty(ref _profileAdditionalJavaArgumentsText, value);
    }

    public string ProfileServerArgumentsText
    {
        get => _profileServerArgumentsText;
        set => SetProperty(ref _profileServerArgumentsText, value);
    }

    public string ProfileCompatibilityText
    {
        get => _profileCompatibilityText;
        private set => SetProperty(ref _profileCompatibilityText, value);
    }

    public bool IsRecommendedJavaInstallVisible
    {
        get => _isRecommendedJavaInstallVisible;
        private set => SetProperty(ref _isRecommendedJavaInstallVisible, value);
    }

    public string RecommendedJavaInstallText
    {
        get => _recommendedJavaInstallText;
        private set => SetProperty(ref _recommendedJavaInstallText, value);
    }

    public string ProfileEditorStatus
    {
        get => _profileEditorStatus;
        private set => SetProperty(ref _profileEditorStatus, value);
    }

    public bool IsProfileEditorEnabled => CanEditSelectedProfile();

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

    public PlayerScopeOption? SelectedPlayerScope
    {
        get => _selectedPlayerScope;
        set
        {
            if (SetProperty(ref _selectedPlayerScope, value))
            {
                RefreshPlayerPlaytime();
            }
        }
    }

    public string PlayerSummaryText
    {
        get => _playerSummaryText;
        private set => SetProperty(ref _playerSummaryText, value);
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
            return count == 1 ? "1 included" : $"{count} included";
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

            var loadedProfiles = await _profileService.LoadAllAsync();
            RefreshManagedJavaOptions();
            await RefreshJavaRuntimesAsync(loadedProfiles.Select(profile => profile.JavaExecutable));
            await _playerPlaytimeService.InitializeAsync(loadedProfiles);
            PlayerScopeOptions.Add(new PlayerScopeOption("all", "All servers"));
            foreach (var profile in loadedProfiles)
            {
                AddProfile(profile);
                PlayerScopeOptions.Add(new PlayerScopeOption(profile.Id, profile.DisplayName));
            }

            SelectedPlayerScope = PlayerScopeOptions[0];
            StartPlayerRefreshLoop();

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
            await Dashboard.SelectProfileAsync(selected);
            await LoadProfileEditorAsync(selected);
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
            await AcceptProfileImportAsync(result);
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

    public async Task<bool> StopForAppExitAsync()
    {
        var stopped = await StopAllActiveAsync();
        if (!stopped)
        {
            return false;
        }

        foreach (var profile in Profiles)
        {
            _playerPlaytimeService.CloseSessions(profile.Id);
        }

        await _playerPlaytimeService.FlushAsync();
        return true;
    }

    public string JavaRuntimeSummary
    {
        get => _javaRuntimeSummary;
        private set => SetProperty(ref _javaRuntimeSummary, value);
    }

    public bool IsJavaInstallInProgress
    {
        get => _isJavaInstallInProgress;
        private set
        {
            if (SetProperty(ref _isJavaInstallInProgress, value))
            {
                RefreshJavaRuntimesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public async Task AcceptProfileImportAsync(ProfileImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ProfileImportStatus = result.Message;
        if (result.Profile is null)
        {
            return;
        }

        var profile = Profiles.FirstOrDefault(item => item.Id == result.Profile.Id);
        if (profile is null)
        {
            await _playerPlaytimeService.InitializeAsync([result.Profile]);
            profile = AddProfile(result.Profile);
            PlayerScopeOptions.Add(new PlayerScopeOption(result.Profile.Id, result.Profile.DisplayName));
            OnPropertyChanged(nameof(ProfileCountText));
        }

        profile.IsSelectedForBulk = true;
        SetSelectedProfileWithoutCallback(profile);
        CurrentFilesPath = profile.ServerDirectory;
        await Dashboard.SelectProfileAsync(profile);
        await LoadProfileEditorAsync(profile);
        await RefreshServerFilesAsync();
        await PersistPreferencesAsync("Profile selection saved.");
    }

    public string JavaInstallProgressText
    {
        get => _javaInstallProgressText;
        private set => SetProperty(ref _javaInstallProgressText, value);
    }

    public double JavaInstallProgressPercent
    {
        get => _javaInstallProgressPercent;
        private set => SetProperty(ref _javaInstallProgressPercent, value);
    }

    public bool IsJavaInstallProgressIndeterminate
    {
        get => _isJavaInstallProgressIndeterminate;
        private set => SetProperty(ref _isJavaInstallProgressIndeterminate, value);
    }

    public void SetProfileJavaExecutable(string executablePath)
    {
        if (!CanEditSelectedProfile() || string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        SelectedJavaRuntime = null;
        ProfileJavaExecutable = executablePath;
        ProfileEditorStatus = "Java executable selected. Save the profile to keep this change.";
    }

    public async Task InstallManagedJavaAsync(int majorVersion)
    {
        if (IsJavaInstallInProgress)
        {
            SettingsStatus = "Another Java installation is already in progress.";
            return;
        }

        var option = ManagedJavaRuntimeOptions.FirstOrDefault(item => item.MajorVersion == majorVersion);
        if (option?.IsInstalled == true)
        {
            SettingsStatus = $"Java {majorVersion} is already managed by the app.";
            return;
        }

        var destination = majorVersion == 16
            ? "the archived Java 16 JDK (Minecraft 1.17 only)"
            : $"Java {majorVersion}";
        SettingsStatus = $"Downloading {destination} from Eclipse Adoptium and verifying it…";
        ProfileEditorStatus = SettingsStatus;
        IsJavaInstallInProgress = true;
        JavaInstallProgressPercent = 0;
        IsJavaInstallProgressIndeterminate = true;
        JavaInstallProgressText = $"Preparing Java {majorVersion}…";
        var progress = new Progress<ManagedJavaInstallProgress>(UpdateManagedJavaInstallProgress);
        try
        {
            var runtime = await _managedJavaRuntimeService.InstallAsync(majorVersion, progress);
            RefreshManagedJavaOptions();
            await RefreshJavaRuntimesAsync(
                Profiles.Select(profile => profile.Profile.JavaExecutable)
                    .Append(runtime.ExecutablePath));

            var recommendedMajor = _javaRuntimeService.GetRecommendedJavaMajor(
                SelectedProfileLauncher?.MinecraftVersion
                    ?? SelectedProfile?.Profile.MinecraftVersion
                    ?? string.Empty);
            if (recommendedMajor == majorVersion && CanEditSelectedProfile())
            {
                SelectedJavaRuntime = JavaRuntimeOptions.FirstOrDefault(candidate =>
                    PathsEqual(candidate.ExecutablePath, runtime.ExecutablePath)) ?? runtime;
                ProfileJavaExecutable = runtime.ExecutablePath;
                ProfileEditorStatus = $"Java {majorVersion} installed and selected. Save the profile to keep this runtime.";
            }

            SettingsStatus = $"Java {majorVersion} installed for {option?.MinecraftVersions ?? "compatible Minecraft servers"}.";
            UpdateProfileCompatibility();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException
                or InvalidDataException or JsonException or ArgumentException or OperationCanceledException)
        {
            SettingsStatus = $"Java {majorVersion} could not be installed: {exception.Message}";
            ProfileEditorStatus = SettingsStatus;
        }
        finally
        {
            IsJavaInstallInProgress = false;
            IsJavaInstallProgressIndeterminate = false;
        }
    }

    public Task InstallRecommendedJavaAsync()
    {
        var minecraftVersion = SelectedProfileLauncher?.MinecraftVersion
            ?? SelectedProfile?.Profile.MinecraftVersion
            ?? string.Empty;
        var majorVersion = _javaRuntimeService.GetRecommendedJavaMajor(minecraftVersion);
        majorVersion ??= SelectedProfileLauncher?.RequiredJavaMajorVersion;
        return majorVersion is null
            ? Task.CompletedTask
            : InstallManagedJavaAsync(majorVersion.Value);
    }

    private async Task LoadProfileEditorAsync(ServerSessionViewModel session)
    {
        var loadVersion = Interlocked.Increment(ref _profileEditorLoadVersion);
        _loadingProfileEditor = true;
        try
        {
            var detections = ServerFolderDetector.DetectCandidates(session.ServerDirectory);
            ProfileLauncherOptions.Clear();
            foreach (var detection in detections)
            {
                ProfileLauncherOptions.Add(detection);
            }

            var runtimes = await _javaRuntimeService.DiscoverAsync(
                detections.Select(detection => detection.JavaExecutable)
                    .Append(session.Profile.JavaExecutable));
            if (loadVersion != _profileEditorLoadVersion
                || !ReferenceEquals(session, SelectedProfile))
            {
                return;
            }

            ReplaceJavaRuntimes(runtimes);

            ProfileDisplayName = session.Profile.DisplayName;
            ProfileJavaExecutable = session.Profile.JavaExecutable;
            ProfileInitialMemoryMb = JavaArgumentUtilities.GetInitialMemoryMegabytes(
                session.Profile.JavaArguments) ?? 1024;
            ProfileMaximumMemoryMb = JavaArgumentUtilities.GetMaximumMemoryMegabytes(
                session.Profile.JavaArguments) ?? Math.Max(ProfileInitialMemoryMb, 2048);
            ProfileAdditionalJavaArgumentsText = CommandLineArgumentParser.Join(
                JavaArgumentUtilities.WithoutMemoryArguments(session.Profile.JavaArguments));
            ProfileServerArgumentsText = CommandLineArgumentParser.Join(session.Profile.ServerArguments);
            SelectedProfileLauncher = detections.FirstOrDefault(detection =>
                    detection.LaunchScript.Equals(session.Profile.LaunchScript, StringComparison.OrdinalIgnoreCase)
                    && detection.ServerJar.Equals(session.Profile.ServerJar, StringComparison.OrdinalIgnoreCase))
                ?? detections.FirstOrDefault(detection =>
                    detection.ServerJar.Equals(session.Profile.ServerJar, StringComparison.OrdinalIgnoreCase))
                ?? detections.FirstOrDefault();

            var resolvedJava = _javaRuntimeService.ResolveExecutablePath(
                session.Profile.JavaExecutable,
                session.Profile.JavaVersion);
            SelectedJavaRuntime = JavaRuntimeOptions.FirstOrDefault(runtime =>
                PathsEqual(runtime.ExecutablePath, resolvedJava));
            var recommendation = _launchRecommendationService.Recommend(
                session.ServerDirectory,
                SelectedProfileLauncher?.ServerType ?? session.Profile.ServerType,
                SelectedProfileLauncher?.MinecraftVersion ?? session.Profile.MinecraftVersion,
                SelectedProfileLauncher?.RequiredJavaMajorVersion);
            ProfileEditorStatus = session.IsServerActive
                ? "Stop this server before changing its launch settings."
                : $"{recommendation.Summary} Review the detected settings before saving.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ProfileEditorStatus = $"Launch settings could not be inspected: {exception.Message}";
        }
        finally
        {
            if (loadVersion == _profileEditorLoadVersion)
            {
                _loadingProfileEditor = false;
                UpdateProfileCompatibility();
                NotifyProfileEditorCanExecuteChanged();
            }
        }
    }

    private async Task RefreshJavaRuntimesAsync(IEnumerable<string> configuredExecutables)
    {
        var runtimes = await _javaRuntimeService.DiscoverAsync(configuredExecutables);
        ReplaceJavaRuntimes(runtimes);
    }

    private void ReplaceJavaRuntimes(IEnumerable<JavaRuntimeInfo> runtimes)
    {
        JavaRuntimeOptions.Clear();
        foreach (var runtime in runtimes)
        {
            JavaRuntimeOptions.Add(runtime);
        }

        JavaRuntimeSummary = JavaRuntimeOptions.Count switch
        {
            0 => "No usable Java installations were detected.",
            1 => "1 usable Java installation detected.",
            _ => $"{JavaRuntimeOptions.Count} usable Java installations detected."
        };
    }

    private async Task RefreshInstalledJavaAsync()
    {
        JavaRuntimeSummary = "Scanning for Java installations…";
        try
        {
            RefreshManagedJavaOptions();
            await RefreshJavaRuntimesAsync(
                Profiles.Select(profile => profile.Profile.JavaExecutable));
            SettingsStatus = JavaRuntimeSummary;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            JavaRuntimeSummary = $"Java installations could not be scanned: {exception.Message}";
            SettingsStatus = JavaRuntimeSummary;
        }
    }

    private void UpdateManagedJavaInstallProgress(ManagedJavaInstallProgress progress)
    {
        JavaInstallProgressText = progress.Message;
        IsJavaInstallProgressIndeterminate = progress.Percent is null;
        if (progress.Percent is not null)
        {
            JavaInstallProgressPercent = progress.Percent.Value;
        }

        SettingsStatus = progress.Message;
        ProfileEditorStatus = progress.Message;
    }

    private void RefreshManagedJavaOptions()
    {
        ManagedJavaRuntimeOptions.Clear();
        foreach (var option in _managedJavaRuntimeService.GetOptions())
        {
            ManagedJavaRuntimeOptions.Add(option);
        }

        UpdateRecommendedJavaInstallState();
    }

    private void ApplyLauncherToEditor(ServerFolderDetection detection)
    {
        var initialMemory = JavaArgumentUtilities.GetInitialMemoryMegabytes(
            detection.EffectiveJavaArguments);
        var maximumMemory = JavaArgumentUtilities.GetMaximumMemoryMegabytes(
            detection.EffectiveJavaArguments);
        if (initialMemory is not null)
        {
            ProfileInitialMemoryMb = initialMemory.Value;
        }

        if (maximumMemory is not null)
        {
            ProfileMaximumMemoryMb = maximumMemory.Value;
        }

        ProfileAdditionalJavaArgumentsText = CommandLineArgumentParser.Join(
            JavaArgumentUtilities.WithoutMemoryArguments(detection.EffectiveJavaArguments));
        ProfileServerArgumentsText = CommandLineArgumentParser.Join(detection.EffectiveServerArguments);
        if (Path.IsPathFullyQualified(detection.JavaExecutable)
            && File.Exists(detection.JavaExecutable))
        {
            ProfileJavaExecutable = detection.JavaExecutable;
            SelectedJavaRuntime = JavaRuntimeOptions.FirstOrDefault(runtime =>
                PathsEqual(runtime.ExecutablePath, detection.JavaExecutable));
        }

        ProfileEditorStatus = "Detected launcher settings loaded. Save the profile to keep them.";
        UpdateProfileCompatibility();
    }

    private async Task SaveProfileAsync()
    {
        var session = SelectedProfile;
        if (session is null || !CanEditSelectedProfile())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ProfileDisplayName))
        {
            ProfileEditorStatus = "Enter a profile name before saving.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ProfileJavaExecutable))
        {
            ProfileEditorStatus = "Choose a Java executable before saving.";
            return;
        }

        if (SelectedProfileLauncher is null)
        {
            ProfileEditorStatus = "Choose a detected launcher before saving.";
            return;
        }

        if (!double.IsFinite(ProfileInitialMemoryMb)
            || !double.IsFinite(ProfileMaximumMemoryMb)
            || ProfileInitialMemoryMb > 262_144
            || ProfileMaximumMemoryMb > 262_144)
        {
            ProfileEditorStatus = "Enter valid initial and maximum memory values.";
            return;
        }

        var initialMemory = (int)Math.Round(ProfileInitialMemoryMb);
        var maximumMemory = (int)Math.Round(ProfileMaximumMemoryMb);
        if (initialMemory < 256 || maximumMemory < 256 || initialMemory > maximumMemory)
        {
            ProfileEditorStatus = "Memory must be at least 256 MB, and initial memory cannot exceed maximum memory.";
            return;
        }

        var profile = session.Profile;
        profile.DisplayName = ProfileDisplayName.Trim();
        var launcher = SelectedProfileLauncher!;
        profile.ServerJar = launcher.ServerJar;
        profile.LaunchScript = launcher.LaunchScript;
        profile.ServerType = launcher.ServerType;
        profile.MinecraftVersion = launcher.MinecraftVersion;
        profile.DirectLaunchArguments = launcher.EffectiveDirectLaunchArguments;
        profile.RequiredFiles = string.IsNullOrWhiteSpace(launcher.ServerJar)
            ? []
            : [launcher.ServerJar];

        profile.JavaExecutable = ProfileJavaExecutable.Trim();
        profile.JavaVersion = SelectedJavaRuntime is null
            ? profile.JavaVersion
            : $"Java {SelectedJavaRuntime.MajorVersion}";
        profile.JavaArguments = JavaArgumentUtilities.ReplaceMemoryArguments(
            [],
            initialMemory,
            maximumMemory,
            CommandLineArgumentParser.Split(ProfileAdditionalJavaArgumentsText));
        profile.ServerArguments = CommandLineArgumentParser.Split(ProfileServerArgumentsText);

        try
        {
            await _profileService.SaveAsync(profile);
            session.RefreshProfile();
            await Dashboard.SelectProfileAsync(session);
            ProfileEditorStatus = "Profile launch settings saved.";
            OnPropertyChanged(nameof(ProfileCountText));
            OnPropertyChanged(nameof(SelectedProfile));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ProfileEditorStatus = $"Profile settings could not be saved: {exception.Message}";
        }
    }

    private Task RedetectProfileAsync()
    {
        var session = SelectedProfile;
        return session is null ? Task.CompletedTask : LoadProfileEditorAsync(session);
    }

    private Task ApplyRecommendedSettingsAsync()
    {
        var session = SelectedProfile;
        var launcher = SelectedProfileLauncher;
        if (session is null || launcher is null || !CanEditSelectedProfile())
        {
            return Task.CompletedTask;
        }

        var recommendation = _launchRecommendationService.Recommend(
            session.ServerDirectory,
            launcher.ServerType,
            launcher.MinecraftVersion,
            launcher.RequiredJavaMajorVersion);
        ProfileInitialMemoryMb = recommendation.InitialMemoryMb;
        ProfileMaximumMemoryMb = recommendation.MaximumMemoryMb;

        if (recommendation.JavaMajorVersion is { } javaMajor)
        {
            var runtime = JavaRuntimeOptions
                .Where(candidate => candidate.MajorVersion == javaMajor)
                .OrderBy(candidate => candidate.Source.StartsWith("Managed", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (runtime is not null)
            {
                SelectedJavaRuntime = runtime;
                ProfileJavaExecutable = runtime.ExecutablePath;
            }
        }

        ProfileEditorStatus = $"{recommendation.Summary} Save the profile to apply it.";
        return Task.CompletedTask;
    }

    private bool CanEditSelectedProfile() => SelectedProfile is { IsServerActive: false };

    private void UpdateProfileCompatibility()
    {
        var minecraftVersion = SelectedProfileLauncher?.MinecraftVersion
            ?? SelectedProfile?.Profile.MinecraftVersion
            ?? string.Empty;
        var runtime = SelectedJavaRuntime
            ?? _javaRuntimeService.FindKnownRuntime(ProfileJavaExecutable);
        var recommendedJava = _javaRuntimeService.GetRecommendedJavaMajor(minecraftVersion)
            ?? SelectedProfileLauncher?.RequiredJavaMajorVersion;
        ProfileCompatibilityText = recommendedJava is not null
            && _javaRuntimeService.GetRecommendedJavaMajor(minecraftVersion) is null
            ? runtime is null
                ? $"The selected JAR bytecode requires Java {recommendedJava} or newer. Select a matching runtime before starting."
                : runtime.MajorVersion < recommendedJava
                    ? $"Compatibility warning: the selected JAR requires Java {recommendedJava} or newer, but Java {runtime.MajorVersion} is selected."
                    : $"Java {runtime.MajorVersion} meets the Java {recommendedJava}-or-newer requirement detected from the selected JAR."
            : _javaRuntimeService.GetCompatibilityMessage(minecraftVersion, runtime);
        UpdateRecommendedJavaInstallState();
    }

    private void UpdateRecommendedJavaInstallState()
    {
        var minecraftVersion = SelectedProfileLauncher?.MinecraftVersion
            ?? SelectedProfile?.Profile.MinecraftVersion
            ?? string.Empty;
        var majorVersion = _javaRuntimeService.GetRecommendedJavaMajor(minecraftVersion);
        majorVersion ??= SelectedProfileLauncher?.RequiredJavaMajorVersion;
        if (majorVersion is null)
        {
            IsRecommendedJavaInstallVisible = false;
            return;
        }

        RecommendedJavaInstallText = $"Install managed Java {majorVersion} (optional)";
        IsRecommendedJavaInstallVisible = ManagedJavaRuntimeOptions.Any(option =>
            option.MajorVersion == majorVersion && !option.IsInstalled);
    }

    private void NotifyProfileEditorCanExecuteChanged()
    {
        OnPropertyChanged(nameof(IsProfileEditorEnabled));
        SaveProfileCommand.NotifyCanExecuteChanged();
        RedetectProfileCommand.NotifyCanExecuteChanged();
        ApplyRecommendedSettingsCommand.NotifyCanExecuteChanged();
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private ServerSessionViewModel AddProfile(ServerProfile profile)
    {
        var session = new ServerSessionViewModel(
            profile,
            _profileValidator,
            _launchRequestFactory,
            _consoleParserFactory,
            _processServiceFactory.Create(),
            _playerPlaytimeService,
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
            && sender is ServerSessionViewModel session)
        {
            NotifyProfileEditorCanExecuteChanged();
            ProfileEditorStatus = session.IsServerActive
                ? "Stop this server before changing its launch settings."
                : "Profile launch settings can now be edited.";
            if (!session.IsServerActive)
            {
                _ = RefreshServerFilesAsync();
            }
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

    private void OnPlayerPlaytimeChanged(object? sender, EventArgs args)
    {
        _uiDispatcher.TryEnqueue(RefreshPlayerPlaytime);
    }

    private void StartPlayerRefreshLoop()
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync())
            {
                _uiDispatcher.TryEnqueue(RefreshPlayerPlaytime);
            }
        });
    }

    private void RefreshPlayerPlaytime()
    {
        if (SelectedPlayerScope is null)
        {
            return;
        }

        var snapshots = _playerPlaytimeService.GetSnapshots();
        IEnumerable<PlayerPlaytimeRow> rows;
        if (SelectedPlayerScope.Id == "all")
        {
            rows = snapshots
                .GroupBy(snapshot => snapshot.PlayerName, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var entries = group.OrderByDescending(entry => entry.Playtime).ToArray();
                    return new PlayerPlaytimeRow(
                        entries[0].PlayerName,
                        entries.Any(entry => entry.IsOnline),
                        entries.Any(entry => entry.IsOnline) ? "Online" : "Offline",
                        string.Join(
                            "  •  ",
                            entries.Select(entry => $"{entry.ProfileName} {FormatPlaytime(entry.Playtime)}")),
                        entries.Aggregate(TimeSpan.Zero, (total, entry) => total + entry.Playtime),
                        FormatPlaytime(entries.Aggregate(TimeSpan.Zero, (total, entry) => total + entry.Playtime)),
                        entries.Any(entry => entry.IsOnline)
                            ? "Now"
                            : FormatLastSeen(entries.Max(entry => entry.LastSeenUtc)));
                });
        }
        else
        {
            rows = snapshots
                .Where(snapshot => snapshot.ProfileId.Equals(
                    SelectedPlayerScope.Id,
                    StringComparison.OrdinalIgnoreCase))
                .Select(snapshot => new PlayerPlaytimeRow(
                    snapshot.PlayerName,
                    snapshot.IsOnline,
                    snapshot.IsOnline ? "Online" : "Offline",
                    snapshot.ProfileName,
                    snapshot.Playtime,
                    FormatPlaytime(snapshot.Playtime),
                    snapshot.IsOnline ? "Now" : FormatLastSeen(snapshot.LastSeenUtc)));
        }

        var orderedRows = rows
            .OrderByDescending(row => row.IsOnline)
            .ThenByDescending(row => row.SortablePlaytime)
            .ThenBy(row => row.PlayerName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        if (!string.Equals(
            _renderedPlayerScopeId,
            SelectedPlayerScope.Id,
            StringComparison.OrdinalIgnoreCase))
        {
            _renderedPlayerScopeId = SelectedPlayerScope.Id;
            PlayerPlaytimes.Clear();
        }

        var desiredNames = orderedRows
            .Select(row => row.PlayerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = PlayerPlaytimes.Count - 1; index >= 0; index--)
        {
            if (!desiredNames.Contains(PlayerPlaytimes[index].PlayerName))
            {
                PlayerPlaytimes.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < orderedRows.Length; targetIndex++)
        {
            var desired = orderedRows[targetIndex];
            var existing = PlayerPlaytimes.FirstOrDefault(row => row.PlayerName.Equals(
                desired.PlayerName,
                StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                PlayerPlaytimes.Insert(targetIndex, desired);
                continue;
            }

            existing.UpdateFrom(desired);
            var currentIndex = PlayerPlaytimes.IndexOf(existing);
            if (currentIndex != targetIndex)
            {
                PlayerPlaytimes.Move(currentIndex, targetIndex);
            }
        }

        var onlineCount = orderedRows.Count(row => row.IsOnline);
        PlayerSummaryText = orderedRows.Length == 0
            ? "No sessions yet  •  Tracking starts with the next player join"
            : $"{orderedRows.Length:N0} players tracked  •  {onlineCount:N0} online";
    }

    private static string FormatPlaytime(TimeSpan playtime)
    {
        if (playtime.TotalDays >= 1)
        {
            return $"{(int)playtime.TotalDays}d {playtime.Hours}h {playtime.Minutes}m";
        }

        if (playtime.TotalHours >= 1)
        {
            return $"{(int)playtime.TotalHours}h {playtime.Minutes}m";
        }

        return $"{playtime.Minutes}m {playtime.Seconds}s";
    }

    private static string FormatLastSeen(DateTimeOffset? lastSeenUtc) =>
        lastSeenUtc is null
            ? "—"
            : lastSeenUtc.Value.ToLocalTime().ToString("dd MMM yyyy, HH:mm");
}
