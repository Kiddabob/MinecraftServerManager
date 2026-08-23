using MinecraftServerManager.Infrastructure;

namespace MinecraftServerManager.Models;

public sealed record PlayerScopeOption(string Id, string DisplayName);

public sealed class PlayerPlaytimeRow : BindableBase
{
    private bool _isOnline;
    private string _statusText;
    private string _profileSummary;
    private TimeSpan _sortablePlaytime;
    private string _playtimeText;
    private string _lastSeenText;

    public PlayerPlaytimeRow(
        string playerName,
        bool isOnline,
        string statusText,
        string profileSummary,
        TimeSpan sortablePlaytime,
        string playtimeText,
        string lastSeenText)
    {
        PlayerName = playerName;
        _isOnline = isOnline;
        _statusText = statusText;
        _profileSummary = profileSummary;
        _sortablePlaytime = sortablePlaytime;
        _playtimeText = playtimeText;
        _lastSeenText = lastSeenText;
    }

    public string PlayerName { get; }

    public bool IsOnline
    {
        get => _isOnline;
        private set => SetProperty(ref _isOnline, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ProfileSummary
    {
        get => _profileSummary;
        private set => SetProperty(ref _profileSummary, value);
    }

    public TimeSpan SortablePlaytime
    {
        get => _sortablePlaytime;
        private set => SetProperty(ref _sortablePlaytime, value);
    }

    public string PlaytimeText
    {
        get => _playtimeText;
        private set => SetProperty(ref _playtimeText, value);
    }

    public string LastSeenText
    {
        get => _lastSeenText;
        private set => SetProperty(ref _lastSeenText, value);
    }

    public void UpdateFrom(PlayerPlaytimeRow source)
    {
        IsOnline = source.IsOnline;
        StatusText = source.StatusText;
        ProfileSummary = source.ProfileSummary;
        SortablePlaytime = source.SortablePlaytime;
        PlaytimeText = source.PlaytimeText;
        LastSeenText = source.LastSeenText;
    }
}
