using System.ComponentModel;
using System.Diagnostics;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ServerProcessService : IServerProcessService, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _inputGate = new(1, 1);

    private Process? _process;
    private TaskCompletionSource<int>? _exitCompletion;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastCpuSampleAt;
    private TimeSpan _lastProcessorTime;
    private int? _processId;
    private bool _stopWasRequested;
    private bool _forceKillWasRequested;

    public event EventHandler<ServerOutputEventArgs>? OutputReceived;

    public event EventHandler<ServerExitedEventArgs>? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return IsProcessRunning(_process);
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_sync)
            {
                return _processId;
            }
        }
    }

    public DateTimeOffset? StartedAt
    {
        get
        {
            lock (_sync)
            {
                return _startedAt;
            }
        }
    }

    public ServerResourceUsage? GetResourceUsage()
    {
        lock (_sync)
        {
            if (!IsProcessRunning(_process) || _startedAt is null)
            {
                return null;
            }

            try
            {
                var process = _process!;
                process.Refresh();

                var sampledAt = DateTimeOffset.UtcNow;
                var processorTime = process.TotalProcessorTime;
                var cpuPercent = 0d;
                if (_lastCpuSampleAt is not null)
                {
                    var elapsedMilliseconds = (sampledAt - _lastCpuSampleAt.Value).TotalMilliseconds;
                    var processorMilliseconds = (processorTime - _lastProcessorTime).TotalMilliseconds;
                    if (elapsedMilliseconds > 0)
                    {
                        cpuPercent = processorMilliseconds
                            / (elapsedMilliseconds * Environment.ProcessorCount)
                            * 100d;
                    }
                }

                _lastCpuSampleAt = sampledAt;
                _lastProcessorTime = processorTime;

                return new ServerResourceUsage(
                    Math.Clamp(cpuPercent, 0d, 100d),
                    process.WorkingSet64,
                    process.PrivateMemorySize64,
                    process.Threads.Count,
                    DateTimeOffset.Now - _startedAt.Value);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or ObjectDisposedException
                    or Win32Exception)
            {
                return null;
            }
        }
    }

    public async Task StartAsync(ServerLaunchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _lifecycleGate.WaitAsync(cancellationToken);

        try
        {
            lock (_sync)
            {
                if (IsProcessRunning(_process))
                {
                    throw new InvalidOperationException("A server process is already running.");
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = request.ExecutablePath,
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += OnOutputDataReceived;
            process.ErrorDataReceived += OnErrorDataReceived;
            process.Exited += OnProcessExited;

            var exitCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _process = process;
                _exitCompletion = exitCompletion;
                _stopWasRequested = false;
                _forceKillWasRequested = false;
                _startedAt = DateTimeOffset.Now;
                _processId = null;
            }

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Java did not start a process.");
                }

                lock (_sync)
                {
                    _processId = process.Id;
                    _lastCpuSampleAt = DateTimeOffset.UtcNow;
                    _lastProcessorTime = process.TotalProcessorTime;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception or IOException)
            {
                CleanupFailedStart(process);
                throw new InvalidOperationException($"Unable to start the Java server: {exception.Message}", exception);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            Process process;
            lock (_sync)
            {
                process = IsProcessRunning(_process)
                    ? _process!
                    : throw new InvalidOperationException("The server process is not running.");
            }

            await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new InvalidOperationException("The server console input stream is no longer available.", exception);
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public async Task<bool> StopAsync(
        string stopCommand,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stopCommand);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Stop timeout must be positive.");
        }

        Task<int>? exitTask;
        lock (_sync)
        {
            if (!IsProcessRunning(_process))
            {
                return true;
            }

            _stopWasRequested = true;
            exitTask = _exitCompletion?.Task;
        }

        await SendCommandAsync(stopCommand, cancellationToken);
        if (exitTask is null)
        {
            return !IsRunning;
        }

        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completedTask = await Task.WhenAny(exitTask, timeoutTask);
        if (completedTask == exitTask)
        {
            await exitTask;
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    public async Task ForceKillAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        Task<int>? exitTask;
        Process process;

        try
        {
            lock (_sync)
            {
                process = IsProcessRunning(_process)
                    ? _process!
                    : throw new InvalidOperationException("The server process is not running.");
                _stopWasRequested = true;
                _forceKillWasRequested = true;
                exitTask = _exitCompletion?.Task;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                if (IsRunning)
                {
                    throw new InvalidOperationException(
                        $"Unable to force-stop the Java process: {exception.Message}",
                        exception);
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (exitTask is not null)
        {
            await exitTask.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
        {
            try
            {
                await StopAsync("stop", TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Application shutdown must not turn a failed stop attempt into a crash.
            }
        }

        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
            _exitCompletion = null;
            _processId = null;
            _startedAt = null;
            _lastCpuSampleAt = null;
            _lastProcessorTime = TimeSpan.Zero;
            _stopWasRequested = false;
            _forceKillWasRequested = false;
        }

        if (process is not null)
        {
            DetachProcessEvents(process);
            process.Dispose();
        }

        _lifecycleGate.Dispose();
        _inputGate.Dispose();
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null)
        {
            OutputReceived?.Invoke(
                this,
                new ServerOutputEventArgs(args.Data, ServerOutputStream.StandardOutput));
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null)
        {
            OutputReceived?.Invoke(
                this,
                new ServerOutputEventArgs(args.Data, ServerOutputStream.StandardError));
        }
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (sender is not Process process)
        {
            return;
        }

        int exitCode;
        try
        {
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        int processId;
        DateTimeOffset startedAt;
        bool stopWasRequested;
        bool forceKillWasRequested;
        TaskCompletionSource<int>? exitCompletion;

        lock (_sync)
        {
            if (!ReferenceEquals(_process, process))
            {
                return;
            }

            processId = _processId ?? 0;
            startedAt = _startedAt ?? DateTimeOffset.Now;
            stopWasRequested = _stopWasRequested;
            forceKillWasRequested = _forceKillWasRequested;
            exitCompletion = _exitCompletion;

            _process = null;
            _exitCompletion = null;
            _processId = null;
            _startedAt = null;
            _lastCpuSampleAt = null;
            _lastProcessorTime = TimeSpan.Zero;
            _stopWasRequested = false;
            _forceKillWasRequested = false;
        }

        exitCompletion?.TrySetResult(exitCode);
        DetachProcessEvents(process);
        process.Dispose();

        Exited?.Invoke(
            this,
            new ServerExitedEventArgs(
                processId,
                exitCode,
                startedAt,
                DateTimeOffset.Now,
                stopWasRequested,
                forceKillWasRequested));
    }

    private void CleanupFailedStart(Process process)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _exitCompletion = null;
                _processId = null;
                _startedAt = null;
                _lastCpuSampleAt = null;
                _lastProcessorTime = TimeSpan.Zero;
                _stopWasRequested = false;
                _forceKillWasRequested = false;
            }
        }

        DetachProcessEvents(process);
        process.Dispose();
    }

    private void DetachProcessEvents(Process process)
    {
        process.OutputDataReceived -= OnOutputDataReceived;
        process.ErrorDataReceived -= OnErrorDataReceived;
        process.Exited -= OnProcessExited;
    }

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null)
        {
            return false;
        }

        try
        {
            return !process.HasExited;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
