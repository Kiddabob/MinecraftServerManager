using System.Text.RegularExpressions;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ProfileConsoleParserFactory : IServerConsoleParserFactory
{
    public IServerConsoleParser Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfileConsoleParser(profile.ReadyPatterns);
    }

    private sealed class ProfileConsoleParser : IServerConsoleParser
    {
        private readonly IReadOnlyList<Regex> _readyPatterns;

        public ProfileConsoleParser(IReadOnlyList<string> readyPatterns)
        {
            _readyPatterns = readyPatterns
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => new Regex(
                    pattern,
                    RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)))
                .ToArray();
        }

        public ServerConsoleSignal Parse(string line)
        {
            foreach (var pattern in _readyPatterns)
            {
                try
                {
                    if (pattern.IsMatch(line))
                    {
                        return ServerConsoleSignal.Ready;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // A bad profile pattern must not interrupt console streaming.
                }
            }

            return ServerConsoleSignal.None;
        }
    }
}
