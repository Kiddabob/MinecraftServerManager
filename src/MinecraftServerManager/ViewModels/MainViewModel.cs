using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class MainViewModel : BindableBase
{
    private const string InitialProfileFile = "tekkit.json";
    private const int MaximumConsoleCharacters = 250_000;

    private readonly IProfileService _profileService;
    private readonly IProfileValidator _profileValidator;
    private readonly IServerLaunchRequestFactory _launchRequestFactory;
    private readonly IServerConsoleParserFactory _consoleParserFactory;
    private readonly IServerProcessService _processService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ConcurrentQueue<PendingConsoleLine> _pendingConsoleLines = new();
    private readonly StringBuilder _consoleBuffer = new();

    private ServerProfile? _profile;
    private IServerConsoleParser? _consoleParser;
    private ServerState _state = ServerState.LoadingProfile;
    private int _consoleDrainScheduled;
    private bool _initialized;

    private string _profileName = "Loading Tekkit profile…";
    private string _profileDetails = string.Empty;
    private string _serverDirectory = string.Empty;
    private string _javaDetails = string.Empty;
    private string _stateText = "Loading profile";
    private string _statusMessage = "Reading Profiles\\tekkit.json";
    private string _validationText = string.Empty;
    private string _processInfo = "No server process";
    private string _consoleText = string.Empty;
    private string _commandText = string.Empty;
    private string _updateStatus = "Updater is starting…";
    private bool _canSendCommands;
    private bool _isUpdateReady;

    public MainViewModel(
        IProfileService profileService,
        IProfileValidator profileValidator,
        IServerLaunchRequestFactory launchRequestFactory,
        IServerConsoleParserFactory consoleParserFactory,
        IServerProcessService processService,
        IAppUpdateService appUpdateService,
        IUiDispatcher uiDispatcher)
    {
        _profileService = profileService;
        _profileValidator = profileValidator;
        _launchRequestFactory = launchRequestFactory;
        _consoleParserFactory = consoleParserFactory;
        _processService = processService;
        _appUpdateService = appUpdateService;
        _uiDispatcher = uiDispatcher;

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, CanStop);
        SendCommand = new AsyncRelayCommand(SendCommandAsync, CanSend);
        ApplyUpdateCommand = new AsyncRelayCommand(ApplyUpdateAsync, CanApplyUpdate);

        _processService.OutputReceived += OnOutputReceived;
        _processService.Exited += OnProcessExited;
        _appUpdateService.StatusChanged += OnUpdateStatusChanged;
    }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public AsyncRelayCommand ApplyUpdateCommand { get; }

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

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _appUpdateService.StartMonitoring();
        SetState(ServerState.LoadingProfile, "Reading Profiles\\tekkit.json");

        try
        {
            _profile = await _profileService.LoadAsync(InitialProfileFile);
            _consoleParser = _consoleParserFactory.Create(_profile);

            ProfileName = _profile.DisplayName;
            ProfileDetails = $"{_profile.ServerType} • Minecraft {_profile.MinecraftVersion} • Forge {_profile.ForgeVersion}";
            ServerDirectory = _profile.ServerDirectory;
            JavaDetails = $"{_profile.JavaVersion} • {_profile.JavaExecutable}";

            var validation = _profileValidator.Validate(_profile);
            ValidationText = validation.ToDisplayText();
            SetState(
                validation.IsValid ? ServerState.Stopped : ServerState.InvalidProfile,
                validation.IsValid
                    ? "Tekkit is configured and ready to start."
                    : "Update Profiles\\tekkit.json, then restart the app.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            ValidationText = exception.Message;
            SetState(ServerState.InvalidProfile, "The Tekkit profile could not be loaded.");
        }
    }

    public async Task<bool> StopForAppExitAsync()
    {
        if (!_processService.IsRunning)
        {
            return true;
        }

        return await StopCoreAsync();
    }

    private bool CanStart() => _profile is not null
        && _state is ServerState.Stopped or ServerState.Failed
        && !_processService.IsRunning;

    private bool CanStop() => _profile is not null
        && _state is ServerState.Starting or ServerState.Running or ServerState.Ready;

    private bool CanSend() => CanSendCommands && !string.IsNullOrWhiteSpace(CommandText);

    private bool CanApplyUpdate() => IsUpdateReady;

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
            SetState(ServerState.InvalidProfile, "The profile failed validation. Correct the paths and restart the app.");
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
                : $"PID {processId} • Started {startedAt.Value:yyyy-MM-dd HH:mm:ss}";

            if (_state == ServerState.Starting)
            {
                SetState(ServerState.Running, "Java is running; waiting for Tekkit's ready message.");
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
            UpdateStatus = "The update is downloaded, but Tekkit must stop safely before it can be installed.";
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
                SetState(ServerState.Stopped, "Tekkit stopped safely.");
            }
            else if (args.ExitCode == 0)
            {
                SetState(ServerState.Stopped, "The Java process exited.");
            }
            else
            {
                SetState(ServerState.Failed, $"Java exited unexpectedly with code {args.ExitCode}.");
            }
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
            SetState(ServerState.Ready, "Tekkit reported that it is ready.");
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
            ServerState.LoadingProfile => "Loading profile",
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
