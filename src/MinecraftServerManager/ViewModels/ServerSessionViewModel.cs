using System.Collections.Concurrent;
using System.Text;
using MinecraftServerManager.Infrastructure;
using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

namespace MinecraftServerManager.ViewModels;

public sealed class ServerSessionViewModel : BindableBase
{
    private const int MaximumConsoleCharacters = 250_000;

    private readonly IProfileValidator _profileValidator;
    private readonly IServerLaunchRequestFactory _launchRequestFactory;
    private readonly IServerConsoleParserFactory _consoleParserFactory;
    private readonly IServerProcessService _processService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ConcurrentQueue<PendingConsoleLine> _pendingConsoleLines = new();
    private readonly StringBuilder _consoleBuffer = new();

    private IServerConsoleParser _consoleParser;
    private ServerState _state;
    private int _consoleDrainScheduled;
    private bool _isSelectedForBulk;
    private string _statusMessage;
    private string _validationText;
    private string _processInfo = "No server process";
    private string _consoleText = string.Empty;
    private string _commandText = string.Empty;

    public ServerSessionViewModel(
        ServerProfile profile,
        IProfileValidator profileValidator,
        IServerLaunchRequestFactory launchRequestFactory,
        IServerConsoleParserFactory consoleParserFactory,
        IServerProcessService processService,
        IUiDispatcher uiDispatcher)
    {
        Profile = profile;
        _profileValidator = profileValidator;
        _launchRequestFactory = launchRequestFactory;
        _consoleParserFactory = consoleParserFactory;
        _processService = processService;
        _uiDispatcher = uiDispatcher;
        _consoleParser = _consoleParserFactory.Create(profile);

        var validation = _profileValidator.Validate(profile);
        _validationText = validation.ToDisplayText();
        _state = validation.IsValid ? ServerState.Stopped : ServerState.InvalidProfile;
        _statusMessage = validation.IsValid
            ? $"{profile.DisplayName} is configured and ready to start."
            : "Select the correct server folder or update the profile paths.";

        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        StopCommand = new AsyncRelayCommand(async () => { await StopAsync(); }, () => CanStop);
        SendCommand = new AsyncRelayCommand(SendCommandAsync, CanSend);

        _processService.OutputReceived += OnOutputReceived;
        _processService.Exited += OnProcessExited;
    }

    public ServerProfile Profile { get; }

    public string Id => Profile.Id;

    public string DisplayName => Profile.DisplayName;

    public string ServerDirectory => Profile.ServerDirectory;

    public string ProfileDetails =>
        $"{Profile.ServerType} • Minecraft {Profile.MinecraftVersion} • Forge {Profile.ForgeVersion}";

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

    public bool IsSelectedForBulk
    {
        get => _isSelectedForBulk;
        set => SetProperty(ref _isSelectedForBulk, value);
    }

    public bool IsServerActive => _state is ServerState.Starting
        or ServerState.Running
        or ServerState.Ready
        or ServerState.Stopping;

    public bool CanStart => _state is ServerState.Stopped or ServerState.Failed
        && !_processService.IsRunning;

    public bool CanStop => _state is ServerState.Starting or ServerState.Running or ServerState.Ready;

    public bool CanSendCommands => _state is ServerState.Running or ServerState.Ready;

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public async Task StartAsync()
    {
        if (!CanStart)
        {
            return;
        }

        var validation = _profileValidator.Validate(Profile);
        ValidationText = validation.ToDisplayText();
        if (!validation.IsValid)
        {
            SetState(ServerState.InvalidProfile, "The profile failed validation. Select the correct server folder.");
            return;
        }

        _consoleBuffer.Clear();
        ConsoleText = string.Empty;
        AppendConsoleLine(new PendingConsoleLine(
            $"Starting {DisplayName} with profile '{Id}'.",
            ServerOutputStream.StandardOutput,
            false,
            true));

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

    public async Task<bool> StopAsync()
    {
        if (!_processService.IsRunning)
        {
            return true;
        }

        SetState(ServerState.Stopping, $"Sending '{Profile.StopCommand}' and waiting for the server to save and exit…");
        AppendSystemMessage($"Sending safe stop command: {Profile.StopCommand}");

        try
        {
            var stopped = await _processService.StopAsync(
                Profile.StopCommand,
                TimeSpan.FromSeconds(Profile.StopTimeoutSeconds));

            if (!stopped)
            {
                SetState(
                    ServerState.Running,
                    $"The server did not exit within {Profile.StopTimeoutSeconds} seconds. It was not force-killed.");
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

    private bool CanSend() => CanSendCommands && !string.IsNullOrWhiteSpace(CommandText);

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

    private void OnOutputReceived(object? sender, ServerOutputEventArgs args)
    {
        var isReady = _consoleParser.Parse(args.Line) == ServerConsoleSignal.Ready;
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
            SetState(ServerState.Ready, $"{DisplayName} reported that it is ready.");
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
        StatusMessage = statusMessage;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsServerActive));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanSendCommands));
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
