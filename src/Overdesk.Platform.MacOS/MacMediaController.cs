using System.Diagnostics;
using System.Globalization;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>
/// Now playing on macOS. The system-wide MediaRemote API is closed to third-party apps on current macOS,
/// so we talk to the players directly through AppleScript: Apple Music and Spotify. A player is only queried
/// while it is already running (scripting a closed app would launch it). The first query makes macOS ask
/// the user to allow controlling that app.
/// </summary>
public sealed class MacMediaController : IMediaController
{
    private sealed record Player(string BundleId, string Name, bool PositionIsSeconds);

    private static readonly Player[] Players =
    {
        new("com.spotify.client", "Spotify", PositionIsSeconds: true),
        new("com.apple.Music", "Music", PositionIsSeconds: true),
    };

    // Every query is an osascript process, so only a playing track is followed at the caller's pace.
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);

    // Not "until the player restarts": the user can allow it in System Settings at any moment.
    private static readonly TimeSpan DeniedRetry = TimeSpan.FromMinutes(1);

    private const string AutomationDenied = "-1743"; // errAEEventNotPermitted

    private readonly Dictionary<string, (string Key, byte[]? Art)> _artwork = new();
    private readonly Dictionary<string, DateTime> _deniedUntil = new();
    private Player? _current;
    private NowPlaying? _last;
    private DateTime _nextQuery;

    public string? BlockedPlayer { get; private set; }

    public async Task<NowPlaying?> GetAsync(CancellationToken cancellation)
    {
        if (DateTime.UtcNow < _nextQuery) return _last;
        _last = await QueryPlayersAsync(cancellation);
        _nextQuery = _last is { IsPlaying: true } ? DateTime.MinValue : DateTime.UtcNow + IdleInterval;
        BlockedPlayer = Players.FirstOrDefault(p => _deniedUntil.ContainsKey(p.BundleId) && RunningApps.Pids(p.BundleId).Count > 0)?.Name;
        return _last;
    }

    private async Task<NowPlaying?> QueryPlayersAsync(CancellationToken cancellation)
    {
        NowPlaying? paused = null;
        Player? pausedPlayer = null;
        // The one we know about first: while it plays, the others need no process at all.
        foreach (var player in Players.OrderBy(p => p == _current ? 0 : 1))
        {
            if (RunningApps.Pids(player.BundleId).Count == 0) continue;
            if (_deniedUntil.TryGetValue(player.BundleId, out var until) && DateTime.UtcNow < until) continue;
            var info = await QueryAsync(player, cancellation);
            if (info is null) continue;
            if (info.IsPlaying)
            {
                _current = player;
                return info;
            }
            if (paused is null || player == _current)
            {
                paused = info;
                pausedPlayer = player;
            }
        }
        _current = pausedPlayer;
        return paused;
    }

    public Task PlayPauseAsync() => Command("playpause");
    public Task NextAsync() => Command("next track");
    public Task PreviousAsync() => Command("previous track");

    public Task SeekAsync(TimeSpan position)
        => Command("set player position to " + position.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));

    public Task SetShuffleAsync(bool shuffle)
        => Command(IsSpotify(_current) ? $"set shuffling to {Bool(shuffle)}" : $"set shuffle enabled to {Bool(shuffle)}");

    public Task SetRepeatAsync(RepeatMode mode)
    {
        if (IsSpotify(_current)) return Command($"set repeating to {Bool(mode != RepeatMode.Off)}");
        var value = mode switch { RepeatMode.One => "one", RepeatMode.All => "all", _ => "off" };
        return Command($"set song repeat to {value}");
    }

    private static bool IsSpotify(Player? p) => p?.BundleId == "com.spotify.client";
    private static string Bool(bool value) => value ? "true" : "false";

    private async Task Command(string command)
    {
        if (_current is not { } player || RunningApps.Pids(player.BundleId).Count == 0) return;
        await RunAsync($"tell application id \"{player.BundleId}\" to {command}", CancellationToken.None);
        _nextQuery = DateTime.MinValue; // the caller refreshes right after and must see the result
    }

    private async Task<NowPlaying?> QueryAsync(Player player, CancellationToken cancellation)
    {
        // One line per field; numbers are converted with "as string" so they follow the user's locale (handled below).
        var modes = IsSpotify(player)
            ? "((shuffling) as string) & linefeed & ((repeating) as string)"
            : "((shuffle enabled) as string) & linefeed & ((song repeat) as string)";
        var script = $$"""
            tell application id "{{player.BundleId}}"
                set s to player state as string
                if s is "stopped" then return "stopped"
                set t to current track
                return s & linefeed & (name of t) & linefeed & (artist of t) & linefeed & (album of t) & linefeed & ((duration of t) as string) & linefeed & ((player position) as string) & linefeed & {{modes}}
            end tell
            """;
        var denied = false;
        var output = await RunAsync(script, cancellation, () => denied = true);
        if (denied) _deniedUntil[player.BundleId] = DateTime.UtcNow + DeniedRetry;
        else _deniedUntil.Remove(player.BundleId);
        if (output is null) return null;
        var lines = output.Split('\n');
        if (lines.Length < 6 || lines[0] == "stopped") return null;

        var duration = ParseNumber(lines[4]);
        // Spotify reports the duration in milliseconds, Music in seconds.
        var durationSeconds = player.BundleId == "com.spotify.client" ? duration / 1000 : duration;
        var info = new NowPlaying(
            Title: lines[1].Trim(),
            Artist: lines[2].Trim(),
            Album: lines[3].Trim(),
            SourceName: player.Name,
            Duration: TimeSpan.FromSeconds(Math.Max(durationSeconds, 0)),
            Position: TimeSpan.FromSeconds(Math.Max(ParseNumber(lines[5]), 0)),
            IsPlaying: lines[0].Trim() == "playing",
            Artwork: null,
            CapturedAtUtc: DateTime.UtcNow,
            Shuffle: lines.Length > 6 ? lines[6].Trim() == "true" : null,
            Repeat: lines.Length > 7 ? ParseRepeat(lines[7].Trim()) : null,
            SupportsRepeatOne: !IsSpotify(player));

        return info with { Artwork = await ArtworkAsync(player, info, cancellation) };
    }

    /// <summary>Artwork is fetched once per track and cached.</summary>
    private async Task<byte[]?> ArtworkAsync(Player player, NowPlaying info, CancellationToken cancellation)
    {
        var key = info.Title + "\u0001" + info.Artist + "\u0001" + info.Album;
        if (_artwork.TryGetValue(player.BundleId, out var cached) && cached.Key == key) return cached.Art;

        byte[]? art = null;
        try
        {
            if (player.BundleId == "com.spotify.client")
            {
                var url = await RunAsync($"tell application id \"{player.BundleId}\" to return artwork url of current track", cancellation);
                if (url is { Length: > 0 } && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    art = await http.GetByteArrayAsync(url.Trim(), cancellation);
                }
            }
            else
            {
                var file = Path.Combine(Path.GetTempPath(), "overdesk-artwork");
                var script = $$"""
                    tell application id "{{player.BundleId}}"
                        try
                            set d to raw data of artwork 1 of current track
                        on error
                            return "none"
                        end try
                    end tell
                    set f to open for access (POSIX file "{{file}}") with write permission
                    set eof f to 0
                    write d to f
                    close access f
                    return "ok"
                    """;
                if (await RunAsync(script, cancellation) == "ok" && File.Exists(file))
                    art = await File.ReadAllBytesAsync(file, cancellation);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            art = null;
        }

        _artwork[player.BundleId] = (key, art);
        return art;
    }

    /// <summary>Music answers off/one/all, Spotify true/false.</summary>
    private static RepeatMode ParseRepeat(string value) => value switch
    {
        "one" => RepeatMode.One,
        "all" or "true" => RepeatMode.All,
        _ => RepeatMode.Off,
    };

    private static double ParseNumber(string text)
    {
        text = text.Trim().Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>Run AppleScript; returns its trimmed output, or null when it failed (not allowed, no track...).</summary>
    private static async Task<string?> RunAsync(string script, CancellationToken cancellation, Action? denied = null)
    {
        var info = new ProcessStartInfo("/usr/bin/osascript")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(info);
        if (process is null) return null;
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync(cancellation);
        var output = await process.StandardOutput.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode == 0) return output.TrimEnd('\n', '\r');
        if ((await errors).Contains(AutomationDenied)) denied?.Invoke();
        return null;
    }
}
