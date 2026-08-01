using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class JavaServerLaunchRequestFactory : IServerLaunchRequestFactory
{
    public ServerLaunchRequest Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var arguments = new List<string>(profile.JavaArguments.Count + profile.ServerArguments.Count + 2);
        arguments.AddRange(profile.JavaArguments);
        arguments.Add("-jar");
        arguments.Add(profile.ServerJar);
        arguments.AddRange(profile.ServerArguments);

        return new ServerLaunchRequest
        {
            ExecutablePath = profile.JavaExecutable,
            WorkingDirectory = profile.ServerDirectory,
            Arguments = arguments
        };
    }
}
