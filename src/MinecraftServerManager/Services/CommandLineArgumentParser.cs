using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Services;

public static class CommandLineArgumentParser
{
    private static readonly Regex TokenPattern = new(
        "\\\"(?<double>[^\\\"]*)\\\"|'(?<single>[^']*)'|(?<bare>[^\\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static IReadOnlyList<string> Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        try
        {
            return TokenPattern.Matches(commandLine)
                .Select(match => match.Groups["double"].Success
                    ? match.Groups["double"].Value
                    : match.Groups["single"].Success
                        ? match.Groups["single"].Value
                        : match.Groups["bare"].Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    public static string Join(IEnumerable<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteIfNeeded));
    }

    private static string QuoteIfNeeded(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"'))
        {
            return value;
        }

        var escaped = new StringBuilder(value.Length + 2);
        escaped.Append('"');
        foreach (var character in value)
        {
            if (character == '"')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        escaped.Append('"');
        return escaped.ToString();
    }
}
