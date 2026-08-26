using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class JavaServerLaunchRequestFactory : IServerLaunchRequestFactory
{
    private readonly IJavaRuntimeService _javaRuntimeService;

    public JavaServerLaunchRequestFactory(IJavaRuntimeService javaRuntimeService)
    {
        _javaRuntimeService = javaRuntimeService;
    }

    public ServerLaunchRequest Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        IReadOnlyList<string> arguments;
        if (profile.DirectLaunchArguments.Count > 0)
        {
            var directArguments = profile.DirectLaunchArguments.ToList();
            var mainArgumentFileIndex = directArguments.FindIndex(argument =>
                argument.Contains("win_args", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("unix_args", StringComparison.OrdinalIgnoreCase));
            if (mainArgumentFileIndex < 0)
            {
                mainArgumentFileIndex = directArguments.Count;
            }

            directArguments.InsertRange(mainArgumentFileIndex, profile.JavaArguments);
            directArguments.AddRange(profile.ServerArguments);
            arguments = directArguments;
        }
        else
        {
            var jarArguments = new List<string>(profile.JavaArguments.Count + profile.ServerArguments.Count + 2);
            jarArguments.AddRange(profile.JavaArguments);
            jarArguments.Add("-jar");
            jarArguments.Add(profile.ServerJar);
            jarArguments.AddRange(profile.ServerArguments);
            arguments = jarArguments;
        }

        return new ServerLaunchRequest
        {
            ExecutablePath = _javaRuntimeService.ResolveExecutablePath(
                profile.JavaExecutable,
                profile.JavaVersion),
            WorkingDirectory = profile.ServerDirectory,
            Arguments = arguments
        };
    }
}
