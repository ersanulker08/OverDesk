using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Platform;
using Overdesk.Core.Settings;
using Overdesk.Core.Tabs;

namespace Overdesk.App;

/// <summary>
/// The web strip: websites and web apps (GitHub, ChatGPT, Codex, Claude, Gemini...) live in their own row under the
/// program tabs. A pill opens the service's desktop app when it is installed, otherwise the website, inside
/// Overdesk's stage in a real native web view (WebKit / WebView2), so it never falls behind like a hosted program.
/// </summary>
public partial class MainWindow
{
    private readonly IWebViewFactory _webFactory = PlatformFactory.CreateWebViews();
    private readonly List<WebTab> _webTabs = new();
    private readonly Dictionary<Guid, WebViewHost> _webHosts = new();
    private readonly DispatcherTimer _webPoll = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private WebTab? _activeWeb;

    /// <summary>Popup windows (sign-in) per web tab, newest last; shown in place of the page.</summary>
    private readonly Dictionary<Guid, List<WebViewHost>> _popups = new();

    private void InitializeWeb()
    {
        WebBar.IsVisible = _settings.ShowWebStrip;
        AddSiteButton.Click += (_, _) => ShowAddSiteMenu();
        WebBack.Click += (_, _) => ActiveWebView()?.GoBack();
        WebForward.Click += (_, _) => ActiveWebView()?.GoForward();
        WebReload.Click += (_, _) => ActiveWebView()?.Reload();
        WebOpenExternal.Click += (_, _) =>
        {
            if ((ActiveWebView()?.Url ?? _activeWeb?.Url) is { } url) _ = Launcher.LaunchUriAsync(new Uri(url));
        };
        WebCloseTab.Click += (_, _) => { if (_activeWeb is { } web) CloseWeb(web); };
        PopupClose.Click += (_, _) => { if (_activeWeb is { } web) ClosePopup(web, null); };
        WebAddress.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _activeWeb is null) return;
            var text = WebAddress.Text?.Trim() ?? "";
            var url = text.Contains(' ') || !text.Contains('.') ? null : WebServices.NormalizeUrl(text);
            ActiveWebView()?.Navigate(url ?? "https://www.google.com/search?q=" + Uri.EscapeDataString(text));
            Focus(); // leave the address box so it shows the page again
            e.Handled = true;
        };
        _webPoll.Tick += (_, _) => PollActiveWeb();
        _webPoll.Start();
        ProgramLibrary.Installed.ContinueWith(_ => Dispatcher.UIThread.Post(BuildWebPills), TaskScheduler.Default);
        BuildWebPills();
    }

    private IWebView? ActiveWebView() => _activeWeb is { } w && _webHosts.TryGetValue(w.Id, out var host) ? host.View : null;

    // ---------------------------------------------------------------- pills

    private void BuildWebPills()
    {
        WebPills.Children.Clear();
        var installed = ProgramLibrary.Installed.IsCompletedSuccessfully ? ProgramLibrary.Installed.Result : Array.Empty<string>();
        foreach (var pin in _settings.WebPins)
        {
            var service = WebServices.Find(pin.Service);
            var url = service?.Url ?? pin.Url;
            if (url is null) continue;
            var name = service?.Name ?? pin.Name ?? new Uri(url).Host;
            var app = service is null || pin.PreferWebsite ? null : WebServices.InstalledApp(service, installed);
            var tab = _webTabs.FirstOrDefault(t => t.ServiceId == (pin.Service ?? url));
            WebPills.Children.Add(Pill(pin, name, url, service?.Brand ?? AccentThemes.Current.Primary, app, tab));
        }
        // Sites opened without a pin (a localhost port, a link from the palette...) show up at the end.
        foreach (var tab in _webTabs.Where(t => !_settings.WebPins.Any(p => (p.Service ?? p.Url) == t.ServiceId)))
            WebPills.Children.Add(Pill(null, tab.Title, tab.Url, AccentThemes.Current.Primary, null, tab));
    }

    private Button Pill(WebPin? pin, string name, string url, Color brand, string? app, WebTab? tab)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var icon = new Panel { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        if (app is not null) icon.Children.Add(new AppIcon { ProgramPath = app });
        else
        {
            var letter = new Border
            {
                CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(AccentThemes.WithAlpha(brand, 0xCC)),
                Child = new TextBlock
                {
                    Text = name[..1].ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var image = new Image { Width = 16, Height = 16 };
            image.Source = FaviconCache.Get(url, b =>
            {
                image.Source = b;
                letter.IsVisible = b is null;
            });
            letter.IsVisible = image.Source is null;
            icon.Children.Add(letter);
            icon.Children.Add(image);
        }
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Classes = { "name" }, Text = tab?.Title is { Length: > 0 } t && pin is null ? Shorten(t) : name });
        if (app is not null)
            row.Children.Add(new Border
            {
                Classes = { "chip" }, Padding = new Thickness(4, 0), Margin = new Thickness(-2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "APP", FontSize = 8.5, FontWeight = FontWeight.Bold },
            });

        var pill = new Button { Classes = { "webpill" }, Content = row };
        var active = tab is not null && tab == _activeWeb;
        pill.Classes.Set("open", tab is not null);
        pill.Classes.Set("active", active);
        if (tab is not null)
        {
            // Open sites wear their brand color; the shown one glows.
            pill.Background = new SolidColorBrush(AccentThemes.WithAlpha(brand, (byte)(active ? 0x40 : 0x1E)));
            pill.BorderBrush = new SolidColorBrush(AccentThemes.WithAlpha(brand, (byte)(active ? 0xB0 : 0x50)));
        }
        ToolTip.SetTip(pill, app is not null ? $"{name} (desktop app)\nRight-click to open the website instead" : url);

        pill.Click += (_, _) =>
        {
            if (tab is not null) ShowWeb(tab);
            else if (app is not null) OpenProgram(app);
            else OpenWeb(url, name, pin?.Service ?? pin?.Url ?? url);
        };
        pill.ContextMenu = PillMenu(pin, name, url, app, tab);
        return pill;
    }

    private static string Shorten(string title) => title.Length > 22 ? title[..21] + "…" : title;

    private ContextMenu PillMenu(WebPin? pin, string name, string url, string? app, WebTab? tab)
    {
        var items = new List<Control>();
        var service = WebServices.Find(pin?.Service);
        if (service is not null && WebServices.InstalledApp(service, ProgramLibrary.Installed.IsCompletedSuccessfully ? ProgramLibrary.Installed.Result : Array.Empty<string>()) is { } installed)
        {
            items.Add(MenuEntry("Open the app", "IconApp", () => OpenProgram(installed)));
            items.Add(MenuEntry("Open the website", "IconGlobe", () => OpenWeb(url, name, pin!.Service!)));
            var always = MenuEntry(pin!.PreferWebsite ? "Prefer the app" : "Always use the website", "IconSliders", () =>
            {
                pin.PreferWebsite = !pin.PreferWebsite;
                SaveSettings();
                BuildWebPills();
            });
            items.Add(always);
            items.Add(new Separator());
        }
        if (tab is not null)
        {
            items.Add(MenuEntry("Reload", "IconRefresh", () => { if (_webHosts.TryGetValue(tab.Id, out var h)) h.View?.Reload(); }));
            items.Add(MenuEntry("Open in your browser", "IconExternal", () => _ = Launcher.LaunchUriAsync(new Uri(tab.Url))));
            items.Add(MenuEntry("Close", "IconClose", () => CloseWeb(tab)));
        }
        if (pin is not null)
        {
            if (items.Count > 0 && items[^1] is not Separator) items.Add(new Separator());
            items.Add(MenuEntry("Remove from the strip", "IconPin", () =>
            {
                _settings.WebPins.Remove(pin);
                SaveSettings();
                BuildWebPills();
            }));
        }
        else if (tab is not null)
        {
            items.Add(MenuEntry("Pin to the strip", "IconPin", () =>
            {
                _settings.WebPins.Add(new WebPin { Name = tab.Title, Url = tab.Url });
                SaveSettings();
                BuildWebPills();
            }));
        }
        return new ContextMenu { ItemsSource = items };
    }

    /// <summary>"+" in the strip: known services that are not pinned yet, or any website.</summary>
    private void ShowAddSiteMenu()
    {
        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(new TextBlock { Text = "ADD TO THE WEB STRIP", Classes = { "eyebrow" }, FontSize = 9.5 });
        var grid = new WrapPanel();
        Flyout? flyout = null;
        foreach (var service in WebServices.All.Where(s => _settings.WebPins.All(p => p.Service != s.Id)))
        {
            var chip = new Button { Classes = { "webpill" }, Margin = new Thickness(0, 0, 6, 6), Content = new TextBlock { Classes = { "name" }, Text = service.Name } };
            chip.Click += (_, _) =>
            {
                _settings.WebPins.Add(new WebPin { Service = service.Id });
                SaveSettings();
                BuildWebPills();
                flyout?.Hide();
            };
            grid.Children.Add(chip);
        }
        if (grid.Children.Count > 0) panel.Children.Add(grid);

        panel.Children.Add(new TextBlock { Text = "ANY WEBSITE", Classes = { "eyebrow" }, FontSize = 9.5, Margin = new Thickness(0, 6, 0, 0) });
        var name = new TextBox { PlaceholderText = "Name (optional)" };
        var address = new TextBox { PlaceholderText = "Address, e.g. linear.app" };
        var add = new Button { Classes = { "accent" }, Content = "Add website", HorizontalAlignment = HorizontalAlignment.Right };
        void AddCustom()
        {
            if (WebServices.NormalizeUrl(address.Text) is not { } url) return;
            _settings.WebPins.Add(new WebPin { Url = url, Name = string.IsNullOrWhiteSpace(name.Text) ? new Uri(url).Host : name.Text.Trim() });
            SaveSettings();
            BuildWebPills();
            flyout?.Hide();
        }
        add.Click += (_, _) => AddCustom();
        address.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddCustom(); };
        panel.Children.Add(name);
        panel.Children.Add(address);
        panel.Children.Add(add);

        flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.ShowAt(AddSiteButton);
    }

    // ---------------------------------------------------------------- tabs

    /// <summary>Open (or switch to) a website inside Overdesk. <paramref name="key"/> groups it with its pin.</summary>
    internal void OpenWeb(string url, string title, string? key = null)
    {
        if (!_webFactory.IsSupported)
        {
            _ = Launcher.LaunchUriAsync(new Uri(url));
            return;
        }
        key ??= url;
        var tab = _webTabs.FirstOrDefault(t => t.ServiceId == key);
        if (tab is null)
        {
            tab = new WebTab(url, title, key);
            _webTabs.Add(tab);
            var host = new WebViewHost(_webFactory, url) { IsVisible = false };
            var owner = tab;
            host.PopupRequested += popup => ShowPopup(owner, popup);
            _webHosts[tab.Id] = host;
            WebHosts.Children.Add(host);
        }
        ShowWeb(tab);
    }

    private void ShowWeb(WebTab tab)
    {
        _activeWeb = tab;
        foreach (var t in _webTabs) t.IsActive = t == tab;
        if (_tabs.Active is not null) _tabs.ShowHome(); // the program strip shows nothing selected
        UpdateTabContent();
        BuildWebPills();
        SaveSettings();
    }

    private void ShowPopup(WebTab tab, Overdesk.Core.Platform.IWebView popup)
    {
        var host = new WebViewHost(popup) { IsVisible = false };
        host.CloseRequested += () => Dispatcher.UIThread.Post(() => ClosePopup(tab, host));
        // A popup can open its own popup (rare, but sign-in providers do it).
        host.PopupRequested += inner => ShowPopup(tab, inner);
        if (!_popups.TryGetValue(tab.Id, out var list)) _popups[tab.Id] = list = new List<WebViewHost>();
        list.Add(host);
        WebHosts.Children.Add(host);
        if (_activeWeb != tab) ShowWeb(tab);
        else UpdateWebContent();
    }

    /// <summary>Close one popup (or the newest when <paramref name="host"/> is null) and show what is under it.</summary>
    private void ClosePopup(WebTab tab, WebViewHost? host)
    {
        if (!_popups.TryGetValue(tab.Id, out var list) || list.Count == 0) return;
        host ??= list[^1];
        list.Remove(host);
        WebHosts.Children.Remove(host);
        host.DisposeView();
        UpdateWebContent();
    }

    private void CloseWeb(WebTab tab)
    {
        if (_popups.Remove(tab.Id, out var popups))
            foreach (var p in popups)
            {
                WebHosts.Children.Remove(p);
                p.DisposeView();
            }
        if (_webHosts.Remove(tab.Id, out var host))
        {
            WebHosts.Children.Remove(host);
            host.DisposeView();
        }
        _webTabs.Remove(tab);
        if (_activeWeb == tab)
        {
            _activeWeb = null;
            UpdateTabContent();
        }
        BuildWebPills();
    }

    /// <summary>Called from UpdateTabContent: show the active website, or hide the web area.</summary>
    private void UpdateWebContent()
    {
        var web = _activeWeb;
        WebArea.IsVisible = web is not null;
        WebWaves.IsAnimated = web is not null && _settings.AnimatedBackground;
        // Native views draw above everything in the window: keep them out of the way of in-window dialogs.
        WebHosts.IsVisible = !OverlayOpen;
        // The newest popup of the shown site covers the page until it closes.
        var popup = web is not null && _popups.TryGetValue(web.Id, out var list) && list.Count > 0 ? list[^1] : null;
        foreach (var (id, host) in _webHosts) host.IsVisible = web is not null && id == web.Id && popup is null;
        foreach (var p in _popups.Values.SelectMany(l => l)) p.IsVisible = p == popup;
        PopupBanner.IsVisible = popup is not null;
        if (popup?.View?.Url is { } popupUrl && Uri.TryCreate(popupUrl, UriKind.Absolute, out var u)) PopupTitle.Text = u.Host;
        else if (popup is not null) PopupTitle.Text = "Sign-in window";
        if (web is not null) PollActiveWeb();
    }

    private void PollActiveWeb()
    {
        if (_activeWeb is not { } tab || ActiveWebView() is not { } view) return;
        var url = view.Url;
        if (url is { Length: > 0 })
        {
            tab.Url = url;
            if (!WebAddress.IsFocused) WebAddress.Text = url;
            WebFavicon.Source = FaviconCache.Get(url, b => WebFavicon.Source = b);
        }
        if (view.Title is { Length: > 0 } title && title != tab.Title)
        {
            tab.Title = title;
            if (!_settings.WebPins.Any(p => (p.Service ?? p.Url) == tab.ServiceId)) BuildWebPills();
        }
        var loading = view.IsLoading;
        var progress = loading ? Math.Max(view.Progress, 0.08) : 0;
        WebProgress.Width = Math.Max(0, WebFrame.Bounds.Width - 2) * progress;
        WebProgress.IsVisible = loading;
    }
}
