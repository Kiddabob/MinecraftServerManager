using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class JsonWindowPlacementService : IWindowPlacementService
{
    private const int MaximumCoordinateMagnitude = 100_000;
    private const int MaximumDimension = 32_768;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _placementPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kidda.MinecraftServerManager",
        "window-placement.json");

    public WindowPlacement? Current { get; private set; }

    public WindowPlacement? Load()
    {
        if (!File.Exists(_placementPath))
        {
            Current = null;
            return null;
        }

        try
        {
            var placement = JsonSerializer.Deserialize<WindowPlacement>(
                File.ReadAllText(_placementPath),
                SerializerOptions);
            Current = Normalize(placement);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Current = null;
        }

        return Current;
    }

    public async Task SaveAsync(
        WindowPlacement placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var normalized = Normalize(placement)
            ?? throw new ArgumentOutOfRangeException(nameof(placement), "Window placement is outside the supported range.");

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var placementDirectory = Path.GetDirectoryName(_placementPath)
                ?? throw new InvalidOperationException("The placement directory could not be resolved.");
            Directory.CreateDirectory(placementDirectory);

            var temporaryPath = _placementPath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    normalized,
                    SerializerOptions,
                    cancellationToken);
            }

            File.Move(temporaryPath, _placementPath, overwrite: true);
            Current = normalized;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static WindowPlacement? Normalize(WindowPlacement? placement)
    {
        if (placement is null
            || Math.Abs((long)placement.X) > MaximumCoordinateMagnitude
            || Math.Abs((long)placement.Y) > MaximumCoordinateMagnitude
            || placement.Width <= 0
            || placement.Height <= 0
            || placement.Width > MaximumDimension
            || placement.Height > MaximumDimension)
        {
            return null;
        }

        return placement;
    }
}
