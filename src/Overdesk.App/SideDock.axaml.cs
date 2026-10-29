using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Platform;
using Overdesk.Core.Settings;

namespace Overdesk.App;

/// <summary>
/// Left-side dock with live widgets: now playing (expands into a full mini player), system load,
/// local dev servers, clipboard history and a scratchpad. Collapses into a slim icon rail.
/// </summary>
public partial class SideDock : UserControl
{
    public const double ExpandedWidth = 284;
    public const double CollapsedWidth = 66;
    private const int MaxPorts = 8;

    // macOS/Windows background services that listen on ports but are not "your" servers.
    private static readonly HashSet<string> SystemListeners = new(StringComparer.OrdinalIgnoreCase)
    {
        "ControlCe", "ControlCenter", "rapportd", "launchd", "sharingd", "identitys", "remoted", "System",
        "svchost", "lsass", "wininit", "services", "spoolsv", "AirPlayXPCHelper", "WiFiAgent",
    };

    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _notesSave = new() { Interval = TimeSpan.FromSeconds(1) };
    private NowPlayingService? _media;
    private SystemStatsService? _stats;
    private ClipboardHistory? _clips;
    private AppSettings _settings = new();
    private bool _mediaOpen;

    public SideDock()
    {
        InitializeComponent();
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _notesSave.Tick += (_, _) =>
        {
            _notesSave.Stop();
            _settings.Notes = Notes.Text ?? "";
            SettingsChanged?.Invoke();
        };

        CollapseButton.Click += (_, _) => SetExpanded(!_settings.SidebarExpanded, animate: true);
        foreach (var rail in new[] { RailMedia, RailSystem, RailPorts, RailClips, RailNotes })
            rail.Click += (_, _) => SetExpanded(true, animate: true);
        RailMedia.Click += (_, _) => SetMediaOpen(true);

        MediaCard.PointerReleased += (_, e) =>
        {
            // Buttons and the seek bar inside the card handle their own clicks.
            if (e.Source is Visual v && (v.FindAncestorOfType<Button>(true) is not null || v.FindAncestorOfType<SeekBar>(true) is not null))
                return;
            if (_media?.Current is not null) SetMediaOpen(!_mediaOpen);
        };
        CompactPlay.Click += (_, _) => _ = _media?.PlayPauseAsync();
        BigPlay.Click += (_, _) => _ = _media?.PlayPauseAsync();
        PrevButton.Click += (_, _) => _ = _media?.PreviousAsync();
        NextButton.Click += (_, _) => _ = _media?.NextAsync();
        ShuffleButton.Click += (_, _) =>
        {
            if (_media?.Current is { Shuffle: { } on }) _ = _media.SetShuffleAsync(!on);
        };
        RepeatButton.Click += (_, _) => _ = _media?.CycleRepeatAsync();
        Back10.Click += (_, _) => SeekBy(-10);
        Fwd10.Click += (_, _) => SeekBy(10);
        BigSeek.Seeked += f =>
        {
            if (_media?.Current is { } c && c.Duration > TimeSpan.Zero) _ = _media.SeekAsync(c.Duration * f);
        };

        RefreshPorts.Click += (_, _) => _ = _stats?.RefreshPortsAsync();
        MetricsButton.Click += (_, _) => ShowMetricsPicker();
        ClearClips.Click += (_, _) => _clips?.Clear();
        Notes.TextChanged += (_, _) =>
        {
            _notesSave.Stop();
            _notesSave.Start();
        };
    }

    public event Action? SettingsChanged;

    /// <summary>Development snapshots: scroll the expanded dock.</summary>
    internal void DevScroll(double y) => Expanded.Offset = new Vector(0, y);
    public event Action<double>? WidthChanging;

    /// <summary>Short confirmation text for the host to show (e.g. "Copied").</summary>
    public event Action<string, string>? Toast;

    /// <summary>Change the scratchpad from outside (the AI); the text box saves it like a typed edit.</summary>
    internal void SetNotes(string text)
    {
        _settings.Notes = text;
        Notes.Text = text;
    }

    internal void Initialize(AppSettings settings, NowPlayingService media, SystemStatsService stats, ClipboardHistory clips)
    {
        _settings = settings;
        _media = media;
        _stats = stats;
        _clips = clips;
        Notes.Text = settings.Notes;

        media.Changed += UpdateMedia;
        stats.Sampled += UpdateSystem;
        stats.PortsChanged += UpdatePorts;
        clips.Changed += UpdateClips;

        SetExpanded(settings.SidebarExpanded, animate: false);
        BuildMetricRows();
        UpdateMedia();
        UpdatePorts();
        UpdateClips();
        _progressTimer.Start();
    }

    // ---------------------------------------------------------------- expand / collapse

    public void SetExpanded(bool expanded, bool animate)
    {
        if (_settings.SidebarExpanded != expanded)
        {
            _settings.SidebarExpanded = expanded;
            SettingsChanged?.Invoke();
        }

        Expanded.IsVisible = expanded;
        Rail.IsVisible = !expanded;
        HeaderText.IsVisible = expanded;
        CollapseButton.HorizontalAlignment = expanded ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        CollapseIcon.Data = Res.Icon(expanded ? "IconChevronLeft" : "IconChevronRight");
        ToolTip.SetTip(CollapseButton, expanded ? "Collapse" : "Expand");
        if (_stats is not null) _stats.WatchPorts = expanded;
        if (expanded) _ = _stats?.RefreshPortsAsync();

        var target = expanded ? ExpandedWidth : CollapsedWidth;
        if (!animate || double.IsNaN(Width))
        {
            Width = target;
            WidthChanging?.Invoke(target);
            return;
        }
        var from = Width;
        Animator.Run(this, 0.24, t =>
        {
            Width = from + (target - from) * Ease.OutCubic(t);
            WidthChanging?.Invoke(Width);
        });
    }

    // ---------------------------------------------------------------- now playing

    private void UpdateMedia()
    {
        var current = _media?.Current;
        var art = _media?.Artwork;

        MediaTitle.Text = current?.Title ?? "Nothing playing";
        MediaArtist.Text = current is null ? "Play something to see it here" : current.Artist;
        BigTitle.Text = current?.Title;
        BigArtist.Text = current?.Artist;
        BigAlbum.Text = current?.Album;
        MediaSource.Text = current?.SourceName;
        CompactPlay.IsVisible = current is not null;
        CompactSeek.IsVisible = current is not null && current.Duration > TimeSpan.Zero && !_mediaOpen;
        MediaHint.IsVisible = current is null;
        MediaHint.Text = OperatingSystem.IsMacOS()
            ? "Works with Apple Music and Spotify. macOS asks once to let OverDesk read and control them."
            : "Works with Spotify, browsers, Media Player and other apps that show media controls.";

        SmallArt.Source = art;
        BigArt.Source = art;
        RailArt.Source = art;
        MediaBackdrop.Background = art is null ? null : new ImageBrush(art) { Stretch = Stretch.UniformToFill };

        var playing = current?.IsPlaying == true;
        var playIcon = Res.Icon(playing ? "IconPause" : "IconPlay");
        CompactPlayIcon.Data = playIcon;
        BigPlayIcon.Data = playIcon;
        // A triangle looks centered when its mass is, i.e. nudged a little to the right.
        CompactPlayIcon.Margin = new Thickness(playing ? 0 : 2, 0, 0, 0);
        BigPlayIcon.Margin = new Thickness(playing ? 0 : 3, 0, 0, 0);
        ToolTip.SetTip(RailMedia, current is null ? "Nothing playing" : $"{current.Title} — {current.Artist}");

        UpdateModes(current);
        // Never fold the player away under the user's hands.
        if (current is null && _mediaOpen && !MediaCard.IsPointerOver) SetMediaOpen(false);
        UpdateProgress();
    }

    private void UpdateModes(NowPlaying? current)
    {
        var shuffle = current?.Shuffle;
        ShuffleButton.Classes.Set("on", shuffle == true);
        ShuffleButton.Classes.Set("unsupported", shuffle is null);
        ShuffleButton.IsEnabled = shuffle is not null;
        ToolTip.SetTip(ShuffleButton, shuffle is null ? "Shuffle (not available for this player)" : shuffle.Value ? "Shuffle: on" : "Shuffle: off");

        var repeat = current?.Repeat;
        RepeatButton.Classes.Set("on", repeat is RepeatMode.All or RepeatMode.One);
        RepeatButton.Classes.Set("unsupported", repeat is null);
        RepeatButton.IsEnabled = repeat is not null;
        RepeatOneBadge.IsVisible = repeat == RepeatMode.One;
        ToolTip.SetTip(RepeatButton, repeat switch
        {
            null => "Repeat (not available for this player)",
            RepeatMode.One => "Repeating this song",
            RepeatMode.All => current!.SupportsRepeatOne ? "Repeating the list · click for this song only" : "Repeating the list",
            _ => "Repeat: off",
        });
    }

    internal void SetMediaOpen(bool open)
    {
        if (_media?.Current is null) open = false;
        if (_mediaOpen == open) return;
        _mediaOpen = open;
        MediaDetails.IsVisible = open;
        MediaCompact.IsVisible = !open;
        CompactSeek.IsVisible = !open && _media?.Current is { Duration.Ticks: > 0 };
        if (!open) return;

        MediaDetails.Opacity = 0;
        Animator.Run(MediaDetails, 0.3, t =>
        {
            var e = Ease.OutCubic(t);
            MediaDetails.Opacity = e;
            MediaDetails.RenderTransform = new TranslateTransform(0, 10 * (1 - e));
        });
    }

    private void UpdateProgress()
    {
        if (_media?.Current is not { } c || c.Duration <= TimeSpan.Zero)
        {
            ElapsedText.Text = RemainingText.Text = "";
            return;
        }
        var position = _media.Position;
        var fraction = position.TotalSeconds / c.Duration.TotalSeconds;
        CompactSeek.Fraction = fraction;
        if (BigSeek.PreviewFraction is { } preview) position = c.Duration * preview;
        else BigSeek.Fraction = fraction;
        ElapsedText.Text = Format(position);
        RemainingText.Text = "-" + Format(c.Duration - position);
    }

    private void SeekBy(double seconds)
    {
        if (_media?.Current is not { } c) return;
        var target = _media.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (c.Duration > TimeSpan.Zero && target > c.Duration) target = c.Duration;
        _ = _media.SeekAsync(target);
    }

    private static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ---------------------------------------------------------------- system

    private sealed record MetricRow(MetricInfo Info, TextBlock Value, Sparkline? Chart, ProgressBar? Bar);

    private readonly List<MetricRow> _metricRows = new();

    /// <summary>One row per gauge the user switched on, in catalog order.</summary>
    internal void BuildMetricRows()
    {
        MetricRows.Children.Clear();
        _metricRows.Clear();
        foreach (var info in SystemStatsService.Catalog.Where(m => _settings.DockMetrics.Contains(m.Id) && (!m.WindowsOnly || OperatingSystem.IsWindows())))
        {
            var row = new StackPanel { Spacing = 5 };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            header.Children.Add(new TextBlock { Text = info.Label, Classes = { "muted" }, FontSize = 12 });
            var value = new TextBlock
            {
                FontWeight = FontWeight.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(value, 1);
            header.Children.Add(value);
            row.Children.Add(header);

            Sparkline? chart = null;
            ProgressBar? bar = null;
            if (info.Kind == MetricKind.Chart)
            {
                chart = new Sparkline { Height = 28 };
                chart.Bind(Sparkline.ColorProperty, this.GetResourceObservable(info.Id is "gpu" or "net" or "npu" ? "Accent2Color" : "AccentColor"));
                row.Children.Add(chart);
            }
            else if (info.Kind == MetricKind.Bar)
            {
                bar = new ProgressBar
                {
                    Minimum = 0, Maximum = 1, Height = 5, MinHeight = 5, CornerRadius = new CornerRadius(3),
                    Background = Res.Brush("Bg4Brush"),
                };
                bar.Bind(ProgressBar.ForegroundProperty, this.GetResourceObservable("AccentGradient"));
                row.Children.Add(bar);
            }

            _metricRows.Add(new MetricRow(info, value, chart, bar));
            MetricRows.Children.Add(row);
        }
        MetricsEmpty.IsVisible = _metricRows.Count == 0;
        UpdateSystem();
    }

    private void UpdateSystem()
    {
        if (_stats is null) return;
        foreach (var row in _metricRows)
        {
            var reading = _stats.Read(row.Info.Id);
            row.Value.Text = reading?.Value ?? "—";
            ToolTip.SetTip(row.Value, reading is null ? "Not available on this computer" : null);
            if (row.Chart is { } chart && reading?.History is { } history)
            {
                chart.Maximum = reading.HistoryMax;
                chart.SetValues(history);
            }
            if (row.Bar is { } bar)
            {
                bar.Value = reading?.Fraction ?? 0;
                // A nearly empty battery or a nearly full disk deserves a warning colour.
                var alarm = (row.Info.Id == "battery" && reading?.Fraction < 0.2) || (row.Info.Id == "disk" && reading?.Fraction > 0.9);
                if (alarm) bar.Foreground = Res.Brush("DangerBrush");
                else bar.Bind(ProgressBar.ForegroundProperty, this.GetResourceObservable("AccentGradient"));
            }
        }
        if (_stats.Read("cpu") is { } cpu) RailCpu.Text = cpu.Value;
    }

    /// <summary>The gear in the System card: tick the gauges you want to see.</summary>
    internal void ShowMetricsPicker()
    {
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(new TextBlock { Text = "SHOW IN THE DOCK", Classes = { "eyebrow" }, FontSize = 9.5, Margin = new Thickness(2, 0, 0, 6) });
        foreach (var info in SystemStatsService.Catalog.Where(m => !m.WindowsOnly || OperatingSystem.IsWindows()))
        {
            var available = _stats?.Read(info.Id) is not null;
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = info.Label, FontWeight = FontWeight.Medium, FontSize = 13 });
            text.Children.Add(new TextBlock
            {
                Text = available ? info.Description : "Not available on this computer",
                Classes = { "faint" }, FontSize = 11,
            });
            var box = new CheckBox
            {
                Classes = { "metric" }, Content = text, IsEnabled = available,
                IsChecked = _settings.DockMetrics.Contains(info.Id),
            };
            box.IsCheckedChanged += (_, _) =>
            {
                _settings.DockMetrics.Remove(info.Id);
                if (box.IsChecked == true) _settings.DockMetrics.Add(info.Id);
                BuildMetricRows();
                SettingsChanged?.Invoke();
            };
            list.Children.Add(box);
        }
        new Flyout { Content = list, Placement = PlacementMode.RightEdgeAlignedTop }.ShowAt(MetricsButton);
    }

    private static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0");

    // ---------------------------------------------------------------- ports

    private void UpdatePorts()
    {
        var ports = (_stats?.Ports ?? Array.Empty<ListeningPort>())
            // Skip privileged ports and the OS's ephemeral range (49152+): dev servers live in between.
            .Where(p => p.Port is >= 1024 and < 49152 && !SystemListeners.Contains(p.ProcessName))
            .ToList();
        RailPortCount.Text = ports.Count.ToString();
        PortList.Children.Clear();
        PortsEmpty.IsVisible = ports.Count == 0;

        foreach (var port in ports.Take(MaxPorts))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new TextBlock
            {
                Text = ":" + port.Port, Classes = { "mono" }, FontWeight = FontWeight.SemiBold, FontSize = 12,
                Foreground = Res.Brush("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 52,
            });
            var name = new TextBlock
            {
                Text = port.ProcessName, Classes = { "muted" }, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0),
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var open = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconExternal"), Height = 11 };
            Grid.SetColumn(open, 2);
            row.Children.Add(open);

            var url = $"http://localhost:{port.Port}";
            var button = new Button { Classes = { "dockrow" }, Content = row };
            ToolTip.SetTip(button, $"{url}  ·  pid {port.ProcessId}\nClick to open, right-click for more");
            button.Click += (_, _) => OpenUrl(url);
            button.ContextMenu = PortMenu(port, url);
            PortList.Children.Add(button);
        }
        if (ports.Count > MaxPorts)
            PortList.Children.Add(new TextBlock { Text = $"+{ports.Count - MaxPorts} more", Classes = { "faint" }, FontSize = 11, Margin = new Thickness(4, 2) });
    }

    private ContextMenu PortMenu(ListeningPort port, string url)
    {
        var open = new MenuItem { Header = "Open in browser", Icon = MenuIcon("IconExternal") };
        open.Click += (_, _) => OpenUrl(url);
        var copy = new MenuItem { Header = "Copy URL", Icon = MenuIcon("IconCopy") };
        copy.Click += async (_, _) =>
        {
            if (_clips is not null) await _clips.CopyAsync(url);
            Toast?.Invoke("URL copied", "IconCopy");
        };
        var stop = new MenuItem { Header = $"Stop {port.ProcessName} (pid {port.ProcessId})", Icon = MenuIcon("IconPower"), Classes = { "danger" } };
        stop.Click += (_, _) =>
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(port.ProcessId);
                p.Kill();
                Toast?.Invoke($"Stopped {port.ProcessName}", "IconPower");
            }
            catch (Exception ex)
            {
                Toast?.Invoke($"Could not stop it: {ex.Message}", "IconAlert");
            }
            _ = _stats?.RefreshPortsAsync();
        };
        return new ContextMenu { ItemsSource = new Control[] { open, copy, new Separator(), stop } };
    }

    private static Avalonia.Controls.Shapes.Path MenuIcon(string key)
        => new() { Classes = { "icon" }, Data = Res.Icon(key) };

    private void OpenUrl(string url)
    {
        if (TopLevel.GetTopLevel(this) is { } top) _ = top.Launcher.LaunchUriAsync(new Uri(url));
    }

    // ---------------------------------------------------------------- clipboard

    private void UpdateClips()
    {
        var items = _clips?.Items ?? new List<string>();
        ClipList.Children.Clear();
        ClipsEmpty.IsVisible = items.Count == 0;
        foreach (var text in items.Take(5))
        {
            var preview = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var row = new TextBlock
            {
                Text = preview, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "mono" },
            };
            var button = new Button { Classes = { "dockrow" }, Content = row };
            ToolTip.SetTip(button, text.Length > 400 ? text[..400] + "…" : text);
            button.Click += async (_, _) =>
            {
                if (_clips is null) return;
                await _clips.CopyAsync(text);
                Toast?.Invoke("Copied to clipboard", "IconCopy");
            };
            button.ContextMenu = new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    MenuItem("Pin as snippet", "IconPin", () => PinSnippet(text)),
                    MenuItem("Ask AI about this", "IconSparkle", () => _actions?.OpenAi(question: "Help me with this:\n\n" + text)),
                },
            };
            ClipList.Children.Add(button);
        }
    }
}
