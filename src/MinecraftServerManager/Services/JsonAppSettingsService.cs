using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class JsonAppSettingsService : IAppSettingsService
{
    private static readonly int[] SupportedIntervals = [5, 15, 30, 60, 360, 1_440];
    private static readonly string[] SupportedThemes = ["System", "Light", "Dark"];
    private static readonly string[] SupportedAccents = ["System", "Coral", "Blue", "Emerald", "Amethyst", "Amber", "Custom"];
    private static readonly string[] SupportedBackdrops = ["Mica", "Acrylic"];
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kidda.MinecraftServerManager",
        "settings.json");

    public AppPreferences Current { get; private set; } = new();

    public async Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            Current = new AppPreferences();
            return Current;
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            var preferences = await JsonSerializer.DeserializeAsync<AppPreferences>(
                stream,
                SerializerOptions,
                cancellationToken);
            Current = Normalize(preferences ?? new AppPreferences());
        }
        catch (JsonException)
        {
            Current = new AppPreferences();
        }

        return Current;
    }

    public async Task SaveAsync(
        AppPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = Normalize(preferences);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var settingsDirectory = Path.GetDirectoryName(_settingsPath)
                ?? throw new InvalidOperationException("The settings directory could not be resolved.");
            Directory.CreateDirectory(settingsDirectory);

            var temporaryPath = _settingsPath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    normalized,
                    SerializerOptions,
                    cancellationToken);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
            Current = normalized;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static AppPreferences Normalize(AppPreferences preferences)
    {
        return preferences with
        {
            UpdateCheckIntervalMinutes = SupportedIntervals.Contains(preferences.UpdateCheckIntervalMinutes)
                ? preferences.UpdateCheckIntervalMinutes
                : 15,
            Theme = SupportedThemes.Contains(preferences.Theme, StringComparer.OrdinalIgnoreCase)
                ? SupportedThemes.First(value => value.Equals(preferences.Theme, StringComparison.OrdinalIgnoreCase))
                : "System",
            AccentColor = SupportedAccents.Contains(preferences.AccentColor, StringComparer.OrdinalIgnoreCase)
                ? SupportedAccents.First(value => value.Equals(preferences.AccentColor, StringComparison.OrdinalIgnoreCase))
                : "System",
            CustomAccentColor = NormalizeAccentHex(preferences.CustomAccentColor),
            Backdrop = SupportedBackdrops.Contains(preferences.Backdrop, StringComparer.OrdinalIgnoreCase)
                ? SupportedBackdrops.First(value => value.Equals(preferences.Backdrop, StringComparison.OrdinalIgnoreCase))
                : "Mica"
        };
    }

    private static string NormalizeAccentHex(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 7
            && candidate[0] == '#'
            && uint.TryParse(
                candidate.AsSpan(1),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            return candidate.ToUpperInvariant();
        }

        return "#60CDFF";
    }
}
