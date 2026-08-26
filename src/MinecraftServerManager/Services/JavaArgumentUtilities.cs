namespace MinecraftServerManager.Services;

public static class JavaArgumentUtilities
{
    public static int? GetInitialMemoryMegabytes(IEnumerable<string> arguments) =>
        GetMemoryMegabytes(arguments, "-Xms");

    public static int? GetMaximumMemoryMegabytes(IEnumerable<string> arguments) =>
        GetMemoryMegabytes(arguments, "-Xmx");

    public static IReadOnlyList<string> ReplaceMemoryArguments(
        IEnumerable<string> existingArguments,
        int initialMemoryMegabytes,
        int maximumMemoryMegabytes,
        IEnumerable<string>? additionalArguments = null)
    {
        var preserved = existingArguments
            .Where(argument => !IsMemoryArgument(argument))
            .ToList();

        var result = new List<string>(preserved.Count + 4)
        {
            $"-Xms{FormatMemory(initialMemoryMegabytes)}",
            $"-Xmx{FormatMemory(maximumMemoryMegabytes)}"
        };
        result.AddRange(preserved);
        if (additionalArguments is not null)
        {
            foreach (var argument in additionalArguments.Where(argument => !IsMemoryArgument(argument)))
            {
                if (!result.Contains(argument, StringComparer.Ordinal))
                {
                    result.Add(argument);
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<string> WithoutMemoryArguments(IEnumerable<string> arguments) =>
        arguments.Where(argument => !IsMemoryArgument(argument)).ToArray();

    private static int? GetMemoryMegabytes(IEnumerable<string> arguments, string prefix)
    {
        var argument = arguments.LastOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (argument is null)
        {
            return null;
        }

        var value = argument[prefix.Length..].Trim();
        if (value.Length < 2 || !long.TryParse(value[..^1], out var amount) || amount <= 0)
        {
            return null;
        }

        var megabytes = char.ToUpperInvariant(value[^1]) switch
        {
            'K' => Math.Max(1, amount / 1024),
            'M' => amount,
            'G' => amount * 1024,
            _ => -1
        };

        return megabytes is > 0 and <= int.MaxValue ? (int)megabytes : null;
    }

    private static bool IsMemoryArgument(string argument) =>
        argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase)
        || argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase);

    private static string FormatMemory(int megabytes) => megabytes % 1024 == 0
        ? $"{megabytes / 1024}G"
        : $"{megabytes}M";
}
