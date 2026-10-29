using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Platform;
using Overdesk.Core.Plugins;
using Overdesk.Core.Settings;
using Overdesk.Core.Tabs;

namespace Overdesk.App;

public partial class MainWindow : Window
{
    private const double MinToolPanelWidth = 240;
    private const double MacTrafficLightsWidth = 78;
    private const double WindowsCaptionWidth = 140;

    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly IProgramCatalog _catalog;
    private readonly NowPlayingService _nowPlaying = new(PlatformFactory.CreateMedia());
    private readonly SystemStatsService _stats = new(PlatformFactory.CreateSystemMonitor());
    private readonly ClipboardHistory _clipboard;
    private readonly Overdesk.Core.Platform.ISecretStore _secrets = PlatformFactory.CreateSecrets();
    private readonly Overdesk.Core.Platform.IDesktopServices _desktop = PlatformFactory.CreateDesktop();
    private CancellationTokenSource? _toastHide;
    private readonly TabManager _tabs = new();
    private readonly PluginRegistry _plugins = new();
    private readonly EmbeddingService _embedding = new(PlatformFactory.CreateEmbedder());
    // Fast enough that Overdesk follows a dragged program window smoothly; the work per tick is a few cheap reads.
    private readonly DispatcherTimer _layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Dictionary<Guid, CancellationTokenSource> _tabStarts = new();
    private readonly Dictionary<string, CancellationTokenSource> _toolStarts = new();
    private readonly Dictionary<string, string> _toolErrors = new();
    private readonly Dictionary<string, Control> _toolViews = new();

    private PluginManifest? _activeTool;
    private bool _toolPanelOpen;
    private bool _opened;
    private bool _browsing;
    private bool _keepOverdeskFocused;
    private bool _focusMode;
    private int _badgeTicks;
    private bool _dockBeforeFocus = true;
    private int _permissionTicks;

    // Tab drag-reorder state.
    private HostedTab? _dragTab;
    private Point _dragStart;
    private bool _dragging;

    public MainWindow() : this(new SettingsStore(SettingsStore.DefaultDirectory()), PlatformFactory.CreateCatalog())
    {
    }

    public MainWindow(SettingsStore store, IProgramCatalog catalog)
    {
        InitializeComponent();

        _store = store;
        _catalog = catalog;
        _settings = _store.Load();
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        _toolPanelOpen = false;

        TabStrip.ItemsSource = _tabs.Tabs;
        DragDrop.SetAllowDrop(this, true);
        DragDrop.SetAllowDrop(ToolStrip, true);

        var mod = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
        ToolTip.SetTip(AddTabButton, $"Add a program  ({mod}T)");
        ToolTip.SetTip(HomeButton, OperatingSystem.IsMacOS() ? "Home  (⌘⇧H)" : "Home  (Ctrl+Shift+H)");
        ToolTip.SetTip(SettingsButton, $"Settings  ({mod},)");

        _clipboard = new ClipboardHistory(this);
        _ai = new AiService(_settings, _secrets, _store.Directory);
        Dock.Initialize(_settings, _nowPlaying, _stats, _clipboard);
        Dock.InitializeDev(this, _runner, _ai, _desktop);
        Dock.SettingsChanged += () =>
        {
            SaveSettings();
            Settings.Refresh(); // the dock's gear or Cmd+B changed something the settings page shows
        };
        Dock.WidthChanging += _ => SyncLayout();
        Dock.Toast += ShowToast;

        Home.Initialize(_settings, _catalog);
        Home.SetAnimated(_settings.AnimatedBackground);
        OverlayWaves.IsAnimated = _settings.AnimatedBackground;
        StageWaves.IsAnimated = _settings.AnimatedBackground;
        Home.ProgramRequested += OpenProgram;
        Home.AddProgramRequested += () => _ = AddProgramsAsync();
        Home.PinToggled += TogglePin;
        Home.RecentRemoved += path =>
        {
            _settings.RecentPrograms.RemoveAll(p => ProgramHistory.SamePath(p, path));
            SaveSettings();
            Home.RebuildTiles();
        };

        Picker.BrowseRequested += BrowseForProgram;
        TaskLog.AskAi += question => OpenAi(question: question);
        TaskLog.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !TaskLog.IsVisible) RestoreEmbedded();
        };
        Palette.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !Palette.IsVisible) RestoreEmbedded();
        };
        Picker.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !Picker.IsVisible && !_browsing) RestoreEmbedded();
        };
        Settings.CloseRequested += CloseSettings;
        Settings.SettingsFilePath = _store.FilePath;
        Settings.Ai = _ai;
        Settings.AddProject = AddProjectAsync;
        Settings.IsGaugeAvailable = id => _stats.Read(id) is not null;
        Settings.Changed += OnSettingsChanged;
        SettingsButton.Click += (_, _) => OpenSettings();
        HomeButton.Click += (_, _) => ShowHome();
        // The logo opens OverDesk's own tab; right-click it for the quick menu.
        LogoButton.Click += (_, _) => OpenSettings();
        LogoButton.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right) ShowMainMenu();
        };

        AddTabButton.Click += async (_, _) => await AddProgramsAsync();
        AddToolButton.Click += async (_, _) => await AddToolsAsync();
        GrantButton.Click += (_, _) => _embedding.Embedder.RequestPermissions();
        ToolCloseButton.Click += (_, _) => CloseToolPanel();
        ToolReloadButton.Click += (_, _) => { if (_activeTool is not null) RestartTool(_activeTool); };
        ToolOverlayAction.Click += (_, _) => { if (_activeTool is not null) RestartTool(_activeTool); };
        OverlayCloseTab.Click += (_, _) => { if (_tabs.Active is { } t) CloseTab(t); };
        OverlayAction.Click += OnOverlayAction;

        TabStrip.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Bubble);
        TabStrip.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Bubble);
        TabStrip.AddHandler(Button.ClickEvent, OnTabButtonClick, RoutingStrategies.Bubble);

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ShowDropOverlay(false));
        AddHandler(DragDrop.DropEvent, OnDropProgram);
        ToolStrip.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        ToolStrip.AddHandler(DragDrop.DropEvent, OnDropTool);

        _tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabManager.Active)) OnActiveTabChanged();
        };
        _tabs.Tabs.CollectionChanged += (_, _) => SaveSettings();
        _plugins.Tools.CollectionChanged += (_, _) => RebuildToolStrip();

        _embedding.Exited += (key, window) => Dispatcher.UIThread.Post(() => OnProgramExited(key, window));
        _layoutTimer.Tick += (_, _) => OnLayoutTick();

        InitializeMirror();
        InitializeWeb();
        InitializeWorkspaces();
        Opened += OnOpened;
        Closing += OnClosing;
        Activated += (_, _) => RaiseHostedWindow();
        Deactivated += (_, _) => _keepOverdeskFocused = false;
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Follow Overdesk immediately when it moves or the content area changes size, not only on the timer.
        PositionChanged += (_, _) => SyncLayout();
        TabHost.SizeChanged += (_, _) => SyncLayout();
        SplitHost.SizeChanged += (_, _) => SyncLayout();
        ToolHost.SizeChanged += (_, _) => SyncLayout();
        UpdateCaptionSpace();
    }

    /// <summary>Fade and lift the window content in (used right after the splash screen).</summary>
    public void PlayEntrance()
    {
        Root.Opacity = 0;
        Animator.Run(this, 0.5, t =>
        {
            var e = Ease.OutCubic(t);
            Root.Opacity = e;
            Root.RenderTransform = new TranslateTransform(0, 10 * (1 - e));
        });
    }

    // Hooks for the development snapshot tool (Services/DevSnapshot).
    internal void DevOpenSettings() => OpenSettings();
    internal void DevCloseSettings() => CloseSettings();
    internal void DevOpenProgram(string path) => OpenProgram(path);
    internal void DevShowHome() => ShowHome();
    internal void DevOpenPicker() => _ = AddProgramsAsync();
    internal void DevOpenPalette() => ShowPalette();
    internal void DevAskAi(string question) => OpenAi(question: question);
    internal string? DevOpenTool(string id)
    {
        if (_plugins.Tools.FirstOrDefault(t => t.Id == id) is not { } tool)
            return "not found; problems: " + string.Join(" | ", _plugins.Problems);
        OpenTool(tool);
        return null;
    }
    internal void DevOpenAi(bool sheet, bool rail)
    {
        OpenAi();
        AiPanel().DevShow(sheet, rail);
    }
    internal void DevShowWorkspaces() => ShowWorkspaceMenu();
    internal void DevScrollDock(double y) => Dock.DevScroll(y);
    internal void DevCloseTools() => CloseToolPanel();
    internal void DevOpenWeb(string serviceId)
    {
        if (WebServices.Find(serviceId) is { } s) OpenWeb(s.Url, s.Name, s.Id);
    }
    internal void DevShowMetricsPicker() => Dock.ShowMetricsPicker();
    internal void DevOpenPlayer() => Dock.SetMediaOpen(true);
    internal void DevClosePalette() => Palette.Hide();

    /// <summary>Pretend the active tab is running, to preview the stage without a real hosted window.</summary>
    internal void DevFakeRunning()
    {
        if (_tabs.Active is not { } tab) return;
        if (_tabStarts.Remove(tab.Id, out var cts)) cts.Cancel();
        tab.IsBusy = false;
        tab.Status = TabStatus.Running;
        UpdateTabContent();
    }
    internal void DevClosePicker() => Picker.Hide();

    /// <summary>Keep our content clear of the OS window buttons that sit inside the extended title bar.</summary>
    private void PrepareForHosting() => _embedding.Embedder.PrepareOwnerWindow(this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

    private void UpdateCaptionSpace()
    {
        if (MacCaptionSpace is null) return; // property changes arrive while XAML is still loading
        var extended = IsExtendedIntoWindowDecorations;
        MacCaptionSpace.Width = extended && OperatingSystem.IsMacOS() && WindowState != WindowState.FullScreen
            ? MacTrafficLightsWidth : 0;
        WinCaptionSpace.Width = extended && !OperatingSystem.IsMacOS() ? WindowsCaptionWidth : 0;
        LogoButton.Margin = new Thickness(MacCaptionSpace.Width > 0 ? 0 : 10, 0, 0, 0);
    }

    // ---------------------------------------------------------------- startup / shutdown

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_opened) return;
        _opened = true;

        PrepareForHosting();
        RegisterBuiltInTools();
        _plugins.Discover(PluginRoots());
        _plugins.AddUserTools(_settings.UserTools);
        RebuildToolStrip();

        RestoreWorkspaceTabs();

        OnActiveTabChanged();
        UpdatePermissionBanner();
        _layoutTimer.Start();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _layoutTimer.Stop();
        SaveSettings();
        foreach (var cts in _tabStarts.Values.Concat(_toolStarts.Values)) cts.Cancel();
        _embedding.Dispose();
        _nowPlaying.Dispose();
        _runner.StopAll();
        _stats.Dispose();
    }

    /// <summary>Plugin folders: repo/app "tools" folder (found by walking up) and the per-user "plugins" folder.</summary>
    private IEnumerable<string> PluginRoots()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var tools = Path.Combine(dir.FullName, "tools");
            if (Directory.Exists(tools))
            {
                yield return tools;
                break;
            }
        }
        yield return Path.Combine(_store.Directory, "plugins");
    }

    private void SaveSettings()
    {
        if (!_opened) return;
        // Overdesk's own tab (settings) is not a program and is not restored.
        SaveWorkspaceTabs();
        _settings.UserTools = _plugins.Tools.Where(t => t.IsUserAdded)
            .Select(t => new UserTool { Name = t.Name, Path = t.Entry! }).ToList();
        if (_toolPanelOpen && ToolPanel.Bounds.Width >= MinToolPanelWidth)
            _settings.ToolsPanelWidth = ToolPanel.Bounds.Width;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _store.Save(_settings);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsExtendedIntoWindowDecorationsProperty)
            UpdateCaptionSpace();
        if (_opened && change.Property == WindowStateProperty) PrepareForHosting();
        // macOS full screen puts Overdesk in its own Space, where hosted programs can never follow (and the user
        // ended up stuck there). Turn it into focus mode instead once the full-screen animation is over.
        if (_opened && change.Property == WindowStateProperty && WindowState == WindowState.FullScreen && OperatingSystem.IsMacOS())
            DispatcherTimer.RunOnce(() =>
            {
                if (WindowState != WindowState.FullScreen) return;
                WindowState = WindowState.Normal;
                DispatcherTimer.RunOnce(() =>
                {
                    _focusMode = false;
                    ToggleFocusMode();
                }, TimeSpan.FromMilliseconds(700));
            }, TimeSpan.FromMilliseconds(900));
        if (_opened && change.Property == WindowStateProperty && WindowState == WindowState.Normal && _focusMode
            && change.OldValue is WindowState.Maximized)
        {
            // Un-zoomed by hand (green button / double-click): that ends focus mode too.
            _focusMode = false;
            Dock.SetExpanded(_dockBeforeFocus, animate: true);
        }
        if (!_opened || change.Property != WindowStateProperty) return;

        if (WindowState == WindowState.Minimized)
            HideAllEmbedded();
        else
            UpdateTabContent();
    }

    // ---------------------------------------------------------------- tabs

    private static string TabKey(HostedTab tab) => "tab:" + tab.Id;
    private static string ToolKey(PluginManifest tool) => "tool:" + tool.Id;

    /// <summary>A short confirmation pill in the top bar that fades away by itself.</summary>
    internal async void ShowToast(string text, string iconKey)
    {
        _toastHide?.Cancel();
        var cts = _toastHide = new CancellationTokenSource();
        ToastText.Text = text;
        ToastIcon.Data = Res.Icon(iconKey);
        if (!ToastHost.IsVisible)
        {
            ToastHost.IsVisible = true;
            Animator.Run(ToastHost, 0.22, t =>
            {
                var e = Ease.OutBack(t);
                ToastHost.Opacity = Math.Clamp(t * 2, 0, 1);
                ToastHost.RenderTransform = new ScaleTransform(0.85 + 0.15 * e, 0.85 + 0.15 * e);
            });
        }
        try { await Task.Delay(1800, cts.Token); }
        catch (TaskCanceledException) { return; }
        Animator.Run(ToastHost, 0.2, t => ToastHost.Opacity = 1 - t, () =>
        {
            if (!cts.IsCancellationRequested) ToastHost.IsVisible = false;
        });
    }

    /// <summary>True while an in-window dialog covers the content; hosted windows are hidden meanwhile.</summary>
    private bool OverlayOpen => Picker.IsVisible || Palette.IsVisible || TaskLog.IsVisible;

    private Task AddProgramsAsync()
    {
        ShowPicker("Add a program", "It opens as a tab in the top bar", OpenProgram);
        return Task.CompletedTask;
    }

    private void ShowPicker(string title, string subtitle, Action<string> onPicked)
    {
        if (OverlayOpen) return;
        Picker.Show(title, subtitle, onPicked);
        HideAllEmbedded();
    }

    private void RestoreEmbedded()
    {
        UpdateTabContent();
        UpdateToolPanel();
        SyncLayout(force: true);
    }

    /// <summary>"Browse…" in the picker: fall back to the OS file dialog for programs outside the list.</summary>
    private async void BrowseForProgram(Action<string>? onPicked)
    {
        // Keep hosted windows hidden while the OS dialog is up so they cannot cover it.
        _browsing = true;
        try
        {
            var paths = await ProgramPicker.PickAsync(this, _embedding.Embedder, "Choose a program");
            foreach (var p in paths) onPicked?.Invoke(p);
        }
        finally
        {
            _browsing = false;
            RestoreEmbedded();
        }
    }

    /// <summary>Open a program as a tab, or switch to its tab when it is already open.</summary>
    private void OpenProgram(string path)
    {
        path = path.TrimEnd('/', '\\');
        ProgramHistory.Touch(_settings.RecentPrograms, path);
        if (_tabs.Tabs.FirstOrDefault(t => ProgramHistory.SamePath(t.ProgramPath, path)) is { } existing)
            _tabs.Activate(existing);
        else
            _tabs.Add(path);
        SaveSettings();
    }

    private void ShowHome()
    {
        _activeWeb = null;
        _tabs.ShowHome();
        UpdateTabContent();
        BuildWebPills();
        SaveSettings();
    }

    private void TogglePin(string path)
    {
        if (_settings.PinnedPrograms.RemoveAll(p => ProgramHistory.SamePath(p, path)) == 0)
        {
            _settings.PinnedPrograms.Add(path);
            ShowToast($"Pinned {ProgramNames.FromPath(path)} to home", "IconPin");
        }
        else
            ShowToast($"Unpinned {ProgramNames.FromPath(path)}", "IconPin");
        SaveSettings();
        Home.RebuildTiles();
    }

    private void CloseTab(HostedTab tab)
    {
        if (_tabStarts.Remove(tab.Id, out var cts)) cts.Cancel();
        _embedding.Stop(TabKey(tab));
        _tabs.Close(tab);
        UpdateTabContent();
        SaveSettings();
    }

    private void CloseOtherTabs(HostedTab keep)
    {
        foreach (var t in _tabs.Tabs.Where(t => t != keep).ToList()) CloseTab(t);
    }

    private void OnActiveTabChanged()
    {
        if (_tabs.Active is not null && _activeWeb is not null)
        {
            _activeWeb = null;
            BuildWebPills();
        }
        UpdateTabContent();
        SaveSettings();
        if (_tabs.Active is { Status: TabStatus.Starting } tab && !_tabStarts.ContainsKey(tab.Id))
            _ = StartTabAsync(tab);
    }

    private async Task StartTabAsync(HostedTab tab)
    {
        var cts = new CancellationTokenSource();
        _tabStarts[tab.Id] = cts;
        tab.Status = TabStatus.Starting;
        tab.Message = null;
        tab.IsBusy = true;
        UpdateTabContent();

        try
        {
            var owner = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            await _embedding.StartAsync(TabKey(tab), tab.ProgramPath, owner, cts.Token);
            tab.Status = TabStatus.Running;
        }
        catch (OperationCanceledException)
        {
            tab.IsBusy = false;
            return; // tab was closed while starting
        }
        catch (EmbeddingException ex)
        {
            tab.Status = TabStatus.Failed;
            tab.Message = ex.Message;
        }
        catch (Exception ex)
        {
            tab.Status = TabStatus.Failed;
            tab.Message = "Unexpected error: " + ex.Message;
        }
        finally
        {
            if (_tabStarts.TryGetValue(tab.Id, out var current) && ReferenceEquals(current, cts))
                _tabStarts.Remove(tab.Id);
            cts.Dispose();
        }

        tab.IsBusy = false;
        UpdateTabContent();
        SyncLayout();
        if (tab.Status == TabStatus.Running && ReferenceEquals(_tabs.Active, tab))
            _embedding.Focus(TabKey(tab));
    }

    private void RestartTab(HostedTab tab)
    {
        if (_tabStarts.Remove(tab.Id, out var cts)) cts.Cancel();
        _embedding.Stop(TabKey(tab));
        tab.Status = TabStatus.Starting;
        tab.Message = null;
        if (ReferenceEquals(_tabs.Active, tab)) _ = StartTabAsync(tab);
        UpdateTabContent();
    }

    private void OnProgramExited(string key, IEmbeddedWindow window)
    {
        if (!_embedding.IsCurrent(key, window)) return;

        if (key.StartsWith("tab:", StringComparison.Ordinal))
        {
            var tab = _tabs.Tabs.FirstOrDefault(t => TabKey(t) == key);
            if (tab is null) return;
            _embedding.Stop(key);
            tab.Status = TabStatus.Exited;
            tab.Message = $"{tab.Title} was closed.";
            UpdateTabContent();
        }
        else
        {
            var tool = _plugins.Tools.FirstOrDefault(t => ToolKey(t) == key);
            if (tool is null) return;
            _embedding.Stop(key);
            _toolErrors[key] = $"{tool.Name} was closed.";
            UpdateToolPanel();
        }
    }

    private void OnOverlayAction(object? sender, RoutedEventArgs e)
    {
        if (_tabs.Active is { } tab) RestartTab(tab);
    }

    /// <summary>Show home, a status card, or nothing (the program's window) for the active tab.</summary>
    private void UpdateTabContent()
    {
        Dispatcher.UIThread.Post(UpdateMirror);
        var tab = _tabs.Active;
        var minimized = WindowState == WindowState.Minimized;
        var covered = OverlayOpen;

        foreach (var t in _tabs.Tabs.Where(t => t != _splitTab))
            _embedding.SetVisible(TabKey(t), !minimized && !covered && ReferenceEquals(t, tab) && t.Status == TabStatus.Running);
        UpdateSplitContent(tab, !minimized && !covered);

        HomeButton.Classes.Set("selected", tab is null && _activeWeb is null);
        SettingsButton.Classes.Set("selected", tab is { IsInternal: true });
        Home.IsVisible = tab is null && _activeWeb is null;
        UpdateWebContent();
        if (tab is { IsInternal: true })
        {
            if (!Settings.IsVisible) Settings.Show(_settings, _embedding.Embedder, animated: true);
        }
        else if (Settings.IsVisible) Settings.Hide();
        OverlayAction.IsVisible = false;
        OverlayCloseTab.IsVisible = false;

        if (tab is null || tab.Status == TabStatus.Running)
        {
            TabOverlay.IsVisible = false;
            return;
        }

        var wasVisible = TabOverlay.IsVisible;
        TabOverlay.IsVisible = true;
        OverlayIcon.ProgramPath = tab.ProgramPath;
        var starting = tab.Status == TabStatus.Starting;
        OverlaySpinner.IsVisible = starting;
        OverlayBadge.IsVisible = !starting;

        switch (tab.Status)
        {
            case TabStatus.Starting:
                OverlayTitle.Text = $"Opening {tab.Title}";
                OverlayText.Text = "Waiting for its window to appear…";
                break;
            case TabStatus.Failed:
                OverlayTitle.Text = $"Couldn't open {tab.Title}";
                OverlayText.Text = tab.Message;
                OverlayAction.Content = "Try again";
                OverlayAction.IsVisible = true;
                OverlayCloseTab.IsVisible = true;
                break;
            case TabStatus.Exited:
                OverlayTitle.Text = $"{tab.Title} was closed";
                OverlayText.Text = "You can start it again or close this tab.";
                OverlayAction.Content = "Reopen";
                OverlayAction.IsVisible = true;
                OverlayCloseTab.IsVisible = true;
                break;
        }

        if (!wasVisible)
        {
            Animator.Run(OverlayCard, 0.35, t =>
            {
                var e = Ease.OutCubic(t);
                OverlayCard.Opacity = e;
                OverlayCard.RenderTransform = new TranslateTransform(0, 12 * (1 - e));
            });
        }
    }

    // Tab strip pointer handling: click to activate, drag to reorder, middle-click or x to close, right-click menu.

    private static HostedTab? TabFromSource(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (v is Border b && b.Classes.Contains("tab") && b.DataContext is HostedTab t)
                return t;
        return null;
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual src && src.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (TabFromSource(e.Source) is not { } tab) return;

        var point = e.GetCurrentPoint(TabStrip);
        if (point.Properties.IsLeftButtonPressed)
        {
            _tabs.Activate(tab);
            _dragTab = tab;
            _dragStart = point.Position;
            _dragging = false;
        }
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab is null || !e.GetCurrentPoint(TabStrip).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(TabStrip);
        if (!_dragging && Math.Abs(pos.X - _dragStart.X) < 6) return;
        _dragging = true;

        var children = TabStrip.ItemsPanelRoot?.Children;
        if (children is null || children.Count == 0) return;
        var target = children.Count - 1;
        for (var i = 0; i < children.Count; i++)
        {
            if (pos.X < children[i].Bounds.Right) { target = i; break; }
        }
        _tabs.Move(_dragTab, target);
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragging = _dragging;
        _dragTab = null;
        _dragging = false;
        if (wasDragging) { SaveSettings(); FocusHostedWindow(); return; }

        if (TabFromSource(e.Source) is not { } tab) return;
        var kind = e.InitialPressMouseButton;
        if (kind == MouseButton.Left && e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is null)
            FocusHostedWindow(); // clicking a program's tab means "let me use that program"
        else if (kind == MouseButton.Middle)
            CloseTab(tab);
        else if (kind == MouseButton.Right)
            ShowTabMenu(tab, (Control)e.Source!);
    }

    private void OnTabButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Tag: "close" } && TabFromSource(e.Source) is { } tab)
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    private void ShowTabMenu(HostedTab tab, Control anchor)
    {
        if (tab.IsInternal)
        {
            new ContextMenu { ItemsSource = new Control[] { MenuEntry("Close tab", "IconClose", () => CloseTab(tab)) } }.Open(anchor);
            return;
        }
        var restart = MenuEntry("Restart program", "IconRefresh", () => RestartTab(tab));
        var canSplit = _tabs.Active is { IsInternal: false } current && current != tab;
        var split = _splitTab == tab
            ? MenuEntry("Close split view", "IconSplit", EndSplit)
            : MenuEntry(canSplit ? $"Split view with {_tabs.Active!.Title}" : "Split view (activate another tab first)", "IconSplit", () => SplitWith(tab));
        split.IsEnabled = _splitTab == tab || canSplit;
        var pinned = _settings.PinnedPrograms.Any(p => ProgramHistory.SamePath(p, tab.ProgramPath));
        var pin = MenuEntry(pinned ? "Unpin from home" : "Pin to home", "IconPin", () => TogglePin(tab.ProgramPath));
        var close = MenuEntry("Close tab", "IconClose", () => CloseTab(tab));
        var others = MenuEntry("Close other tabs", "IconTabs", () => CloseOtherTabs(tab));
        others.IsEnabled = _tabs.Tabs.Count > 1;
        new ContextMenu { ItemsSource = new Control[] { restart, pin, split, new Separator(), close, others } }.Open(anchor);
    }

    private static MenuItem MenuEntry(string header, string icon, Action onClick, string? gesture = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Data = Res.Icon(icon) },
        };
        if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
        item.Click += (_, _) => onClick();
        return item;
    }

    // ---------------------------------------------------------------- main menu, settings

    private void ShowMainMenu()
    {
        var cmd = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        var quit = MenuEntry("Quit OverDesk", "IconPower", Close, $"{cmd}+Q");
        quit.Classes.Add("danger");
        var items = new Control[]
        {
            MenuEntry("Home", "IconHome", ShowHome, $"{cmd}+Shift+H"),
            MenuEntry("Add program…", "IconPlus", () => _ = AddProgramsAsync(), $"{cmd}+T"),
            MenuEntry("Search programs", "IconSearch", FocusHomeSearch, $"{cmd}+K"),
            MenuEntry("Command palette", "IconCommand", ShowPalette, $"{cmd}+P"),
            MenuEntry(_settings.SidebarExpanded ? "Collapse side dock" : "Expand side dock", "IconPanel",
                () => Dock.SetExpanded(!_settings.SidebarExpanded, animate: true), $"{cmd}+B"),
            new Separator(),
            MenuEntry("Settings", "IconSliders", OpenSettings, $"{cmd}+OemComma"),
            MenuEntry("About OverDesk", "IconInfo", OpenSettings),
            new Separator(),
            quit,
        };
        var flyout = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.ShowAt(LogoButton);
    }

    // ---------------------------------------------------------------- command palette

    private void ShowPalette()
    {
        if (OverlayOpen) return;
        Palette.Dynamic = TypedPaletteItems;
        Palette.Show(BuildPaletteItems());
        HideAllEmbedded();
    }

    private List<PaletteItem> BuildPaletteItems()
    {
        var mod = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
        var items = new List<PaletteItem>();

        for (var i = 0; i < _tabs.Tabs.Count; i++)
        {
            var tab = _tabs.Tabs[i];
            items.Add(new PaletteItem($"Switch to {tab.Title}", "Tab", () => _tabs.Activate(tab),
                IconKey: tab.IsInternal ? "IconSliders" : null, ProgramPath: tab.IsInternal ? null : tab.ProgramPath,
                Shortcut: i < 9 ? $"{mod}{i + 1}" : null, Detail: tab.IsInternal ? "Settings" : tab.ProgramPath));
        }

        items.Add(new PaletteItem("Go home", "Action", ShowHome, "IconHome", Shortcut: OperatingSystem.IsMacOS() ? "⌘⇧H" : "Ctrl+Shift+H"));
        items.Add(new PaletteItem("Add a program…", "Action", () => _ = AddProgramsAsync(), "IconPlus", Shortcut: $"{mod}T"));
        items.Add(new PaletteItem("Search programs on this computer", "Action", FocusHomeSearch, "IconSearch", Shortcut: $"{mod}K"));
        items.Add(new PaletteItem("Open settings", "Action", OpenSettings, "IconSliders", Shortcut: $"{mod},"));
        items.Add(new PaletteItem(_settings.SidebarExpanded ? "Collapse side dock" : "Expand side dock", "Action",
            () => Dock.SetExpanded(!_settings.SidebarExpanded, animate: true), "IconPanel", Shortcut: $"{mod}B"));
        if (_tabs.Active is { IsInternal: false } active)
        {
            items.Add(new PaletteItem($"Restart {active.Title}", "Action", () => RestartTab(active), "IconRefresh"));
            items.Add(new PaletteItem($"Close {active.Title}", "Action", () => CloseTab(active), "IconClose", Shortcut: $"{mod}W"));
            var pinned = _settings.PinnedPrograms.Any(p => ProgramHistory.SamePath(p, active.ProgramPath));
            items.Add(new PaletteItem($"{(pinned ? "Unpin" : "Pin")} {active.Title} {(pinned ? "from" : "to")} home", "Action",
                () => TogglePin(active.ProgramPath), "IconPin"));
            if (_tabs.Tabs.Count > 1)
                items.Add(new PaletteItem("Close other tabs", "Action", () => CloseOtherTabs(active), "IconTabs"));
        }
        items.Add(new PaletteItem(_settings.AnimatedBackground ? "Turn off animated background" : "Turn on animated background",
            "Action", () =>
            {
                _settings.AnimatedBackground = !_settings.AnimatedBackground;
                OnSettingsChanged();
            }, "IconSparkle"));
        items.Add(new PaletteItem("Clear recent programs", "Action", () =>
        {
            _settings.RecentPrograms.Clear();
            OnSettingsChanged();
            ShowToast("Recent programs cleared", "IconCheck");
        }, "IconClose"));

        for (var i = 0; i < _settings.Workspaces.Count; i++)
        {
            var ws = _settings.Workspaces[i];
            if (ws.Id == CurrentWorkspace.Id) continue;
            items.Add(new PaletteItem($"Switch to workspace {ws.Name}", "Workspace", () => SwitchWorkspace(ws), "IconTabs",
                Shortcut: i < 9 ? (OperatingSystem.IsMacOS() ? $"⌘⌥{i + 1}" : $"Ctrl+Alt+{i + 1}") : null));
        }
        items.Add(new PaletteItem("New workspace…", "Workspace", ShowWorkspaceMenu, "IconPlus"));

        foreach (var pin in _settings.WebPins)
        {
            var service = WebServices.Find(pin.Service);
            var url = service?.Url ?? pin.Url;
            if (url is null) continue;
            var name = service?.Name ?? pin.Name ?? url;
            items.Add(new PaletteItem($"Open {name}", "Web", () => OpenWeb(url, name, pin.Service ?? pin.Url), "IconGlobe", Detail: url));
        }

        foreach (var theme in AccentThemes.All)
        {
            items.Add(new PaletteItem($"Theme: {theme.Name}", "Theme", () =>
            {
                _settings.AccentTheme = theme.Name;
                AccentThemes.Apply(theme.Name);
                SaveSettings();
                ShowToast($"{theme.Name} theme", "IconSparkle");
            }, "IconSparkle", Detail: theme == AccentThemes.Current ? "Current theme" : null));
        }

        foreach (var port in _stats.Ports.Where(p => p.Port is >= 1024 and < 49152))
        {
            var url = $"http://localhost:{port.Port}";
            items.Add(new PaletteItem($"Open localhost:{port.Port}", "Server",
                () => _ = Launcher.LaunchUriAsync(new Uri(url)), "IconServer", Detail: $"{port.ProcessName} · pid {port.ProcessId}"));
        }

        foreach (var clip in _clipboard.Items)
        {
            var preview = string.Join(' ', clip.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            items.Add(new PaletteItem(preview.Length > 80 ? preview[..80] + "…" : preview, "Clipboard", async () =>
            {
                await _clipboard.CopyAsync(clip);
                ShowToast("Copied to clipboard", "IconCopy");
            }, "IconCopy"));
        }

        if (ProgramLibrary.Installed.IsCompletedSuccessfully)
        {
            foreach (var path in ProgramLibrary.Installed.Result)
                items.Add(new PaletteItem($"Open {ProgramNames.FromPath(path)}", "Program", () => OpenProgram(path), ProgramPath: path, Detail: path));
        }

        foreach (var project in _settings.Projects)
        {
            items.Add(new PaletteItem($"Launch {project.Name}", "Project", () => LaunchProject(project), "IconRocket", Detail: project.Folder));
            items.Add(new PaletteItem($"Commit message for {project.Name} (AI)", "Project", () => _ = CommitMessageWithAiAsync(project.Folder), "IconBranch"));
        }
        items.Add(new PaletteItem("Add a project folder…", "Project", () => _ = AddProjectAsync(), "IconPlus"));
        foreach (var task in _runner.Tasks.Take(8))
            items.Add(new PaletteItem($"Show output: {task.Name}", "Task", () => ShowTask(task), "IconTerminal", Detail: task.IsRunning ? "running" : "finished"));
        foreach (var snippet in _settings.Snippets)
            items.Add(new PaletteItem(snippet.Name, "Snippet", async () =>
            {
                await _clipboard.CopyAsync(snippet.Text);
                ShowToast("Snippet copied", "IconCopy");
            }, "IconPin"));

        if (_tabs.Active is { IsInternal: false } current)
            foreach (var other in _tabs.Tabs.Where(t => t != current && !t.IsInternal))
                items.Add(new PaletteItem($"Split view: {current.Title} | {other.Title}", "View", () => SplitWith(other), "IconSplit"));
        if (_splitTab is not null) items.Add(new PaletteItem("Close split view", "View", EndSplit, "IconSplit"));
        items.Add(new PaletteItem("Take a screenshot (to clipboard)", "Action", _desktop.ScreenshotToClipboard, "IconScissors"));
        items.Add(new PaletteItem("Focus mode (fill the screen)", "View", ToggleFocusMode, "IconTimer"));

        items.Add(new PaletteItem("Open the AI panel", "AI", () => OpenAi(), "IconSparkle"));
        foreach (var prompt in _settings.Prompts)
            items.Add(new PaletteItem($"AI: {prompt.Name}", "AI", () => OpenAi(prompt: prompt.Text), "IconSparkle", Detail: "Uses the clipboard text"));

        items.Add(new PaletteItem("Quit OverDesk", "Action", Close, "IconPower", Shortcut: $"{mod}Q"));
        return items;
    }

    /// <summary>"? something" searches developer docs, "> command" runs a shell command, anything else can go to the AI.</summary>
    private IEnumerable<PaletteItem> TypedPaletteItems(string query)
    {
        if (query.StartsWith('?'))
        {
            var q = query[1..].Trim();
            if (q.Length == 0) yield break;
            yield return new PaletteItem($"Search DevDocs for “{q}”", "Docs", () => OpenWeb("https://devdocs.io/#q=" + Uri.EscapeDataString(q), "DevDocs", "devdocs"), "IconBook");
            yield return new PaletteItem($"Search MDN for “{q}”", "Docs", () => OpenWeb("https://developer.mozilla.org/search?q=" + Uri.EscapeDataString(q), "MDN", "mdn"), "IconBook");
            yield return new PaletteItem($"Search Stack Overflow for “{q}”", "Docs", () => OpenWeb("https://stackoverflow.com/search?q=" + Uri.EscapeDataString(q), "Stack Overflow", "stackoverflow"), "IconBook");
            yield break;
        }
        if (query.StartsWith('>'))
        {
            var command = query[1..].Trim();
            if (command.Length > 0)
                yield return new PaletteItem($"Run: {command}", "Shell", () => RunCommand(command, CurrentProjectFolder() ?? ""), "IconTerminal", Detail: CurrentProjectFolder());
            yield break;
        }
        yield return new PaletteItem($"Ask AI: {query}", "AI", () => OpenAi(question: query), "IconSparkle", Detail: $"{_ai.Provider.Name} · {_ai.Model ?? "default model"}");
    }

    private void FocusHomeSearch()
    {
        ShowHome();
        Dispatcher.UIThread.Post(Home.FocusSearch, DispatcherPriority.Background);
    }

    /// <summary>Settings live in OverDesk's own tab (like a browser's settings page): open it or switch to it.</summary>
    private void OpenSettings()
    {
        if (OverlayOpen) return;
        var tab = _tabs.Tabs.FirstOrDefault(t => t.IsInternal) ?? _tabs.Add(HostedTab.SettingsPath);
        _tabs.Activate(tab);
    }

    private void CloseSettings()
    {
        if (_tabs.Tabs.FirstOrDefault(t => t.IsInternal) is { } tab) CloseTab(tab);
    }

    private void OnSettingsChanged()
    {
        WebBar.IsVisible = _settings.ShowWebStrip;
        BuildWebPills();
        Dock.RefreshDev();
        UpdateMirror();
        Dock.SetExpanded(_settings.SidebarExpanded, animate: true);
        Dock.BuildMetricRows();
        Home.SetAnimated(_settings.AnimatedBackground);
        OverlayWaves.IsAnimated = _settings.AnimatedBackground;
        StageWaves.IsAnimated = _settings.AnimatedBackground;
        Home.RebuildTiles();
        SaveSettings();
    }

    // ---------------------------------------------------------------- drag and drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        if (files)
        {
            // Over the tool strip the drop adds a tool instead of a tab; say so.
            var onTools = ToolStrip.IsPointerOver || ReferenceEquals(sender, ToolStrip);
            DropTitle.Text = onTools ? "Drop to add a tool" : "Drop to open as a tab";
            DropText.Text = onTools
                ? "It will sit in the tools strip on the right and open in the side panel."
                : "Any program works: it will live in OverDesk's top bar.";
        }
        e.Handled = true;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File)) ShowDropOverlay(true);
    }

    private void ShowDropOverlay(bool show)
    {
        if (DropOverlay.IsVisible == show) return;
        DropOverlay.IsVisible = true;
        Animator.Run(DropOverlay, 0.18, t =>
        {
            DropOverlay.Opacity = show ? t : 1 - t;
            var s = show ? 0.98 + 0.02 * Ease.OutCubic(t) : 1;
            DropFrame.RenderTransform = new ScaleTransform(s, s);
        }, () => DropOverlay.IsVisible = show);
    }

    private void OnDropProgram(object? sender, DragEventArgs e)
    {
        ShowDropOverlay(false);
        foreach (var path in DroppedPaths(e)) OpenProgram(path);
        e.Handled = true;
    }

    private void OnDropTool(object? sender, DragEventArgs e)
    {
        ShowDropOverlay(false);
        foreach (var path in DroppedPaths(e)) AddTool(path);
        e.Handled = true;
    }

    private static IEnumerable<string> DroppedPaths(DragEventArgs e)
        => (e.DataTransfer.TryGetFiles() ?? Array.Empty<Avalonia.Platform.Storage.IStorageItem>())
            .Select(f => f.TryGetLocalPath()).OfType<string>().ToList();

    // ---------------------------------------------------------------- keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && Palette.IsVisible)
        {
            Palette.Hide();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && Picker.IsVisible)
        {
            Picker.Hide();
            e.Handled = true;
            return;
        }
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!command) return;

        switch (e.Key)
        {
            case Key.K:
                FocusHomeSearch();
                e.Handled = true;
                break;
            case Key.P:
                ShowPalette();
                e.Handled = true;
                break;
            case Key.B:
                Dock.SetExpanded(!_settings.SidebarExpanded, animate: true);
                e.Handled = true;
                break;
            case Key.OemComma:
                if (_tabs.Active is { IsInternal: true }) CloseSettings(); else OpenSettings();
                e.Handled = true;
                break;
            case Key.H when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                ShowHome();
                e.Handled = true;
                break;
            case Key.T:
                _ = AddProgramsAsync();
                e.Handled = true;
                break;
            case Key.W when _tabs.Active is { } active:
                CloseTab(active);
                e.Handled = true;
                break;
            case Key.Tab when _tabs.Active is null:
                if (_tabs.Tabs.Count > 0) _tabs.Activate(_tabs.Tabs[e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? ^1 : 0]);
                e.Handled = true;
                break;
            case Key.Tab:
                _tabs.ActivateNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9 when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                SwitchWorkspaceByIndex(e.Key - Key.D1);
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9:
                var index = e.Key - Key.D1;
                if (index < _tabs.Tabs.Count) _tabs.Activate(_tabs.Tabs[index]);
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- tools

    private Task AddToolsAsync()
    {
        ShowPicker("Add a tool", "It sits in the tools strip and opens in the side panel", AddTool);
        return Task.CompletedTask;
    }

    private void AddTool(string path)
    {
        path = path.TrimEnd('/', '\\');
        if (_plugins.Tools.Any(t => t.Entry == path)) return;
        var manifest = _plugins.AddUserTool(new UserTool { Name = ProgramNames.FromPath(path), Path = path });
        SaveSettings();
        OpenTool(manifest);
    }

    private void RemoveTool(PluginManifest tool)
    {
        if (_toolStarts.Remove(ToolKey(tool), out var cts)) cts.Cancel();
        _embedding.Stop(ToolKey(tool));
        _toolViews.Remove(ToolKey(tool));
        _toolErrors.Remove(ToolKey(tool));
        if (_activeTool == tool) CloseToolPanel();
        _plugins.Remove(tool);
        SaveSettings();
    }

    private void RebuildToolStrip()
    {
        ToolButtons.Children.Clear();
        foreach (var tool in _plugins.Tools)
        {
            var button = new Button
            {
                Classes = { "tool" },
                Content = tool.Icon is { } iconKey
                    ? new Avalonia.Controls.Shapes.Path { Classes = { "icon", "large" }, Data = Res.Icon(iconKey), Height = 19 }
                    : tool.Run == PluginRunMode.Process && tool.ResolvedEntry is { } entry
                    ? new AppIcon { ProgramPath = entry, Width = 24, Height = 24 }
                    : new TextBlock
                    {
                        Text = tool.Name.Length > 0 ? tool.Name[..1].ToUpperInvariant() : "?",
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 16,
                    },
            };
            button.Classes.Set("active", _toolPanelOpen && _activeTool == tool);
            ToolTip.SetTip(button, tool.Name);
            button.Click += (_, _) => ToggleTool(tool);
            button.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Right) ShowToolMenu(tool, button);
            };
            ToolButtons.Children.Add(button);
        }
    }

    private void ShowToolMenu(PluginManifest tool, Control anchor)
    {
        var items = new List<MenuItem> { MenuEntry("Restart tool", "IconRefresh", () => RestartTool(tool)) };
        if (tool.IsUserAdded)
            items.Add(MenuEntry("Remove from tools", "IconClose", () => RemoveTool(tool)));
        new ContextMenu { ItemsSource = items }.Open(anchor);
    }

    private void ToggleTool(PluginManifest tool)
    {
        if (_toolPanelOpen && _activeTool == tool)
            CloseToolPanel();
        else
            OpenTool(tool);
    }

    private void OpenTool(PluginManifest tool)
    {
        if (_activeTool is not null && _activeTool != tool)
            _embedding.SetVisible(ToolKey(_activeTool), false);

        _activeTool = tool;
        if (!_toolPanelOpen)
        {
            _toolPanelOpen = true;
            var column = ((Grid)ToolPanel.Parent!).ColumnDefinitions[3];
            column.MinWidth = MinToolPanelWidth;
            column.Width = new GridLength(Math.Max(_settings.ToolsPanelWidth, MinToolPanelWidth));
            ToolSplitter.IsVisible = true;
        }

        RebuildToolStrip();
        ToolTitle.Text = tool.Name;
        UpdateToolPanel();

        var key = ToolKey(tool);
        if (tool.Run == PluginRunMode.Process && !_embedding.Has(key) && !_toolStarts.ContainsKey(key)
            && !_toolErrors.ContainsKey(key))
            _ = StartToolAsync(tool);
    }

    private void CloseToolPanel()
    {
        if (!_toolPanelOpen) return;
        SaveSettings();
        _toolPanelOpen = false;
        var column = ((Grid)ToolPanel.Parent!).ColumnDefinitions[3];
        column.MinWidth = 0;
        column.Width = new GridLength(0);
        ToolSplitter.IsVisible = false;
        if (_activeTool is not null) _embedding.SetVisible(ToolKey(_activeTool), false);
        RebuildToolStrip();
    }

    private void RestartTool(PluginManifest tool)
    {
        var key = ToolKey(tool);
        if (_toolStarts.Remove(key, out var cts)) cts.Cancel();
        _embedding.Stop(key);
        _toolErrors.Remove(key);
        _toolViews.Remove(key);
        UpdateToolPanel();
        if (tool.Run == PluginRunMode.Process) _ = StartToolAsync(tool);
    }

    private async Task StartToolAsync(PluginManifest tool)
    {
        var key = ToolKey(tool);
        var cts = new CancellationTokenSource();
        _toolStarts[key] = cts;
        _toolErrors.Remove(key);
        UpdateToolPanel();

        try
        {
            var owner = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            await _embedding.StartAsync(key, tool.ResolvedEntry!, owner, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (EmbeddingException ex)
        {
            _toolErrors[key] = ex.Message;
        }
        catch (Exception ex)
        {
            _toolErrors[key] = "Unexpected error: " + ex.Message;
        }
        finally
        {
            if (_toolStarts.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                _toolStarts.Remove(key);
            cts.Dispose();
        }

        UpdateToolPanel();
        SyncLayout();
    }

    /// <summary>Decide what the tool panel body shows: embedded program, in-process view, or a message.</summary>
    private void UpdateToolPanel()
    {
        if (_activeTool is not { } tool) return;
        var key = ToolKey(tool);

        ToolOverlay.IsVisible = false;
        ToolOverlayAction.IsVisible = false;
        ToolViewHost.IsVisible = false;

        if (tool.Run == PluginRunMode.InProcess)
        {
            try
            {
                if (!_toolViews.TryGetValue(key, out var view))
                {
                    view = PluginRegistry.LoadInProcess(tool).CreateView() as Control
                           ?? throw new InvalidOperationException("The plugin did not return an Avalonia control.");
                    _toolViews[key] = view;
                }
                ToolViewHost.Content = view;
                ToolViewHost.IsVisible = true;
            }
            catch (Exception ex)
            {
                ShowToolMessage($"{tool.Name} could not be loaded: {ex.Message}", retry: true);
            }
            return;
        }

        var running = _embedding.Has(key);
        _embedding.SetVisible(key, _toolPanelOpen && WindowState != WindowState.Minimized && !OverlayOpen && running);

        if (running) return;
        if (_toolErrors.TryGetValue(key, out var error))
            ShowToolMessage(error, retry: true);
        else
            ShowToolMessage($"Starting {tool.Name}…", retry: false);
    }

    private void ShowToolMessage(string text, bool retry)
    {
        ToolOverlayText.Text = text;
        ToolOverlayAction.IsVisible = retry;
        ToolOverlay.IsVisible = true;
    }

    // ---------------------------------------------------------------- layout tracking

    private void OnLayoutTick()
    {
        FollowHostedWindow();

        // Twice a second re-send the bounds even if unchanged: on macOS this undoes size/position changes the
        // program made by itself (changes the user makes by hand are followed instead, see FollowHostedWindow).
        var halfSecond = _permissionTicks % 12 == 0;
        SyncLayout(force: halfSecond);
        if (halfSecond) CheckLostWindows();
        if (_permissionTicks == 0 && ++_badgeTicks % 4 == 0) _ = UpdateBadgesAsync(); // every ~4 s

        // Re-check the OS permission about once a second so the banner disappears after the user grants it.
        if (++_permissionTicks >= 24)
        {
            _permissionTicks = 0;
            UpdatePermissionBanner();
        }
    }

    /// <summary>
    /// The program's window and Overdesk behave as one: dragging the program's title bar moves Overdesk with it,
    /// resizing the program resizes Overdesk, and the program's full-screen button switches Overdesk to focus mode.
    /// </summary>
    private void FollowHostedWindow()
    {
        if (!_opened || OverlayOpen || WindowState == WindowState.Minimized) return;
        if (_tabs.Active is not { Status: TabStatus.Running } tab) return;
        if (_embedding.TakeUserChange(TabKey(tab)) is not { } change) return;

        if (change.WantsFullScreen)
        {
            ToggleFocusMode();
            return;
        }

        // Window.Position uses the same desktop units the platform layer reports (points on macOS).
        Position = new PixelPoint(Position.X + (int)Math.Round(change.DeltaX), Position.Y + (int)Math.Round(change.DeltaY));
        if (Math.Abs(change.DeltaWidth) > 0.5 || Math.Abs(change.DeltaHeight) > 0.5)
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            Width = Math.Max(MinWidth, Width + change.DeltaWidth);
            Height = Math.Max(MinHeight, Height + change.DeltaHeight);
        }
    }

    /// <summary>
    /// Overdesk's answer to "full screen": fill the screen (zoom) and fold the dock away, while staying on the
    /// normal desktop so hosted programs can stay with it. macOS full screen would move Overdesk into its own
    /// Space where other apps' windows cannot follow.
    /// </summary>
    private void ToggleFocusMode()
    {
        _focusMode = !_focusMode;
        if (_focusMode)
        {
            _dockBeforeFocus = _settings.SidebarExpanded;
            Dock.SetExpanded(false, animate: true);
            WindowState = WindowState.Maximized;
            ShowToast("Focus mode · press full screen again to leave", "IconSparkle");
        }
        else
        {
            WindowState = WindowState.Normal;
            Dock.SetExpanded(_dockBeforeFocus, animate: true);
            ShowToast("Left focus mode", "IconSparkle");
        }
    }

    /// <summary>The user closed a hosted program's window itself (e.g. Cmd+W inside it): show the "closed" card.</summary>
    private void CheckLostWindows()
    {
        foreach (var key in _embedding.FindLostWindows())
        {
            if (_tabs.Tabs.FirstOrDefault(t => TabKey(t) == key) is not { } tab) continue;
            _embedding.Stop(key);
            tab.Status = TabStatus.Exited;
            tab.Message = $"{tab.Title}'s window was closed.";
            UpdateTabContent();
        }
    }

    private void UpdatePermissionBanner()
    {
        var help = _embedding.Embedder.HasPermissions ? null : _embedding.Embedder.PermissionHelp;
        PermissionBanner.IsVisible = help is not null;
        PermissionText.Text = help;
    }

    /// <summary>Send the current on-screen position of the content and tool regions to the embedded programs.</summary>
    private void SyncLayout(bool force = false)
    {
        if (!_opened || WindowState == WindowState.Minimized || OverlayOpen) return;

        if (_tabs.Active is { Status: TabStatus.Running, IsInternal: false } tab && Measure(TabHost) is { } tabBounds)
            _embedding.Layout(TabKey(tab), tabBounds, force);
        if (_splitTab is { Status: TabStatus.Running } split && Measure(SplitHost) is { } splitBounds)
            _embedding.Layout(TabKey(split), splitBounds, force);

        if (_toolPanelOpen && _activeTool is { Run: PluginRunMode.Process } tool && Measure(ToolHost) is { } toolBounds)
            _embedding.Layout(ToolKey(tool), toolBounds, force);
    }

    private HostBounds? Measure(Control control)
    {
        var top = TopLevel.GetTopLevel(control);
        if (top is null || control.Bounds.Width < 1 || control.Bounds.Height < 1) return null;
        if (control.TranslatePoint(default, top) is not { } client) return null;

        var scale = top.RenderScaling;
        var screen = control.PointToScreen(default);
        return new HostBounds(
            (int)Math.Round(client.X * scale), (int)Math.Round(client.Y * scale),
            screen.X, screen.Y,
            (int)Math.Round(control.Bounds.Width * scale), (int)Math.Round(control.Bounds.Height * scale),
            scale);
    }

    /// <summary>
    /// On macOS the hosted window is a separate app window, so it drops behind Overdesk whenever Overdesk is
    /// activated (clicking its tab, the empty top bar, Cmd+Tab). Hand the front back to the program right away,
    /// unless the click was meant for Overdesk itself (logo menu, +, settings, tools...), which must keep focus.
    /// </summary>
    private async void RaiseHostedWindow()
    {
        // Let the click that activated us arrive first so we know what it was aimed at.
        await Task.Delay(60);
        if (_keepOverdeskFocused) return;
        FocusHostedWindow();
    }

    private void FocusHostedWindow()
    {
        if (!_opened || WindowState == WindowState.Minimized || OverlayOpen) return;
        if (_tabs.Active is { Status: TabStatus.Running } tab)
            _embedding.Focus(TabKey(tab));
    }

    /// <summary>Remember whether the latest click targets Overdesk's own controls (true) or the program's tab/area (false).</summary>
    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var onTab = TabFromSource(e.Source) is not null;
        var onButton = e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null;
        _keepOverdeskFocused = (onButton && !onTab) || !TopBar.IsPointerOver || OverlayOpen
                               || e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
    }

    private void HideAllEmbedded()
    {
        WebHosts.IsVisible = false; // native web views would draw over in-window dialogs
        foreach (var t in _tabs.Tabs) _embedding.SetVisible(TabKey(t), false);
        foreach (var t in _plugins.Tools) _embedding.SetVisible(ToolKey(t), false);
    }
}
