using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class JsonPlayerPlaytimeService : IPlayerPlaytimeService
{
    private const int StorageVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, ProfileState> _profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private Task _pendingSave = Task.CompletedTask;
    private bool _checkpointLoopStarted;

    private static string StorageDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kidda.MinecraftServerManager",
        "PlayerPlaytime");

    public event EventHandler? Changed;

    public async Task InitializeAsync(
        IEnumerable<ServerProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await LoadProfileAsync(profile, cancellationToken);
            lock (_syncRoot)
            {
                _profiles[profile.Id] = state;
            }
        }

        StartCheckpointLoop();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordConnection(string profileId, PlayerConnectionChange change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(change);

        var changed = false;
        lock (_syncRoot)
        {
            if (!_profiles.TryGetValue(profileId, out var profile))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (!profile.Players.TryGetValue(change.PlayerName, out var player))
            {
                if (change.Kind == PlayerConnectionKind.Left)
                {
                    return;
                }

                player = new PlayerState(change.PlayerName);
                profile.Players[change.PlayerName] = player;
            }

            player.DisplayName = change.PlayerName;
            if (change.Kind == PlayerConnectionKind.Joined)
            {
                if (player.SessionStartedUtc is null)
                {
                    player.SessionStartedUtc = now;
                    player.LastSeenUtc = now;
                    changed = true;
                }
            }
            else if (player.SessionStartedUtc is not null)
            {
                CompleteSession(player, now);
                changed = true;
            }

            if (changed)
            {
                QueueSaveLocked(profileId);
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void CloseSessions(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var changed = false;
        lock (_syncRoot)
        {
            if (!_profiles.TryGetValue(profileId, out var profile))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var player in profile.Players.Values.Where(player => player.SessionStartedUtc is not null))
            {
                CompleteSession(player, now);
                changed = true;
            }

            if (changed)
            {
                QueueSaveLocked(profileId);
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<PlayerPlaytimeSnapshot> GetSnapshots()
    {
        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            return _profiles.Values
                .SelectMany(profile => profile.Players.Values.Select(player =>
                    new PlayerPlaytimeSnapshot(
                        profile.Id,
                        profile.DisplayName,
                        player.DisplayName,
                        player.CompletedPlaytime + CurrentSessionDuration(player, now),
                        player.SessionStartedUtc is not null,
                        player.LastSeenUtc)))
                .ToArray();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task pendingSave;
        lock (_syncRoot)
        {
            pendingSave = _pendingSave;
        }

        await pendingSave.WaitAsync(cancellationToken);
    }

    private async Task<ProfileState> LoadProfileAsync(
        ServerProfile profile,
        CancellationToken cancellationToken)
    {
        var state = new ProfileState(profile.Id, profile.DisplayName);
        var path = GetProfilePath(profile.Id);
        if (!File.Exists(path))
        {
            return state;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var stored = await JsonSerializer.DeserializeAsync<StoredProfile>(
                stream,
                SerializerOptions,
                cancellationToken);
            if (stored?.Version != StorageVersion)
            {
                return state;
            }

            foreach (var storedPlayer in stored.Players)
            {
                if (string.IsNullOrWhiteSpace(storedPlayer.PlayerName)
                    || storedPlayer.CompletedTicks < 0)
                {
                    continue;
                }

                state.Players[storedPlayer.PlayerName] = new PlayerState(storedPlayer.PlayerName)
                {
                    CompletedPlaytime = TimeSpan.FromTicks(storedPlayer.CompletedTicks),
                    LastSeenUtc = storedPlayer.LastSeenUtc
                };
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged history file must not prevent the server manager from opening.
        }

        return state;
    }

    private void QueueSaveLocked(string profileId)
    {
        _pendingSave = SaveAfterAsync(_pendingSave, profileId);
    }

    private async Task SaveAfterAsync(Task previousSave, string profileId)
    {
        await previousSave;
        await PersistProfileAsync(profileId);
    }

    private void StartCheckpointLoop()
    {
        lock (_syncRoot)
        {
            if (_checkpointLoopStarted)
            {
                return;
            }

            _checkpointLoopStarted = true;
        }

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync())
            {
                lock (_syncRoot)
                {
                    foreach (var profile in _profiles.Values.Where(profile =>
                        profile.Players.Values.Any(player => player.SessionStartedUtc is not null)))
                    {
                        QueueSaveLocked(profile.Id);
                    }
                }
            }
        });
    }

    private async Task PersistProfileAsync(string profileId)
    {
        try
        {
            StoredProfile? stored;
            lock (_syncRoot)
            {
                if (!_profiles.TryGetValue(profileId, out var profile))
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                stored = new StoredProfile
                {
                    Version = StorageVersion,
                    ProfileId = profile.Id,
                    ProfileName = profile.DisplayName,
                    Players = profile.Players.Values
                        .OrderBy(player => player.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        .Select(player => new StoredPlayer
                        {
                            PlayerName = player.DisplayName,
                            CompletedTicks = (player.CompletedPlaytime + CurrentSessionDuration(player, now)).Ticks,
                            LastSeenUtc = player.LastSeenUtc
                        })
                        .ToList()
                };
            }

            Directory.CreateDirectory(StorageDirectory);
            var targetPath = GetProfilePath(profileId);
            var temporaryPath = $"{targetPath}.tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, stored, SerializerOptions);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Tracking remains live in memory if local persistence is temporarily unavailable.
        }
    }

    private static void CompleteSession(PlayerState player, DateTimeOffset now)
    {
        if (player.SessionStartedUtc is not { } started)
        {
            return;
        }

        player.CompletedPlaytime += now - started;
        player.SessionStartedUtc = null;
        player.LastSeenUtc = now;
    }

    private static TimeSpan CurrentSessionDuration(PlayerState player, DateTimeOffset now) =>
        player.SessionStartedUtc is { } started ? now - started : TimeSpan.Zero;

    private static string GetProfilePath(string profileId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId)));
        return Path.Combine(StorageDirectory, $"{hash}.json");
    }

    private sealed class ProfileState(string id, string displayName)
    {
        public string Id { get; } = id;

        public string DisplayName { get; } = displayName;

        public Dictionary<string, PlayerState> Players { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PlayerState(string displayName)
    {
        public string DisplayName { get; set; } = displayName;

        public TimeSpan CompletedPlaytime { get; set; }

        public DateTimeOffset? SessionStartedUtc { get; set; }

        public DateTimeOffset? LastSeenUtc { get; set; }
    }

    private sealed class StoredProfile
    {
        public int Version { get; set; }

        public string ProfileId { get; set; } = string.Empty;

        public string ProfileName { get; set; } = string.Empty;

        public List<StoredPlayer> Players { get; set; } = [];
    }

    private sealed class StoredPlayer
    {
        public string PlayerName { get; set; } = string.Empty;

        public long CompletedTicks { get; set; }

        public DateTimeOffset? LastSeenUtc { get; set; }
    }
}
