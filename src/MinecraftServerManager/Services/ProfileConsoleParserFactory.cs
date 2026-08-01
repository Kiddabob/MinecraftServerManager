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

        public ServerConsoleParseResult Parse(string line, ServerOutputStream stream)
        {
            var cleanedLine = StripAnsi(line).TrimEnd();
            var signal = ServerConsoleSignal.None;
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

            var match = MatchLogLine(cleanedLine);
            if (match.Success)
            {
                var source = match.Groups["source"].Value.Trim();
                var level = ParseLevel(match.Groups["level"].Value, source, stream);
                if (signal == ServerConsoleSignal.Ready && level == ServerLogLevel.Information)
                {
                    level = ServerLogLevel.Success;
                }

                return new ServerConsoleParseResult(
                    signal,
                    new ServerLogEntry(
                        match.Groups["time"].Value,
                        level,
                        string.IsNullOrWhiteSpace(source) ? "Server" : source,
                        match.Groups["message"].Value.TrimStart()));
            }

            var fallbackLevel = stream == ServerOutputStream.StandardError
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
                    cleanedLine));
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
