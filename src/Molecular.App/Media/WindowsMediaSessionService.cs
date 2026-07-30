using System.Diagnostics.CodeAnalysis;
using System.IO;
using Windows.Storage.Streams;
using Windows.Media;
using Windows.Media.Control;

namespace Molecular.App.Media;

public enum MediaTransportAction
{
    Previous,
    TogglePlayPause,
    Next
}

public sealed record MediaSessionSnapshot(
    string SourceAppId,
    string Title,
    string Artist,
    bool IsPlaying,
    bool CanGoPrevious,
    bool CanTogglePlayPause,
    bool CanGoNext,
    byte[]? ThumbnailBytes,
    TimeSpan Position,
    TimeSpan Duration);

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Media reads may still be completing during synchronous window shutdown; SemaphoreSlim does not allocate a native wait handle unless explicitly requested.")]
public sealed class WindowsMediaSessionService
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly Dictionary<string, byte[]> _thumbnailCache = new(StringComparer.Ordinal);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private DateTime _nextInitializationAttemptAt = DateTime.MinValue;

    public async Task<IReadOnlyList<MediaSessionSnapshot>> ReadSessionsAsync()
    {
        var manager = await GetManagerAsync();
        if (manager is null) return Array.Empty<MediaSessionSnapshot>();

        var snapshots = new List<MediaSessionSnapshot>();
        var activeThumbnailKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in manager.GetSessions())
        {
            try
            {
                var properties = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var controls = playback.Controls;
                var thumbnailKey = $"{session.SourceAppUserModelId}\n{properties.Title}\n{properties.Artist}";
                activeThumbnailKeys.Add(thumbnailKey);
                if (!_thumbnailCache.TryGetValue(thumbnailKey, out var thumbnailBytes))
                {
                    thumbnailBytes = await ReadThumbnailAsync(properties.Thumbnail);
                    if (thumbnailBytes is not null) _thumbnailCache[thumbnailKey] = thumbnailBytes;
                }
                var timeline = session.GetTimelineProperties();
                var position = timeline.Position - timeline.StartTime;
                var duration = timeline.EndTime - timeline.StartTime;
                if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    position += DateTimeOffset.Now - timeline.LastUpdatedTime;
                position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Math.Max(0, duration.Ticks)));
                snapshots.Add(new MediaSessionSnapshot(
                    session.SourceAppUserModelId,
                    properties.Title ?? string.Empty,
                    properties.Artist ?? string.Empty,
                    playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    controls.IsPreviousEnabled,
                    controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                    controls.IsNextEnabled,
                    thumbnailBytes,
                    position,
                    duration));
            }
            catch
            {
                // A media session may disappear while its properties are read.
            }
        }

        foreach (var staleKey in _thumbnailCache.Keys.Where(key => !activeThumbnailKeys.Contains(key)).ToArray())
            _thumbnailCache.Remove(staleKey);

        return snapshots;
    }

    public async Task<bool> ExecuteAsync(string executableName, string applicationName, MediaTransportAction action)
    {
        var manager = await GetManagerAsync();
        var session = manager?.GetSessions().FirstOrDefault(candidate =>
            Matches(candidate.SourceAppUserModelId, executableName, applicationName));
        if (session is null) return false;

        try
        {
            return action switch
            {
                MediaTransportAction.Previous => await session.TrySkipPreviousAsync(),
                MediaTransportAction.TogglePlayPause => await session.TryTogglePlayPauseAsync(),
                MediaTransportAction.Next => await session.TrySkipNextAsync(),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    public static MediaSessionSnapshot? FindForApplication(
        IEnumerable<MediaSessionSnapshot> sessions,
        string executableName,
        string applicationName) =>
        sessions.FirstOrDefault(session => Matches(session.SourceAppId, executableName, applicationName));

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync()
    {
        if (_manager is not null) return _manager;
        if (DateTime.UtcNow < _nextInitializationAttemptAt) return null;

        await _initializationLock.WaitAsync();
        try
        {
            if (_manager is not null) return _manager;
            if (DateTime.UtcNow < _nextInitializationAttemptAt) return null;
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            }
            catch
            {
                _manager = null;
                _nextInitializationAttemptAt = DateTime.UtcNow.AddSeconds(10);
            }

            return _manager;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static bool Matches(string sourceAppId, string executableName, string applicationName)
    {
        var source = Normalize(sourceAppId);
        var executable = Normalize(Path.GetFileNameWithoutExtension(executableName));
        var name = Normalize(applicationName);
        return (!string.IsNullOrWhiteSpace(executable) && source.Contains(executable, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(name) && source.Contains(name, StringComparison.Ordinal));
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return null;

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

}
