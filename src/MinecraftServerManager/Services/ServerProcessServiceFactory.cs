namespace MinecraftServerManager.Services;

public sealed class ServerProcessServiceFactory : IServerProcessServiceFactory
{
    public IServerProcessService Create() => new ServerProcessService();
}
