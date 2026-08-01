using Microsoft.UI.Dispatching;

namespace MinecraftServerManager.Infrastructure;

public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherQueueUiDispatcher()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The UI dispatcher is not available on the current thread.");
    }

    public bool TryEnqueue(Action action) => _dispatcherQueue.TryEnqueue(() => action());
}
