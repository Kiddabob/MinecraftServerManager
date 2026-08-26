using System.Text.RegularExpressions;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ProfileConsoleParserFactory : IServerConsoleParserFactory
{
    public IServerConsoleParser Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfileConsoleParser(
            profile.ReadyPatterns,
            profile.FailurePatterns,
            profile.PlayerJoinPatterns,
            profile.PlayerLeavePatterns);
    }

    private sealed class ProfileConsoleParser : IServerConsoleParser
    {
        private static readonly Regex LegacyLogPattern = new(
            @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})\s+\[(?<level>[A-Za-z]+)\]\s*(?:\[(?<source>[^\]]+)\]\s*)?(?<message>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex ModernLogPattern = new(
            @"^\[(?<time>\d{2}:\d{2}:\d{2})\]\s+\[(?<source>[^/\]]+)/(?<level>[A-Za-z]+)\](?::\s*)?(?<message>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex AnsiEscapePattern = new(
            "\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private readonly IReadOnlyList<Regex> _readyPatterns;
        private readonly IReadOnlyList<Regex> _failurePatterns;
        private readonly IReadOnlyList<Regex> _playerJoinPatterns;
        private readonly IReadOnlyList<Regex> _playerLeavePatterns;

        public ProfileConsoleParser(
            IReadOnlyList<string> readyPatterns,
            IReadOnlyList<string> failurePatterns,
            IReadOnlyList<string> playerJoinPatterns,
            IReadOnlyList<string> playerLeavePatterns)
        {
            _readyPatterns = CompilePatterns(readyPatterns);
            _failurePatterns = CompilePatterns(failurePatterns);
            _playerJoinPatterns = CompilePatterns(playerJoinPatterns);
            _playerLeavePatterns = CompilePatterns(playerLeavePatterns);
        }

        private static IReadOnlyList<Regex> CompilePatterns(IReadOnlyList<string> patterns) =>
            patterns
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => new Regex(
                    pattern,
                    RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)))
                .ToArray();

        public ServerConsoleParseResult Parse(string line, ServerOutputStream stream)
        {
            var cleanedLine = StripAnsi(line).TrimEnd();
            var signal = ServerConsoleSignal.None;
            var playerConnection = MatchPlayerConnection(cleanedLine);
            foreach (var pattern in _readyPatterns)
            {
                try
                {
                    if (pattern.IsMatch(cleanedLine))
                    {
                        signal = ServerConsoleSignal.Ready;
                        break;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // A bad profile pattern must not interrupt console streaming.
                }
            }

            if (signal == ServerConsoleSignal.None && MatchesAny(_failurePatterns, cleanedLine))
            {
                signal = ServerConsoleSignal.Failed;
            }

            var match = MatchLogLine(cleanedLine);
            if (match.Success)
            {
                var source = match.Groups["source"].Value.Trim();
                var level = ParseLevel(match.Groups["level"].Value, source, stream);
                if (signal == ServerConsoleSignal.Ready && level == ServerLogLevel.Information)
                {
                    level = ServerLogLevel.Success;
                }
                else if (signal == ServerConsoleSignal.Failed)
                {
                    level = ServerLogLevel.Error;
                }

                return new ServerConsoleParseResult(
                    signal,
                    new ServerLogEntry(
                        match.Groups["time"].Value,
                        level,
                        string.IsNullOrWhiteSpace(source) ? "Server" : source,
                        match.Groups["message"].Value.TrimStart()),
                    playerConnection);
            }

            var fallbackLevel = stream == ServerOutputStream.StandardError
                ? ServerLogLevel.Error
                : signal == ServerConsoleSignal.Failed
                    ? ServerLogLevel.Error
                : signal == ServerConsoleSignal.Ready
                    ? ServerLogLevel.Success
                    : ServerLogLevel.Information;

            return new ServerConsoleParseResult(
                signal,
                new ServerLogEntry(
                    DateTime.Now.ToString("HH:mm:ss"),
                    fallbackLevel,
                    stream == ServerOutputStream.StandardError ? "Java" : "Server",
                    cleanedLine),
                playerConnection);
        }

        private static bool MatchesAny(IReadOnlyList<Regex> patterns, string line)
        {
            foreach (var pattern in patterns)
            {
                try
                {
                    if (pattern.IsMatch(line))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // A bad profile pattern must not interrupt console streaming.
                }
            }

            return false;
        }

        private PlayerConnectionChange? MatchPlayerConnection(string line)
        {
            var playerName = MatchPlayerName(_playerJoinPatterns, line);
            if (playerName is not null)
            {
                return new PlayerConnectionChange(playerName, PlayerConnectionKind.Joined);
            }

            playerName = MatchPlayerName(_playerLeavePatterns, line);
            return playerName is null
                ? null
                : new PlayerConnectionChange(playerName, PlayerConnectionKind.Left);
        }

        private static string? MatchPlayerName(IReadOnlyList<Regex> patterns, string line)
        {
            foreach (var pattern in patterns)
            {
                try
                {
                    var match = pattern.Match(line);
                    var playerName = match.Groups["player"].Value;
                    if (match.Success && !string.IsNullOrWhiteSpace(playerName))
                    {
                        return playerName;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // A bad profile pattern must not interrupt console streaming.
                }
            }

            return null;
        }

        private static Match MatchLogLine(string line)
        {
            try
            {
                var legacyMatch = LegacyLogPattern.Match(line);
                return legacyMatch.Success ? legacyMatch : ModernLogPattern.Match(line);
            }
            catch (RegexMatchTimeoutException)
            {
                return Match.Empty;
            }
        }

        private static string StripAnsi(string line)
        {
            try
            {
                return AnsiEscapePattern.Replace(line, string.Empty);
            }
            catch (RegexMatchTimeoutException)
            {
                return line;
            }
        }

        private static ServerLogLevel ParseLevel(
            string level,
            string source,
            ServerOutputStream stream)
        {
            if (source.Equals("STDERR", StringComparison.OrdinalIgnoreCase))
            {
                return ServerLogLevel.Error;
            }

            return level.ToUpperInvariant() switch
            {
                "WARN" or "WARNING" => ServerLogLevel.Warning,
                "ERROR" or "SEVERE" or "FATAL" => ServerLogLevel.Error,
                "SUCCESS" => ServerLogLevel.Success,
                "INFO" or "DEBUG" or "TRACE" => ServerLogLevel.Information,
                _ => stream == ServerOutputStream.StandardError
                    ? ServerLogLevel.Error
                    : ServerLogLevel.Information
            };
        }
    }
}
