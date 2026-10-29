using Overdesk.Core.Platform;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Overdesk.Platform.Windows;

/// <summary>
/// Now playing on Windows through the system media transport controls: the same source as the volume
/// flyout, so it covers Spotify, browsers (YouTube...), the Media Player and most other players.
/// </summary>
public sealed class WinMediaController : IMediaController
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private (string Key, byte[]? Art) _artwork;

    private async Task<GlobalSystemMediaTransportControlsSession?> SessionAsync()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _manager.GetCurrentSession();
        }
        catch (Exception)
        {
            return null; // media service unavailable (e.g. Windows N without the media pack)
        }
    }

    public async Task<NowPlaying?> GetAsync(CancellationToken cancellation)
    {
        var session = await SessionAsync();
        if (session is null) return null;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null || string.IsNullOrWhiteSpace(props.Title)) return null;
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            // The timeline is a snapshot taken at LastUpdatedTime; move it forward while playing.
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var position = timeline.Position;
            if (playing && timeline.LastUpdatedTime.Year > 2000)
                position += DateTimeOffset.Now - timeline.LastUpdatedTime;
            var duration = timeline.EndTime - timeline.StartTime;
            if (duration > TimeSpan.Zero && position > duration) position = duration;

            var key = props.Title + "\u0001" + props.Artist + "\u0001" + props.AlbumTitle;
            if (_artwork.Key != key)
                _artwork = (key, await ReadThumbnailAsync(props.Thumbnail));

            return new NowPlaying(
                props.Title, props.Artist ?? "", props.AlbumTitle ?? "",
                SourceName(session.SourceAppUserModelId),
                duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
                position < TimeSpan.Zero ? TimeSpan.Zero : position,
                playing, _artwork.Art, DateTime.UtcNow,
                Shuffle: playback.IsShuffleActive,
                Repeat: playback.AutoRepeatMode switch
                {
                    global::Windows.Media.MediaPlaybackAutoRepeatMode.Track => RepeatMode.One,
                    global::Windows.Media.MediaPlaybackAutoRepeatMode.List => RepeatMode.All,
                    global::Windows.Media.MediaPlaybackAutoRepeatMode.None => RepeatMode.Off,
                    _ => null,
                });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SourceName(string appId)
    {
        // "Spotify.exe", "Chrome", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> a readable name.
        var name = appId.Split('!')[0].Split('_')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "Media";
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            return memory.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task PlayPauseAsync()
    {
        if (await SessionAsync() is { } s) await s.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        if (await SessionAsync() is { } s) await s.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        if (await SessionAsync() is { } s) await s.TrySkipPreviousAsync();
    }

    public async Task SetShuffleAsync(bool shuffle)
    {
        if (await SessionAsync() is { } s) await s.TryChangeShuffleActiveAsync(shuffle);
    }

    public async Task SetRepeatAsync(RepeatMode mode)
    {
        if (await SessionAsync() is not { } s) return;
        await s.TryChangeAutoRepeatModeAsync(mode switch
        {
            RepeatMode.One => global::Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            RepeatMode.All => global::Windows.Media.MediaPlaybackAutoRepeatMode.List,
            _ => global::Windows.Media.MediaPlaybackAutoRepeatMode.None,
        });
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (await SessionAsync() is { } s) await s.TryChangePlaybackPositionAsync(position.Ticks);
    }
}
