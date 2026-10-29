using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overdesk.Core.Platform;

namespace Overdesk.App.Services;

/// <summary>
/// Polls the OS media controller in the background and keeps the latest track for the UI.
/// Between polls the position is extrapolated so progress bars move smoothly.
/// </summary>
internal sealed class NowPlayingService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.2);

    // Players report "nothing" for a moment between tracks; only believe it after this long.
    private static readonly TimeSpan GoneAfter = TimeSpan.FromSeconds(4);

    private readonly IMediaController _media;
    private readonly CancellationTokenSource _stop = new();
    private byte[]? _artworkBytes;
    private DateTime? _emptySince;

    public NowPlayingService(IMediaController media)
    {
        _media = media;
        _ = PollLoopAsync();
    }

    /// <summary>Raised on the UI thread when the track, play state or artwork changed.</summary>
    public event Action? Changed;

    public NowPlaying? Current { get; private set; }
    public Bitmap? Artwork { get; private set; }

    /// <summary>Best estimate of the playback position right now.</summary>
    public TimeSpan Position
    {
        get
        {
            if (Current is not { } c) return TimeSpan.Zero;
            var p = c.IsPlaying ? c.Position + (DateTime.UtcNow - c.CapturedAtUtc) : c.Position;
            return c.Duration > TimeSpan.Zero && p > c.Duration ? c.Duration : p;
        }
    }

    public Task PlayPauseAsync() => After(_media.PlayPauseAsync(), c => c with
    {
        IsPlaying = !c.IsPlaying, Position = Position, CapturedAtUtc = DateTime.UtcNow,
    });

    public Task SetShuffleAsync(bool shuffle) => After(_media.SetShuffleAsync(shuffle), c => c with { Shuffle = shuffle });

    /// <summary>Off → repeat all → repeat one (when the player has it) → off.</summary>
    public Task CycleRepeatAsync()
    {
        if (Current is not { } c) return Task.CompletedTask;
        var next = c.Repeat switch
        {
            RepeatMode.All when c.SupportsRepeatOne => RepeatMode.One,
            RepeatMode.All or RepeatMode.One => RepeatMode.Off,
            _ => RepeatMode.All,
        };
        return After(_media.SetRepeatAsync(next), x => x with { Repeat = next });
    }

    public Task NextAsync() => After(_media.NextAsync(), null);
    public Task PreviousAsync() => After(_media.PreviousAsync(), null);

    public Task SeekAsync(TimeSpan position) => After(_media.SeekAsync(position), c => c with
    {
        Position = position, CapturedAtUtc = DateTime.UtcNow,
    });

    /// <summary>Apply an optimistic local update so the UI reacts instantly, then refresh from the player.</summary>
    private async Task After(Task command, Func<NowPlaying, NowPlaying>? optimistic)
    {
        if (optimistic is not null && Current is { } c)
        {
            Current = optimistic(c);
            Changed?.Invoke();
        }
        try { await command; } catch (Exception) { /* the player went away; the next poll will tell */ }
        await RefreshAsync();
    }

    private async Task PollLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await RefreshAsync();
            try { await Task.Delay(PollInterval, _stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshAsync()
    {
        NowPlaying? next;
        try { next = await Task.Run(() => _media.GetAsync(_stop.Token)); }
        catch (Exception) { next = null; }

        // Bridge the short gap players report between two tracks, so the UI does not collapse and re-open.
        if (next is null && Current is not null)
        {
            _emptySince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _emptySince < GoneAfter) return;
        }
        else _emptySince = null;

        Bitmap? art = null;
        var artChanged = !SameBytes(next?.Artwork, _artworkBytes);
        if (artChanged && next?.Artwork is { Length: > 0 } bytes)
        {
            try { using var ms = new MemoryStream(bytes); art = new Bitmap(ms); }
            catch (Exception) { art = null; }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var changed = !Equivalent(Current, next) || artChanged;
            Current = next;
            if (artChanged)
            {
                _artworkBytes = next?.Artwork;
                Artwork = art;
            }
            if (changed) Changed?.Invoke();
        });
    }

    private static bool SameBytes(byte[]? a, byte[]? b)
        => ReferenceEquals(a, b) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b));

    /// <summary>Equal apart from the natural drift of the position while playing.</summary>
    private bool Equivalent(NowPlaying? a, NowPlaying? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (!a.SameTrack(b) || a.IsPlaying != b.IsPlaying || a.Duration != b.Duration
            || a.Shuffle != b.Shuffle || a.Repeat != b.Repeat) return false;
        return Math.Abs((Position - b.Position).TotalSeconds) < 2;
    }

    public void Dispose() => _stop.Cancel();
}
