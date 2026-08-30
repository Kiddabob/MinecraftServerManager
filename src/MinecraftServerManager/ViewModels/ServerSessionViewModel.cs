using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class ServerSessionViewModel : BindableBase
{
    private const int MaximumConsoleEntries = 5_000;
    private const int MaximumResourceHistorySamples = 60;

    private readonly IServerReadinessService _serverReadinessService;
    private readonly IServerLaunchRequestFactory _launchRequestFactory;
    private readonly IServerConsoleParserFactory _consoleParserFactory;
    private readonly IServerProcessService _processService;
    private readonly IPlayerPlaytimeService _playerPlaytimeService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ConcurrentQueue<PendingConsoleLine> _pendingConsoleLines = new();

    private IServerConsoleParser _consoleParser;
    private ServerReadinessReport _readiness;
    private CancellationTokenSource? _resourceMonitorCancellation;
    private ServerState _state;
    private int _consoleDrainScheduled;
    private bool _isSelectedForBulk;
    private string _statusMessage;
    private string _validationText;
    private string _processInfo = "No server process";
    private string _commandText = string.Empty;
    private string _resourceUsageStatus = "Starts with the Java process";
    private string _cpuUsageText = "—";
    private string _workingSetText = "—";
    private string _privateMemoryText = "Private: —";
    private string _threadCountText = "—";
    private string _uptimeText = "—";

    public ServerSessionViewModel(
        ServerProfile profile,
        IServerReadinessService serverReadinessService,
        IServerLaunchRequestFactory launchRequestFactory,
        IServerConsoleParserFactory consoleParserFactory,
        IServerProcessService processService,
        IPlayerPlaytimeService playerPlaytimeService,
        IUiDispatcher uiDispatcher)
    {
        Profile = profile;
        _serverReadinessService = serverReadinessService;
        _launchRequestFactory = launchRequestFactory;
        _consoleParserFactory = consoleParserFactory;
        _processService = processService;
        _playerPlaytimeService = playerPlaytimeService;
        _uiDispatcher = uiDispatcher;
        _consoleParser = _consoleParserFactory.Create(profile);

        _readiness = _serverReadinessService.Evaluate(profile);
        _validationText = _readiness.ValidationText;
        _state = _readiness.CanStart ? ServerState.Stopped : ServerState.InvalidProfile;
        _statusMessage = GetIdleStatusMessage();

        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        StopCommand = new AsyncRelayCommand(async () => { await StopAsync(); }, () => CanStop);
        SendCommand = new AsyncRelayCommand(SendCommandAsync, CanSend);
        ListPlayersCommand = new AsyncRelayCommand(
            () => SendProfileCommandAsync(Profile.ListPlayersCommand),
            () => CanSendProfileCommand(Profile.ListPlayersCommand));
        SaveNowCommand = new AsyncRelayCommand(
            () => SendProfileCommandAsync(Profile.SaveCommand),
            () => CanSendProfileCommand(Profile.SaveCommand));
        RefreshReadinessCommand = new AsyncRelayCommand(
            () =>
            {
                RefreshProfile();
                return Task.CompletedTask;
            },
            () => !IsServerActive);

        _processService.OutputReceived += OnOutputReceived;
        _processService.Exited += OnProcessExited;
    }

    public ServerProfile Profile { get; }

    public ServerReadinessReport Readiness => _readiness;

    public ObservableCollection<ServerLogEntry> ConsoleEntries { get; } = [];

    public ObservableCollection<double> CpuHistory { get; } = [];

    public ObservableCollection<double> MemoryHistory { get; } = [];

    public string Id => Profile.Id;

    public string DisplayName => Profile.DisplayName;

    public string ServerDirectory => Profile.ServerDirectory;

    public string? ProfileIconPath => Profile.IconPath;

    public string ProfileDetails => string.IsNullOrWhiteSpace(Profile.ForgeVersion)
        ? $"{Profile.ServerType} • Minecraft {Profile.MinecraftVersion}"
        : $"{Profile.ServerType} • Minecraft {Profile.MinecraftVersion} • Forge {Profile.ForgeVersion}";

    public string JavaDetails => $"{Profile.JavaVersion} • {Profile.JavaExecutable}";

    public ServerState State => _state;

    public string StateText => _state switch
    {
        ServerState.LoadingProfile => "Loading profile",
        ServerState.InvalidProfile => "Configuration required",
        ServerState.Stopped => "Stopped",
        ServerState.Starting => "Starting",
        ServerState.Running => "Running",
        ServerState.Ready => "Ready",
        ServerState.Stopping => "Stopping",
        ServerState.Failed => "Failed",
        _ => _state.ToString()
    };

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

    public string ResourceUsageStatus
    {
        get => _resourceUsageStatus;
        private set => SetProperty(ref _resourceUsageStatus, value);
    }

    public string CpuUsageText
    {
        get => _cpuUsageText;
        private set => SetProperty(ref _cpuUsageText, value);
    }

    public string WorkingSetText
    {
        get => _workingSetText;
        private set => SetProperty(ref _workingSetText, value);
    }

    public string PrivateMemoryText
    {
        get => _privateMemoryText;
        private set => SetProperty(ref _privateMemoryText, value);
    }

    public string ThreadCountText
    {
        get => _threadCountText;
        private set => SetProperty(ref _threadCountText, value);
    }

    public string UptimeText
    {
        get => _uptimeText;
        private set => SetProperty(ref _uptimeText, value);
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

    public bool IsSelectedForBulk
    {
        get => _isSelectedForBulk;
        set => SetProperty(ref _isSelectedForBulk, value);
    }

    public bool IsServerActive => _processService.IsRunning || _state is ServerState.Starting
        or ServerState.Running
        or ServerState.Ready
        or ServerState.Stopping;

    public bool CanStart => _readiness.CanStart
        && (_state is ServerState.Stopped or ServerState.Failed)
        && !_processService.IsRunning;

    public bool CanStop => _processService.IsRunning && _state != ServerState.Stopping;

    public bool CanSendCommands => _state is ServerState.Running or ServerState.Ready;

    public bool CanBroadcast => CanSendProfileCommand(Profile.BroadcastCommandPrefix);

    public bool CanEmergencyStop => _processService.IsRunning && _state != ServerState.Stopping;

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public AsyncRelayCommand ListPlayersCommand { get; }

    public AsyncRelayCommand SaveNowCommand { get; }

    public AsyncRelayCommand RefreshReadinessCommand { get; }

    public void RefreshProfile()
    {
        _consoleParser = _consoleParserFactory.Create(Profile);
        RefreshReadiness();
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ServerDirectory));
        OnPropertyChanged(nameof(ProfileIconPath));
        OnPropertyChanged(nameof(ProfileDetails));
        OnPropertyChanged(nameof(JavaDetails));
        OnPropertyChanged(nameof(CanBroadcast));

        if (!_processService.IsRunning)
        {
            SetState(
                _readiness.CanStart ? ServerState.Stopped : ServerState.InvalidProfile,
                GetIdleStatusMessage());
        }
    }

    public async Task StartAsync()
    {
        if (!CanStart)
        {
            return;
        }

        RefreshReadiness();
        if (!_readiness.CanStart)
        {
            SetState(ServerState.InvalidProfile, _readiness.Summary);
            return;
        }

        StopResourceMonitoring();
        _playerPlaytimeService.CloseSessions(Profile.Id);
        ConsoleEntries.Clear();
        CpuHistory.Clear();
        MemoryHistory.Clear();
        AppendManagerMessage(
            $"Starting {DisplayName} with profile '{Id}'.",
            ServerLogLevel.Manager);
        ResetResourceUsage("Starting Java…");

        _consoleParser = _consoleParserFactory.Create(Profile);
        SetState(ServerState.Starting, "Starting the Java process…");

        try
        {
            await _processService.StartAsync(_launchRequestFactory.Create(Profile));

            var processId = _processService.ProcessId;
            var startedAt = _processService.StartedAt;
            ProcessInfo = processId is null || startedAt is null
                ? "Java process started"
                : $"PID {processId} • Started {startedAt.Value:dd MMM yyyy, HH:mm:ss}";
            StartResourceMonitoring();

            if (_state == ServerState.Starting)
            {
                SetState(ServerState.Running, "Java is running; waiting for the profile's ready message.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            AppendManagerMessage($"Start failed: {exception.Message}", ServerLogLevel.Error);
            ProcessInfo = "No server process";
            ResetResourceUsage("Process failed to start");
            SetState(ServerState.Failed, exception.Message);
        }
    }

    public async Task<bool> AcceptEulaAsync()
    {
        if (IsServerActive)
        {
            StatusMessage = "Stop this server before changing its EULA setting.";
            return false;
        }

        try
        {
            ApplyReadiness(await _serverReadinessService.AcceptEulaAsync(Profile));
            SetState(
                _readiness.CanStart ? ServerState.Stopped : ServerState.InvalidProfile,
                GetIdleStatusMessage());
            return _readiness.EulaState == ServerEulaState.Accepted;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            SetState(_state, $"eula.txt could not be updated: {exception.Message}");
            return false;
        }
    }

    public async Task<bool> StopAsync()
    {
        if (!_processService.IsRunning)
        {
            return true;
        }

        var failedBeforeStop = _state == ServerState.Failed;
        SetState(ServerState.Stopping, $"Sending '{Profile.StopCommand}' and waiting for the server to save and exit…");
        AppendManagerMessage($"Sending safe stop command: {Profile.StopCommand}", ServerLogLevel.Command);

        try
        {
            var stopped = await _processService.StopAsync(
                Profile.StopCommand,
                TimeSpan.FromSeconds(Profile.StopTimeoutSeconds));

            if (!stopped)
            {
                SetState(
                    failedBeforeStop ? ServerState.Failed : ServerState.Running,
                    $"The server did not exit within {Profile.StopTimeoutSeconds} seconds. It was not force-killed.");
                AppendManagerMessage(
                    "Safe stop timed out; the Java process is still running.",
                    ServerLogLevel.Warning);
            }

            return stopped;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            SetState(failedBeforeStop ? ServerState.Failed : ServerState.Running, exception.Message);
            AppendManagerMessage($"Stop failed: {exception.Message}", ServerLogLevel.Error);
            return false;
        }
    }

    public bool PrepareBroadcast()
    {
        if (!CanBroadcast)
        {
            return false;
        }

        CommandText = Profile.BroadcastCommandPrefix;
        return true;
    }

    public async Task EmergencyStopAsync()
    {
        if (!CanEmergencyStop)
        {
            return;
        }

        var failedBeforeStop = _state == ServerState.Failed;
        SetState(ServerState.Stopping, "Force-stopping Java without waiting for a world save…");
        AppendManagerMessage(
            "Emergency stop requested. Recent world changes may be lost.",
            ServerLogLevel.Warning);

        try
        {
            await _processService.ForceKillAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            SetState(failedBeforeStop ? ServerState.Failed : ServerState.Running, exception.Message);
            AppendManagerMessage($"Emergency stop failed: {exception.Message}", ServerLogLevel.Error);
        }
    }

    private bool CanSend() => CanSendCommands && !string.IsNullOrWhiteSpace(CommandText);

    private bool CanSendProfileCommand(string command) =>
        CanSendCommands && !string.IsNullOrWhiteSpace(command);

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
            AppendManagerMessage(command, ServerLogLevel.Command, "Command");
            CommandText = string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            AppendManagerMessage($"Command failed: {exception.Message}", ServerLogLevel.Error);
            StatusMessage = exception.Message;
        }
    }

    private async Task SendProfileCommandAsync(string command)
    {
        if (!CanSendProfileCommand(command))
        {
            return;
        }

        try
        {
            await _processService.SendCommandAsync(command);
            AppendManagerMessage(command, ServerLogLevel.Command, "Quick action");
        }
        catch (InvalidOperationException exception)
        {
            AppendManagerMessage($"Command failed: {exception.Message}", ServerLogLevel.Error);
            StatusMessage = exception.Message;
        }
    }

    private void OnOutputReceived(object? sender, ServerOutputEventArgs args)
    {
        var parsed = _consoleParser.Parse(args.Line, args.Stream);
        _pendingConsoleLines.Enqueue(new PendingConsoleLine(
            parsed.Entry,
            parsed.Signal == ServerConsoleSignal.Ready,
            parsed.Signal == ServerConsoleSignal.Failed,
            parsed.PlayerConnection));
        ScheduleConsoleDrain();
    }

    private void OnProcessExited(object? sender, ServerExitedEventArgs args)
    {
        _uiDispatcher.TryEnqueue(() =>
        {
            StopResourceMonitoring();
            _playerPlaytimeService.CloseSessions(Profile.Id);
            var runtime = args.ExitedAt - args.StartedAt;
            ProcessInfo = $"Last process: PID {args.ProcessId} • Exit {args.ExitCode} • Runtime {runtime:hh\\:mm\\:ss}";
            AppendManagerMessage(
                $"Java exited with code {args.ExitCode}.",
                args.ForceKillWasRequested
                    ? ServerLogLevel.Warning
                    : args.ExitCode == 0
                        ? ServerLogLevel.Manager
                        : ServerLogLevel.Error);
            ResetResourceUsage(args.ForceKillWasRequested
                ? "Server was force-stopped"
                : "Server process stopped");

            RefreshReadiness();

            if (!_readiness.CanStart)
            {
                SetState(ServerState.InvalidProfile, _readiness.Summary);
            }
            else if (args.ForceKillWasRequested)
            {
                SetState(
                    ServerState.Stopped,
                    $"{DisplayName} was force-stopped. Recent world changes may not have been saved.");
            }
            else if (args.StopWasRequested && args.ExitCode == 0)
            {
                SetState(ServerState.Stopped, $"{DisplayName} stopped safely.");
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
        var sawFailureSignal = false;
        while (_pendingConsoleLines.TryDequeue(out var pendingLine))
        {
            AppendConsoleEntry(pendingLine.Entry);
            sawReadySignal |= pendingLine.IsReadySignal;
            sawFailureSignal |= pendingLine.IsFailureSignal;
            if (pendingLine.PlayerConnection is not null)
            {
                _playerPlaytimeService.RecordConnection(Profile.Id, pendingLine.PlayerConnection);
            }
        }

        Interlocked.Exchange(ref _consoleDrainScheduled, 0);
        if (!_pendingConsoleLines.IsEmpty)
        {
            ScheduleConsoleDrain();
        }

        if (sawFailureSignal && _processService.IsRunning && _state != ServerState.Stopping)
        {
            SetState(
                ServerState.Failed,
                "The server reported a startup failure. Review the highlighted console line, then stop safely or use emergency stop if Java is stuck.");
        }
        else if (sawReadySignal && _state is ServerState.Starting or ServerState.Running)
        {
            SetState(ServerState.Ready, $"{DisplayName} reported that it is ready.");
        }
    }

    private void AppendManagerMessage(
        string message,
        ServerLogLevel level,
        string source = "Manager")
    {
        AppendConsoleEntry(new ServerLogEntry(
            DateTime.Now.ToString("HH:mm:ss"),
            level,
            source,
            level == ServerLogLevel.Command ? $"> {message}" : message));
    }

    private void AppendConsoleEntry(ServerLogEntry entry)
    {
        ConsoleEntries.Add(entry);
        while (ConsoleEntries.Count > MaximumConsoleEntries)
        {
            ConsoleEntries.RemoveAt(0);
        }
    }

    private void StartResourceMonitoring()
    {
        StopResourceMonitoring();
        ResourceUsageStatus = "Live • updates every second";

        var cancellation = new CancellationTokenSource();
        _resourceMonitorCancellation = cancellation;
        _ = Task.Run(() => MonitorResourceUsageAsync(cancellation.Token), cancellation.Token);
    }

    private void StopResourceMonitoring()
    {
        var cancellation = _resourceMonitorCancellation;
        _resourceMonitorCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task MonitorResourceUsageAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            QueueResourceUsageUpdate(cancellationToken);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                QueueResourceUsageUpdate(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal when the process exits or a profile restarts.
        }
    }

    private void QueueResourceUsageUpdate(CancellationToken cancellationToken)
    {
        var usage = _processService.GetResourceUsage();
        if (usage is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _uiDispatcher.TryEnqueue(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ApplyResourceUsage(usage);
            }
        });
    }

    private void ApplyResourceUsage(ServerResourceUsage usage)
    {
        CpuUsageText = $"{usage.CpuPercent:0.0}%";
        WorkingSetText = FormatBytes(usage.WorkingSetBytes);
        PrivateMemoryText = $"Private: {FormatBytes(usage.PrivateMemoryBytes)}";
        ThreadCountText = usage.ThreadCount.ToString("N0");
        UptimeText = FormatUptime(usage.Uptime);
        AppendResourceSample(CpuHistory, usage.CpuPercent);
        AppendResourceSample(MemoryHistory, usage.WorkingSetBytes / 1024d / 1024d);
    }

    private static void AppendResourceSample(ObservableCollection<double> history, double value)
    {
        history.Add(value);
        while (history.Count > MaximumResourceHistorySamples)
        {
            history.RemoveAt(0);
        }
    }

    private void ResetResourceUsage(string status)
    {
        ResourceUsageStatus = status;
        CpuUsageText = "—";
        WorkingSetText = "—";
        PrivateMemoryText = "Private: —";
        ThreadCountText = "—";
        UptimeText = "—";
    }

    private static string FormatBytes(long bytes)
    {
        const double megabyte = 1024d * 1024d;
        const double gigabyte = 1024d * 1024d * 1024d;
        return bytes >= gigabyte
            ? $"{bytes / gigabyte:0.00} GB"
            : $"{bytes / megabyte:0} MB";
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        return uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}"
            : $"{uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
    }

    private void SetState(ServerState state, string statusMessage)
    {
        _state = state;
        StatusMessage = statusMessage;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsServerActive));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanSendCommands));
        OnPropertyChanged(nameof(CanBroadcast));
        OnPropertyChanged(nameof(CanEmergencyStop));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        ListPlayersCommand.NotifyCanExecuteChanged();
        SaveNowCommand.NotifyCanExecuteChanged();
        RefreshReadinessCommand.NotifyCanExecuteChanged();
    }

    private void RefreshReadiness()
    {
        ApplyReadiness(_serverReadinessService.Evaluate(Profile));
    }

    private void ApplyReadiness(ServerReadinessReport readiness)
    {
        _readiness = readiness;
        ValidationText = _readiness.ValidationText;
        OnPropertyChanged(nameof(Readiness));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    private string GetIdleStatusMessage() => _readiness.CanStart
        ? $"{Profile.DisplayName} is configured and ready to start."
        : _readiness.Summary;

    private readonly record struct PendingConsoleLine(
        ServerLogEntry Entry,
        bool IsReadySignal,
        bool IsFailureSignal,
        PlayerConnectionChange? PlayerConnection);
}
