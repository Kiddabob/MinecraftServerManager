using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class MainViewModel : BindableBase
{
    private const int MaximumConsoleCharacters = 250_000;

    private readonly IProfileService _profileService;
    private readonly IProfileValidator _profileValidator;
    private readonly IServerLaunchRequestFactory _launchRequestFactory;
    private readonly IServerConsoleParserFactory _consoleParserFactory;
    private readonly IServerProcessService _processService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly IServerFileService _serverFileService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ConcurrentQueue<PendingConsoleLine> _pendingConsoleLines = new();
    private readonly StringBuilder _consoleBuffer = new();

    private ServerProfile? _profile;
    private ServerProfile? _selectedProfile;
    private IServerConsoleParser? _consoleParser;
    private ServerState _state = ServerState.LoadingProfile;
    private int _consoleDrainScheduled;
    private bool _initialized;
    private bool _changingProfile;

    private string _profileName = "Loading profiles…";
    private string _profileDetails = string.Empty;
    private string _profileImportStatus = "Choose a Tekkit server folder to detect or create a profile.";
    private string _serverDirectory = string.Empty;
    private string _javaDetails = string.Empty;
    private string _stateText = "Loading profiles";
    private string _statusMessage = "Reading available server profiles";
    private string _validationText = string.Empty;
    private string _processInfo = "No server process";
    private string _consoleText = string.Empty;
    private string _commandText = string.Empty;
    private string _updateStatus = "Updater is starting…";
    private string _currentFilesPath = string.Empty;
    private string _filesStatus = "Select a profile to browse its server files.";
    private string _settingsStatus = "Settings are stored for this Windows account.";
    private bool _canNavigateUp;
    private bool _canSendCommands;
    private bool _isUpdateReady;
    private AppThemeOption? _selectedThemeOption;
    private AccentColorOption? _selectedAccentOption;
    private UpdateIntervalOption? _selectedUpdateIntervalOption;

    public MainViewModel(
        IProfileService profileService,
        IProfileValidator profileValidator,
        IServerLaunchRequestFactory launchRequestFactory,
        IServerConsoleParserFactory consoleParserFactory,
        IServerProcessService processService,
        IAppUpdateService appUpdateService,
        IAppSettingsService appSettingsService,
        IServerFileService serverFileService,
        IUiDispatcher uiDispatcher)
    {
        _profileService = profileService;
        _profileValidator = profileValidator;
        _launchRequestFactory = launchRequestFactory;
        _consoleParserFactory = consoleParserFactory;
        _processService = processService;
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

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, CanStop);
        SendCommand = new AsyncRelayCommand(SendCommandAsync, CanSend);
        ApplyUpdateCommand = new AsyncRelayCommand(ApplyUpdateAsync, CanApplyUpdate);
        RefreshFilesCommand = new AsyncRelayCommand(RefreshServerFilesAsync, CanBrowseFiles);
        NavigateUpCommand = new AsyncRelayCommand(NavigateUpAsync, () => CanNavigateUp);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);

        _processService.OutputReceived += OnOutputReceived;
        _processService.Exited += OnProcessExited;
        _appUpdateService.StatusChanged += OnUpdateStatusChanged;
    }

    public ObservableCollection<ServerProfile> Profiles { get; } = [];

    public ObservableCollection<ServerFileItem> ServerFiles { get; } = [];

    public IReadOnlyList<AppThemeOption> ThemeOptions { get; }

    public IReadOnlyList<AccentColorOption> AccentOptions { get; }

    public IReadOnlyList<UpdateIntervalOption> UpdateIntervalOptions { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public AsyncRelayCommand ApplyUpdateCommand { get; }

    public AsyncRelayCommand RefreshFilesCommand { get; }

    public AsyncRelayCommand NavigateUpCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public ServerProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_changingProfile || value is null || ReferenceEquals(_selectedProfile, value))
            {
                return;
            }

            if (IsServerActive)
            {
                OnPropertyChanged();
                return;
            }

            _selectedProfile = value;
            OnPropertyChanged();
            _ = ApplySelectedProfileAsync(value, persistSelection: true);
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

    public string ProfileName
    {
        get => _profileName;
        private set => SetProperty(ref _profileName, value);
    }

    public string ProfileDetails
    {
        get => _profileDetails;
        private set => SetProperty(ref _profileDetails, value);
    }

    public string ProfileImportStatus
    {
        get => _profileImportStatus;
        private set => SetProperty(ref _profileImportStatus, value);
    }

    public string ServerDirectory
    {
        get => _serverDirectory;
        private set => SetProperty(ref _serverDirectory, value);
    }

    public string JavaDetails
    {
        get => _javaDetails;
        private set => SetProperty(ref _javaDetails, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ValidationText
    {
        get => _validationText;
        private set => SetProperty(ref _validationText, value);
    }

    public string ProcessInfo
    {
        get => _processInfo;
        private set => SetProperty(ref _processInfo, value);
    }

    public string ConsoleText
    {
        get => _consoleText;
        private set => SetProperty(ref _consoleText, value);
    }

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (SetProperty(ref _commandText, value))
            {
                SendCommand.NotifyCanExecuteChanged();
            }
        }
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

    public bool CanSendCommands
    {
        get => _canSendCommands;
        private set => SetProperty(ref _canSendCommands, value);
    }

    public bool IsServerActive => _state is ServerState.Starting
        or ServerState.Running
        or ServerState.Ready
        or ServerState.Stopping;

    public bool CanSelectProfile => !IsServerActive;

    public string ProfileCountText => Profiles.Count == 1
        ? "1 server profile"
        : $"{Profiles.Count} server profiles";

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        SetState(ServerState.LoadingProfile, "Reading available server profiles");

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
                Profiles.Add(profile);
            }

            OnPropertyChanged(nameof(ProfileCountText));
            var selected = Profiles.FirstOrDefault(profile => profile.Id == preferences.LastProfileId)
                ?? Profiles.FirstOrDefault(profile => profile.Id.Equals("tekkit-1.6.4", StringComparison.OrdinalIgnoreCase))
                ?? Profiles.FirstOrDefault();

            if (selected is null)
            {
                ValidationText = "No packaged server profiles were found.";
                SetState(ServerState.InvalidProfile, "Add a profile definition before starting a server.");
                return;
            }

            SetSelectedProfileWithoutCallback(selected);
            await ApplySelectedProfileAsync(selected, persistSelection: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ValidationText = exception.Message;
            SetState(ServerState.InvalidProfile, "The server profiles could not be loaded.");
        }
    }

    public async Task ImportServerFolderAsync(string folderPath)
    {
        if (IsServerActive)
        {
            ProfileImportStatus = "Stop the active server before changing profiles.";
            return;
        }

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
                profile = result.Profile;
                Profiles.Add(profile);
                OnPropertyChanged(nameof(ProfileCountText));
            }

            SetSelectedProfileWithoutCallback(profile);
            await ApplySelectedProfileAsync(profile, persistSelection: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ProfileImportStatus = $"The folder could not be opened: {exception.Message}";
        }
    }

    public async Task NavigateIntoAsync(ServerFileItem item)
    {
        if (!item.IsDirectory || _profile is null)
        {
            return;
        }

        CurrentFilesPath = item.FullPath;
        await RefreshServerFilesAsync();
    }

    public async Task<bool> StopForAppExitAsync()
    {
        if (!_processService.IsRunning)
        {
            return true;
        }

        return await StopCoreAsync();
    }

    private async Task ApplySelectedProfileAsync(ServerProfile profile, bool persistSelection)
    {
        _profile = profile;
        _consoleParser = _consoleParserFactory.Create(profile);

        ProfileName = profile.DisplayName;
        ProfileDetails = $"{profile.ServerType} • Minecraft {profile.MinecraftVersion} • Forge {profile.ForgeVersion}";
        ServerDirectory = profile.ServerDirectory;
        JavaDetails = $"{profile.JavaVersion} • {profile.JavaExecutable}";
        CurrentFilesPath = profile.ServerDirectory;

        var validation = _profileValidator.Validate(profile);
        ValidationText = validation.ToDisplayText();
        SetState(
            validation.IsValid ? ServerState.Stopped : ServerState.InvalidProfile,
            validation.IsValid
                ? $"{profile.DisplayName} is configured and ready to start."
                : "Select the correct server folder or update the profile paths.");

        await RefreshServerFilesAsync();
        if (persistSelection)
        {
            await PersistPreferencesAsync("Profile selection saved.");
        }
    }

    private void SetSelectedProfileWithoutCallback(ServerProfile profile)
    {
        _changingProfile = true;
        _selectedProfile = profile;
        OnPropertyChanged(nameof(SelectedProfile));
        _changingProfile = false;
    }

    private bool CanStart() => _profile is not null
        && _state is ServerState.Stopped or ServerState.Failed
        && !_processService.IsRunning;

    private bool CanStop() => _profile is not null
        && _state is ServerState.Starting or ServerState.Running or ServerState.Ready;

    private bool CanSend() => CanSendCommands && !string.IsNullOrWhiteSpace(CommandText);

    private bool CanApplyUpdate() => IsUpdateReady;

    private bool CanBrowseFiles() => _profile is not null && Directory.Exists(_profile.ServerDirectory);

    private async Task StartAsync()
    {
        if (_profile is null)
        {
            return;
        }

        var validation = _profileValidator.Validate(_profile);
        ValidationText = validation.ToDisplayText();
        if (!validation.IsValid)
        {
            SetState(ServerState.InvalidProfile, "The profile failed validation. Select the correct server folder.");
            return;
        }

        _consoleBuffer.Clear();
        ConsoleText = string.Empty;
        AppendConsoleLine(new PendingConsoleLine(
            $"Starting {_profile.DisplayName} with profile '{_profile.Id}'.",
            ServerOutputStream.StandardOutput,
            false,
            true));

        _consoleParser = _consoleParserFactory.Create(_profile);
        SetState(ServerState.Starting, "Starting the Java process…");

        try
        {
            await _processService.StartAsync(_launchRequestFactory.Create(_profile));

            var processId = _processService.ProcessId;
            var startedAt = _processService.StartedAt;
            ProcessInfo = processId is null || startedAt is null
                ? "Java process started"
                : $"PID {processId} • Started {startedAt.Value:dd MMM yyyy, HH:mm:ss}";

            if (_state == ServerState.Starting)
            {
                SetState(ServerState.Running, "Java is running; waiting for the profile's ready message.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            AppendSystemMessage($"Start failed: {exception.Message}", isError: true);
            ProcessInfo = "No server process";
            SetState(ServerState.Failed, exception.Message);
        }
    }

    private Task StopAsync() => StopCoreAsync();

    private async Task<bool> StopCoreAsync()
    {
        if (_profile is null || !_processService.IsRunning)
        {
            return true;
        }

        SetState(ServerState.Stopping, $"Sending '{_profile.StopCommand}' and waiting for the server to save and exit…");
        AppendSystemMessage($"Sending safe stop command: {_profile.StopCommand}");

        try
        {
            var stopped = await _processService.StopAsync(
                _profile.StopCommand,
                TimeSpan.FromSeconds(_profile.StopTimeoutSeconds));

            if (!stopped)
            {
                SetState(
                    ServerState.Running,
                    $"The server did not exit within {_profile.StopTimeoutSeconds} seconds. It was not force-killed.");
                AppendSystemMessage("Safe stop timed out; the Java process is still running.", isError: true);
            }

            return stopped;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            SetState(ServerState.Running, exception.Message);
            AppendSystemMessage($"Stop failed: {exception.Message}", isError: true);
            return false;
        }
    }

    private async Task SendCommandAsync()
    {
        var command = CommandText.Trim();
        if (command.Length == 0)
        {
            return;
        }

        try
        {
            await _processService.SendCommandAsync(command);
            AppendSystemMessage($"> {command}");
            CommandText = string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            AppendSystemMessage($"Command failed: {exception.Message}", isError: true);
            StatusMessage = exception.Message;
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (!IsUpdateReady)
        {
            return;
        }

        if (_processService.IsRunning && !await StopCoreAsync())
        {
            UpdateStatus = "The update is downloaded, but the server must stop safely before it can be installed.";
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
        if (_profile is null || !Directory.Exists(_profile.ServerDirectory))
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
                CurrentFilesPath = _profile.ServerDirectory;
            }

            var items = await _serverFileService.GetItemsAsync(
                _profile.ServerDirectory,
                CurrentFilesPath);
            foreach (var item in items)
            {
                ServerFiles.Add(item);
            }

            CanNavigateUp = _serverFileService.GetParentWithinRoot(
                _profile.ServerDirectory,
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
        if (_profile is null)
        {
            return;
        }

        var parent = _serverFileService.GetParentWithinRoot(
            _profile.ServerDirectory,
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
            LastProfileId = _profile?.Id
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

    private void OnOutputReceived(object? sender, ServerOutputEventArgs args)
    {
        var isReady = _consoleParser?.Parse(args.Line) == ServerConsoleSignal.Ready;
        _pendingConsoleLines.Enqueue(new PendingConsoleLine(args.Line, args.Stream, isReady, false));
        ScheduleConsoleDrain();
    }

    private void OnProcessExited(object? sender, ServerExitedEventArgs args)
    {
        _uiDispatcher.TryEnqueue(() =>
        {
            var runtime = args.ExitedAt - args.StartedAt;
            ProcessInfo = $"Last process: PID {args.ProcessId} • Exit {args.ExitCode} • Runtime {runtime:hh\\:mm\\:ss}";
            AppendSystemMessage($"Java exited with code {args.ExitCode}.", args.ExitCode != 0);

            if (args.StopWasRequested && args.ExitCode == 0)
            {
                SetState(ServerState.Stopped, $"{_profile?.DisplayName ?? "Server"} stopped safely.");
            }
            else if (args.ExitCode == 0)
            {
                SetState(ServerState.Stopped, "The Java process exited.");
            }
            else
            {
                SetState(ServerState.Failed, $"Java exited unexpectedly with code {args.ExitCode}.");
            }

            _ = RefreshServerFilesAsync();
        });
    }

    private void OnUpdateStatusChanged(object? sender, AppUpdateStatusChangedEventArgs args)
    {
        _uiDispatcher.TryEnqueue(() =>
        {
            UpdateStatus = args.Message;
            IsUpdateReady = args.State == AppUpdateState.ReadyToApply;
        });
    }

    private void ScheduleConsoleDrain()
    {
        if (Interlocked.Exchange(ref _consoleDrainScheduled, 1) != 0)
        {
            return;
        }

        if (!_uiDispatcher.TryEnqueue(DrainConsoleQueue))
        {
            Interlocked.Exchange(ref _consoleDrainScheduled, 0);
        }
    }

    private void DrainConsoleQueue()
    {
        var sawReadySignal = false;
        while (_pendingConsoleLines.TryDequeue(out var pendingLine))
        {
            AppendConsoleLine(pendingLine);
            sawReadySignal |= pendingLine.IsReadySignal;
        }

        Interlocked.Exchange(ref _consoleDrainScheduled, 0);
        if (!_pendingConsoleLines.IsEmpty)
        {
            ScheduleConsoleDrain();
        }

        if (sawReadySignal && _state is ServerState.Starting or ServerState.Running)
        {
            SetState(ServerState.Ready, $"{_profile?.DisplayName ?? "Server"} reported that it is ready.");
        }
    }

    private void AppendSystemMessage(string message, bool isError = false)
    {
        AppendConsoleLine(new PendingConsoleLine(
            message,
            isError ? ServerOutputStream.StandardError : ServerOutputStream.StandardOutput,
            false,
            true));
    }

    private void AppendConsoleLine(PendingConsoleLine pendingLine)
    {
        var prefix = pendingLine.IsSystemMessage
            ? "[manager] "
            : pendingLine.Stream == ServerOutputStream.StandardError
                ? "[stderr] "
                : string.Empty;

        _consoleBuffer.Append('[')
            .Append(DateTime.Now.ToString("HH:mm:ss"))
            .Append("] ")
            .Append(prefix)
            .AppendLine(pendingLine.Line);

        if (_consoleBuffer.Length > MaximumConsoleCharacters)
        {
            var removeCount = _consoleBuffer.Length - MaximumConsoleCharacters;
            var nextLineBreak = IndexOfLineBreak(_consoleBuffer, removeCount);
            _consoleBuffer.Remove(0, nextLineBreak < 0 ? removeCount : nextLineBreak + Environment.NewLine.Length);
        }

        ConsoleText = _consoleBuffer.ToString();
    }

    private void SetState(ServerState state, string statusMessage)
    {
        _state = state;
        StateText = state switch
        {
            ServerState.LoadingProfile => "Loading profiles",
            ServerState.InvalidProfile => "Configuration required",
            ServerState.Stopped => "Stopped",
            ServerState.Starting => "Starting",
            ServerState.Running => "Running",
            ServerState.Ready => "Ready",
            ServerState.Stopping => "Stopping",
            ServerState.Failed => "Failed",
            _ => state.ToString()
        };
        StatusMessage = statusMessage;
        CanSendCommands = state is ServerState.Running or ServerState.Ready;
        OnPropertyChanged(nameof(IsServerActive));
        OnPropertyChanged(nameof(CanSelectProfile));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    private static int IndexOfLineBreak(StringBuilder builder, int startIndex)
    {
        for (var index = Math.Max(0, startIndex); index < builder.Length; index++)
        {
            if (builder[index] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private readonly record struct PendingConsoleLine(
        string Line,
        ServerOutputStream Stream,
        bool IsReadySignal,
        bool IsSystemMessage);
}
