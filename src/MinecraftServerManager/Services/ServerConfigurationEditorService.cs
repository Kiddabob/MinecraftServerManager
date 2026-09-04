using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class ServerConfigurationEditorService : IServerConfigurationEditorService
{
    private const int MaximumFriendlyFields = 750;

    private static readonly Regex ForgeValuePattern = new(
        @"^(?<indent>\s*)(?<type>[BIDS]):(?<key>[^=<>]+?)(?<separator>\s*=\s*)(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PropertyValuePattern = new(
        @"^(?<indent>\s*)(?<key>[^#!;\s][^=:]*?)(?<separator>\s*[=:]\s*)(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JsonValuePattern = new(
        """^(?<indent>\s*)"(?<key>(?:\\.|[^"\\])+)"(?<separator>\s*:\s*)(?<value>true|false|null|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|"(?:\\.|[^"\\])*")(?<suffix>\s*,?\s*)$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex YamlValuePattern = new(
        @"^(?<indent>\s*)(?<key>[^#\s][^:]*?)(?<separator>\s*:\s+)(?<value>.*?)(?<suffix>\s+#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MinimumPattern = new(
        @"\bmin(?:imum)?\s*(?:value\s*)?[:=]?\s*(?<value>-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MaximumPattern = new(
        @"\bmax(?:imum)?\s*(?:value\s*)?[:=]?\s*(?<value>-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex WordBoundaryPattern = new(
        @"(?<=[a-z0-9])(?=[A-Z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyList<ServerConfigurationFieldDefinition> StandardServerPropertiesDefinitions =
    [
        IntegerDefinition("server-port", "Server port", 1, 65_535,
            "The network port used by Minecraft clients."),
        IntegerDefinition("query.port", "Query port", 1, 65_535,
            "The network port used by the GameSpy query listener."),
        IntegerDefinition("rcon.port", "RCON port", 1, 65_535,
            "The network port used by remote console clients."),
        IntegerDefinition("max-players", "Maximum players", 1, int.MaxValue,
            "The greatest number of players allowed online at once."),
        IntegerDefinition("view-distance", "View distance", 3, 32,
            "The server-side chunk radius sent to each player."),
        IntegerDefinition("simulation-distance", "Simulation distance", 3, 32,
            "The chunk radius in which entities and game ticks are simulated."),
        IntegerDefinition("spawn-protection", "Spawn protection radius", 0, int.MaxValue,
            "The protected radius around world spawn. Use 0 to disable it."),
        IntegerDefinition("player-idle-timeout", "Idle timeout", 0, int.MaxValue,
            "Minutes before an idle player is removed. Use 0 to disable it."),
        IntegerDefinition("max-world-size", "Maximum world size", 1, 29_999_984,
            "The maximum world radius, measured in blocks."),
        IntegerDefinition("op-permission-level", "Operator permission level", 1, 4,
            "The command permission level granted to server operators."),
        IntegerDefinition("function-permission-level", "Function permission level", 1, 4,
            "The command permission level used by functions."),
        IntegerDefinition("entity-broadcast-range-percentage", "Entity broadcast range", 10, 1_000,
            "Percentage multiplier applied to the normal entity broadcast range."),
        IntegerDefinition("network-compression-threshold", "Network compression threshold", -1, int.MaxValue,
            "Packet size in bytes before compression is used. Use -1 to disable compression."),
        IntegerDefinition("rate-limit", "Connection rate limit", 0, int.MaxValue,
            "Maximum packets per second before a connection is removed. Use 0 to disable it.")
    ];

    public ServerConfigurationFriendlyDocument Parse(
        ServerProfile profile,
        ServerConfigurationFile file,
        string content)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);

        var format = GetFormat(file.Name);
        if (format == ConfigurationTextFormat.Unsupported)
        {
            return Unsupported(content, file.ExtensionText);
        }

        var definitions = GetDefinitions(profile, file);
        var fields = new List<ServerConfigurationField>();
        var comments = new List<string>();
        var sections = new Stack<string>();
        var currentOffset = 0;

        foreach (var line in EnumerateLines(content))
        {
            var trimmed = line.Text.Trim();
            if (TryReadComment(trimmed, out var comment))
            {
                if (!string.IsNullOrWhiteSpace(comment) && comment.Any(character => character != '#'))
                {
                    comments.Add(comment);
                }

                currentOffset += line.TotalLength;
                continue;
            }

            if (format == ConfigurationTextFormat.Forge && TryUpdateForgeSection(trimmed, sections))
            {
                comments.Clear();
                currentOffset += line.TotalLength;
                continue;
            }

            if (format == ConfigurationTextFormat.Properties && TryReadIniSection(trimmed, sections))
            {
                comments.Clear();
                currentOffset += line.TotalLength;
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                comments.Clear();
                currentOffset += line.TotalLength;
                continue;
            }

            if (fields.Count < MaximumFriendlyFields
                && TryReadValue(format, line.Text, out var token))
            {
                var sectionPath = sections.Count == 0
                    ? string.Empty
                    : string.Join('.', sections.Reverse());
                var keyPath = string.IsNullOrEmpty(sectionPath)
                    ? token.Key
                    : $"{sectionPath}.{token.Key}";
                var definition = FindDefinition(definitions, keyPath, token.Key);
                var description = !string.IsNullOrWhiteSpace(definition?.Description)
                    ? definition.Description
                    : CleanDescription(comments);
                var field = CreateField(
                    file,
                    token,
                    keyPath,
                    sectionPath,
                    description,
                    definition,
                    currentOffset);
                if (field is not null)
                {
                    fields.Add(field);
                }
            }

            comments.Clear();
            currentOffset += line.TotalLength;
        }

        if (fields.Count == 0)
        {
            return Unsupported(
                content,
                "No safely editable scalar settings were found in this file. Use Text Editor for lists, tables, and other complex structures.");
        }

        var limitSuffix = fields.Count == MaximumFriendlyFields
            ? $" The first {MaximumFriendlyFields:N0} settings are shown to keep the editor responsive."
            : string.Empty;
        var schemaSuffix = definitions.Count > 0
            ? " Known server guidance is combined with limits declared in file comments."
            : " Limits are shown only when the file declares them; other values are marked as having no declared limit.";
        return new ServerConfigurationFriendlyDocument(
            content,
            fields,
            fields.Count == 1 ? "1 friendly setting" : $"{fields.Count:N0} friendly settings",
            $"Changes update the underlying text without rearranging comments or unrelated settings.{schemaSuffix}{limitSuffix}");
    }

    public string Apply(ServerConfigurationFriendlyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var invalid = document.Fields.FirstOrDefault(field => !field.IsValid);
        if (invalid is not null)
        {
            throw new InvalidDataException($"{invalid.DisplayName}: {invalid.ValidationText}");
        }

        var builder = new StringBuilder(document.SourceText);
        foreach (var field in document.Fields.OrderByDescending(field => field.ValueStartOffset))
        {
            builder.Remove(field.ValueStartOffset, field.ValueLength);
            builder.Insert(field.ValueStartOffset, Serialize(field));
        }

        return builder.ToString();
    }

    private static ServerConfigurationField? CreateField(
        ServerConfigurationFile file,
        ParsedValue token,
        string keyPath,
        string sectionPath,
        string commentDescription,
        ServerConfigurationFieldDefinition? definition,
        int lineOffset)
    {
        var rawValue = DecodeValue(token.RawValue, token.ValueEncoding);
        var kind = ResolveKind(token.TypeHint, rawValue, definition);
        var options = BuildOptions(definition, rawValue);
        if (options.Count > 0)
        {
            kind = ServerConfigurationFieldKind.Choice;
        }

        var minimum = definition?.Minimum ?? ReadDeclaredLimit(commentDescription, MinimumPattern);
        var maximum = definition?.Maximum ?? ReadDeclaredLimit(commentDescription, MaximumPattern);
        var step = definition?.Step
            ?? (kind == ServerConfigurationFieldKind.Integer ? 1d : 0.1d);
        var displayName = !string.IsNullOrWhiteSpace(definition?.DisplayName)
            ? definition.DisplayName
            : Humanize(token.Key);
        var section = string.IsNullOrWhiteSpace(sectionPath)
            ? file.SourceName
            : Humanize(sectionPath);
        var choicePresentation = definition?.Presentation.Equals("Radio", StringComparison.OrdinalIgnoreCase) == true
            ? ServerConfigurationChoicePresentation.Radio
            : ServerConfigurationChoicePresentation.DropDown;

        var booleanValue = false;
        var numericValue = 0d;
        var textValue = rawValue;
        ServerConfigurationChoiceOption? selectedOption = null;
        switch (kind)
        {
            case ServerConfigurationFieldKind.Boolean:
                if (!bool.TryParse(rawValue, out booleanValue))
                {
                    return null;
                }

                break;
            case ServerConfigurationFieldKind.Integer:
            case ServerConfigurationFieldKind.Number:
                if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out numericValue))
                {
                    return null;
                }

                break;
            case ServerConfigurationFieldKind.Choice:
                selectedOption = options.First(option => option.Value.Equals(rawValue, StringComparison.OrdinalIgnoreCase));
                break;
        }

        return new ServerConfigurationField(
            keyPath,
            displayName,
            section,
            commentDescription,
            kind,
            choicePresentation,
            minimum,
            maximum,
            step,
            options,
            booleanValue,
            numericValue,
            textValue,
            selectedOption,
            BuildLimitsText(kind, minimum, maximum, options),
            lineOffset + token.ValueStart,
            token.ValueLength,
            token.ValueEncoding);
    }

    private static IReadOnlyList<ServerConfigurationChoiceOption> BuildOptions(
        ServerConfigurationFieldDefinition? definition,
        string currentValue)
    {
        if (definition?.Options.Count is not > 0)
        {
            return [];
        }

        var options = definition.Options
            .Select(option => new ServerConfigurationChoiceOption(
                option.Value,
                string.IsNullOrWhiteSpace(option.DisplayName) ? option.Value : option.DisplayName))
            .ToList();
        if (!options.Any(option => option.Value.Equals(currentValue, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new ServerConfigurationChoiceOption(currentValue, $"Current value ({currentValue})"));
        }

        return options;
    }

    private static ServerConfigurationFieldKind ResolveKind(
        char? typeHint,
        string rawValue,
        ServerConfigurationFieldDefinition? definition)
    {
        if (!string.IsNullOrWhiteSpace(definition?.Kind))
        {
            return definition.Kind.ToLowerInvariant() switch
            {
                "boolean" => ServerConfigurationFieldKind.Boolean,
                "integer" => ServerConfigurationFieldKind.Integer,
                "number" or "decimal" => ServerConfigurationFieldKind.Number,
                "choice" => ServerConfigurationFieldKind.Choice,
                _ => ServerConfigurationFieldKind.Text
            };
        }

        return typeHint switch
        {
            'B' => ServerConfigurationFieldKind.Boolean,
            'I' => ServerConfigurationFieldKind.Integer,
            'D' => ServerConfigurationFieldKind.Number,
            'S' => ServerConfigurationFieldKind.Text,
            _ when bool.TryParse(rawValue, out _) => ServerConfigurationFieldKind.Boolean,
            _ when long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => ServerConfigurationFieldKind.Integer,
            _ when double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => ServerConfigurationFieldKind.Number,
            _ => ServerConfigurationFieldKind.Text
        };
    }

    private static string Serialize(ServerConfigurationField field)
    {
        var value = field.Kind switch
        {
            ServerConfigurationFieldKind.Boolean => field.BooleanValue ? "true" : "false",
            ServerConfigurationFieldKind.Integer => Math.Truncate(field.NumericValue).ToString("0", CultureInfo.InvariantCulture),
            ServerConfigurationFieldKind.Number => field.NumericValue.ToString("0.################", CultureInfo.InvariantCulture),
            ServerConfigurationFieldKind.Choice => field.SelectedOption!.Value,
            _ => field.TextValue
        };

        return field.ValueEncoding switch
        {
            ServerConfigurationValueEncoding.JsonString => JsonSerializer.Serialize(value),
            ServerConfigurationValueEncoding.SingleQuoted => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'",
            ServerConfigurationValueEncoding.DoubleQuoted => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            _ => value
        };
    }

    private static string BuildLimitsText(
        ServerConfigurationFieldKind kind,
        double? minimum,
        double? maximum,
        IReadOnlyList<ServerConfigurationChoiceOption> options)
    {
        if (kind == ServerConfigurationFieldKind.Boolean)
        {
            return "Allowed values: On or Off";
        }

        if (kind == ServerConfigurationFieldKind.Choice)
        {
            return $"Allowed values: {string.Join(", ", options.Select(option => option.DisplayName))}";
        }

        if (kind is ServerConfigurationFieldKind.Integer or ServerConfigurationFieldKind.Number)
        {
            return (minimum, maximum) switch
            {
                (not null, not null) => $"Allowed range: {FormatLimit(minimum.Value)} to {FormatLimit(maximum.Value)}",
                (not null, null) => $"Minimum: {FormatLimit(minimum.Value)} • no declared maximum",
                (null, not null) => $"Maximum: {FormatLimit(maximum.Value)} • no declared minimum",
                _ => "No minimum or maximum declared by this configuration"
            };
        }

        return "No length or value list declared by this configuration";
    }

    private static string FormatLimit(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static double? ReadDeclaredLimit(string description, Regex pattern)
    {
        var match = pattern.Match(description);
        return match.Success
            && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static IReadOnlyDictionary<string, ServerConfigurationFieldDefinition> GetDefinitions(
        ServerProfile profile,
        ServerConfigurationFile file)
    {
        var definitions = new Dictionary<string, ServerConfigurationFieldDefinition>(
            StringComparer.OrdinalIgnoreCase);
        if (file.Name.Equals("server.properties", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var definition in StandardServerPropertiesDefinitions)
            {
                definitions[definition.Key] = definition;
            }
        }

        var normalizedRelativePath = file.RelativePath.Replace('\\', '/');
        var schema = profile.ConfigurationSchemas.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate.FilePattern)
            && (FileSystemName.MatchesSimpleExpression(candidate.FilePattern, file.Name, ignoreCase: true)
                || FileSystemName.MatchesSimpleExpression(
                    candidate.FilePattern.Replace('\\', '/'),
                    normalizedRelativePath,
                    ignoreCase: true)));
        if (schema is not null)
        {
            foreach (var definition in schema.Fields.Where(field => !string.IsNullOrWhiteSpace(field.Key)))
            {
                definitions[definition.Key] = definition;
            }
        }

        return definitions;
    }

    private static ServerConfigurationFieldDefinition IntegerDefinition(
        string key,
        string displayName,
        double minimum,
        double maximum,
        string description) => new()
        {
            Key = key,
            DisplayName = displayName,
            Description = description,
            Kind = "Integer",
            Minimum = minimum,
            Maximum = maximum,
            Step = 1
        };

    private static ServerConfigurationFieldDefinition? FindDefinition(
        IReadOnlyDictionary<string, ServerConfigurationFieldDefinition> definitions,
        string keyPath,
        string key)
    {
        if (definitions.TryGetValue(keyPath, out var pathDefinition))
        {
            return pathDefinition;
        }

        return definitions.TryGetValue(key, out var keyDefinition) ? keyDefinition : null;
    }

    private static bool TryReadValue(
        ConfigurationTextFormat format,
        string line,
        out ParsedValue token)
    {
        return format switch
        {
            ConfigurationTextFormat.Forge => TryMatchValue(line, ForgeValuePattern, isForge: true, out token)
                || TryMatchValue(line, PropertyValuePattern, isForge: false, out token),
            ConfigurationTextFormat.Json => TryMatchJsonValue(line, out token),
            ConfigurationTextFormat.Yaml => TryMatchYamlValue(line, out token),
            _ => TryMatchValue(line, PropertyValuePattern, isForge: false, out token)
        };
    }

    private static bool TryMatchValue(string line, Regex pattern, bool isForge, out ParsedValue token)
    {
        var match = pattern.Match(line);
        if (!match.Success)
        {
            token = default;
            return false;
        }

        var valueGroup = match.Groups["value"];
        var leadingWhitespace = valueGroup.Value.Length - valueGroup.Value.TrimStart().Length;
        var trailingWhitespace = valueGroup.Value.Length - valueGroup.Value.TrimEnd().Length;
        var value = valueGroup.Value.Trim();
        if (string.IsNullOrEmpty(value) && isForge)
        {
            token = default;
            return false;
        }

        var typeHint = isForge ? match.Groups["type"].Value[0] : (char?)null;
        var encoding = DetectQuotedEncoding(value);
        token = new ParsedValue(
            match.Groups["key"].Value.Trim(),
            value,
            valueGroup.Index + leadingWhitespace,
            valueGroup.Length - leadingWhitespace - trailingWhitespace,
            typeHint,
            encoding);
        return true;
    }

    private static bool TryMatchJsonValue(string line, out ParsedValue token)
    {
        var match = JsonValuePattern.Match(line);
        if (!match.Success || match.Groups["value"].Value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            token = default;
            return false;
        }

        var value = match.Groups["value"];
        token = new ParsedValue(
            JsonSerializer.Deserialize<string>($"\"{match.Groups["key"].Value}\"") ?? match.Groups["key"].Value,
            value.Value,
            value.Index,
            value.Length,
            null,
            value.Value.StartsWith('"') ? ServerConfigurationValueEncoding.JsonString : ServerConfigurationValueEncoding.Raw);
        return true;
    }

    private static bool TryMatchYamlValue(string line, out ParsedValue token)
    {
        var match = YamlValuePattern.Match(line);
        if (!match.Success)
        {
            token = default;
            return false;
        }

        var valueGroup = match.Groups["value"];
        var rawValue = valueGroup.Value.Trim();
        if (string.IsNullOrEmpty(rawValue)
            || rawValue is "|" or ">"
            || rawValue.StartsWith('[')
            || rawValue.StartsWith('{'))
        {
            token = default;
            return false;
        }

        var leadingWhitespace = valueGroup.Value.Length - valueGroup.Value.TrimStart().Length;
        var trailingWhitespace = valueGroup.Value.Length - valueGroup.Value.TrimEnd().Length;
        token = new ParsedValue(
            match.Groups["key"].Value.Trim(),
            rawValue,
            valueGroup.Index + leadingWhitespace,
            valueGroup.Length - leadingWhitespace - trailingWhitespace,
            null,
            DetectQuotedEncoding(rawValue));
        return true;
    }

    private static ServerConfigurationValueEncoding DetectQuotedEncoding(string value)
    {
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            return ServerConfigurationValueEncoding.DoubleQuoted;
        }

        return value.Length >= 2 && value.StartsWith('\'') && value.EndsWith('\'')
            ? ServerConfigurationValueEncoding.SingleQuoted
            : ServerConfigurationValueEncoding.Raw;
    }

    private static string DecodeValue(string value, ServerConfigurationValueEncoding encoding)
    {
        return encoding switch
        {
            ServerConfigurationValueEncoding.JsonString => JsonSerializer.Deserialize<string>(value) ?? string.Empty,
            ServerConfigurationValueEncoding.SingleQuoted => value[1..^1].Replace("''", "'", StringComparison.Ordinal),
            ServerConfigurationValueEncoding.DoubleQuoted => value[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal),
            _ => value
        };
    }

    private static bool TryReadComment(string trimmed, out string comment)
    {
        foreach (var marker in new[] { "#", "//", ";" })
        {
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                comment = trimmed[marker.Length..].Trim();
                return true;
            }
        }

        comment = string.Empty;
        return false;
    }

    private static bool TryUpdateForgeSection(string trimmed, Stack<string> sections)
    {
        if (trimmed == "}")
        {
            if (sections.Count > 0)
            {
                sections.Pop();
            }

            return true;
        }

        if (trimmed.EndsWith('{'))
        {
            var section = trimmed[..^1].Trim();
            if (!string.IsNullOrWhiteSpace(section))
            {
                sections.Push(section);
            }

            return true;
        }

        return false;
    }

    private static bool TryReadIniSection(string trimmed, Stack<string> sections)
    {
        if (trimmed.Length < 3 || !trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
        {
            return false;
        }

        sections.Clear();
        sections.Push(trimmed[1..^1].Trim());
        return true;
    }

    private static string CleanDescription(IReadOnlyList<string> comments) =>
        string.Join(' ', comments.Where(comment => !string.IsNullOrWhiteSpace(comment))).Trim();

    private static string Humanize(string value)
    {
        var spaced = WordBoundaryPattern.Replace(value, " ")
            .Replace('.', ' ')
            .Replace('-', ' ')
            .Replace('_', ' ');
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(spaced.ToLower(CultureInfo.CurrentCulture));
    }

    private static ConfigurationTextFormat GetFormat(string name)
    {
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".cfg" or ".conf" => ConfigurationTextFormat.Forge,
            ".properties" or ".ini" or ".config" or ".txt" => ConfigurationTextFormat.Properties,
            ".json" or ".json5" => ConfigurationTextFormat.Json,
            ".yml" or ".yaml" => ConfigurationTextFormat.Yaml,
            _ => ConfigurationTextFormat.Unsupported
        };
    }

    private static ServerConfigurationFriendlyDocument Unsupported(string sourceText, string reason) =>
        new(sourceText, [], "Text Editor required", reason);

    private static IEnumerable<ConfigurationLine> EnumerateLines(string content)
    {
        var offset = 0;
        while (offset < content.Length)
        {
            var lineEnd = offset;
            while (lineEnd < content.Length && content[lineEnd] is not '\r' and not '\n')
            {
                lineEnd++;
            }

            var newlineLength = 0;
            if (lineEnd < content.Length)
            {
                newlineLength = content[lineEnd] == '\r'
                    && lineEnd + 1 < content.Length
                    && content[lineEnd + 1] == '\n'
                        ? 2
                        : 1;
            }

            yield return new ConfigurationLine(
                content[offset..lineEnd],
                lineEnd - offset + newlineLength);
            offset = lineEnd + newlineLength;
        }

        if (content.Length == 0)
        {
            yield return new ConfigurationLine(string.Empty, 0);
        }
    }

    private readonly record struct ConfigurationLine(string Text, int TotalLength);

    private readonly record struct ParsedValue(
        string Key,
        string RawValue,
        int ValueStart,
        int ValueLength,
        char? TypeHint,
        ServerConfigurationValueEncoding ValueEncoding);

    private enum ConfigurationTextFormat
    {
        Unsupported,
        Properties,
        Forge,
        Json,
        Yaml
    }
}
