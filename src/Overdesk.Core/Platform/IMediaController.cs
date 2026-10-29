namespace Overdesk.Core.Platform;

/// <summary>Repeat setting of the player. Not every player supports <see cref="One"/>.</summary>
public enum RepeatMode { Off, All, One }

/// <summary>What is playing right now, as reported by the OS or the player app.</summary>
public sealed record NowPlaying(
    string Title,
    string Artist,
    string Album,
    string SourceName,
    TimeSpan Duration,
    TimeSpan Position,
    bool IsPlaying,
    byte[]? Artwork,
    DateTime CapturedAtUtc,
    bool? Shuffle = null,
    RepeatMode? Repeat = null,
    bool SupportsRepeatOne = true)
{
    /// <summary>Same track as <paramref name="other"/> (ignores position and play state).</summary>
    public bool SameTrack(NowPlaying? other)
        => other is not null && other.Title == Title && other.Artist == Artist && other.SourceName == SourceName;
}

/// <summary>Reads and controls the music/video that is currently playing on this machine.</summary>
public interface IMediaController
{
    /// <summary>The current media, or null when nothing is playing or paused.</summary>
    Task<NowPlaying?> GetAsync(CancellationToken cancellation);

    Task PlayPauseAsync();
    Task NextAsync();
    Task PreviousAsync();
    Task SeekAsync(TimeSpan position);
    Task SetShuffleAsync(bool shuffle);
    Task SetRepeatAsync(RepeatMode mode);
}

/// <summary>Used where no media integration exists.</summary>
public sealed class NoMediaController : IMediaController
{
    public Task<NowPlaying?> GetAsync(CancellationToken cancellation) => Task.FromResult<NowPlaying?>(null);
    public Task PlayPauseAsync() => Task.CompletedTask;
    public Task NextAsync() => Task.CompletedTask;
    public Task PreviousAsync() => Task.CompletedTask;
    public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
    public Task SetShuffleAsync(bool shuffle) => Task.CompletedTask;
    public Task SetRepeatAsync(RepeatMode mode) => Task.CompletedTask;
}
