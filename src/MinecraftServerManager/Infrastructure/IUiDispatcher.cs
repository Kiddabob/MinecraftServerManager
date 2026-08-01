namespace MinecraftServerManager.Infrastructure;

public interface IUiDispatcher
{
    bool TryEnqueue(Action action);
}
