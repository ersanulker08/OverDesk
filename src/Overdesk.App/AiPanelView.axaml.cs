using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Overdesk.AI;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Settings;

namespace Overdesk.App;

/// <summary>
/// The AI panel (first built-in tool of the right-hand tools panel): one chat for local models (Ollama, LM Studio)
/// and cloud ones (Claude, OpenAI, Gemini), with the clipboard or a screenshot of the program on stage as context,
/// a prompt library and quick actions.
/// </summary>
public partial class AiPanelView : UserControl, Overdesk.Core.Plugins.IFileDropTarget
{
    private readonly List<(string Label, byte[] Png)> _pendingImages = new();
    private readonly List<(string Name, string Text)> _pendingFiles = new();
    private string? _pendingClipboard;
    private AiService? _ai;
    private AppSettings _settings = new();

    public AiPanelView()
    {
        InitializeComponent();
        ModelButton.Click += (_, _) => ToggleModelSheet();
        CloseSheet.Click += (_, _) => HideModelSheet();
        NewChatButton.Click += (_, _) => StartNewChat();
        ChatMenuButton.Click += (_, _) => ShowChatMenu();
        RailNewChat.Click += (_, _) => StartNewChat();
        HistoryRail.PointerEntered += (_, _) => SetRail(true);
        HistoryRail.PointerExited += (_, _) => SetRail(false);
        SendButton.Click += (_, _) => SendOrStop();
        AttachClipboard.Click += async (_, _) => await AttachClipboardAsync();
        AttachScreenshot.Click += async (_, _) => await AttachScreenshotAsync();
        PromptsButton.Click += (_, _) => ShowPrompts(PromptsButton);
        FolderButton.Click += (_, _) => ShowFolderMenu();
        EditModeButton.Click += (_, _) =>
        {
            _settings.AiAllowEdits = !_settings.AiAllowEdits;
            SettingsChanged?.Invoke();
            RenderFolder();
        };
        Input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            SendOrStop();
        };
    }

    /// <summary>Takes a PNG of the program currently on stage, or null when there is none.</summary>
    internal Func<Task<byte[]?>>? CaptureStage { get; set; }

    internal void Initialize(AiService ai, AppSettings settings)
    {
        _ai = ai;
        _settings = settings;
        ai.Changed += Render;
        BuildQuickActions();
        Render();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (_ai is null) return;
        await _ai.PickStartingProviderAsync();
        await CheckProviderAsync();
    }

    private void StartNewChat()
    {
        _ai?.NewChat();
        SetRail(false);
        HideModelSheet();
        FocusInput();
    }

    /// <summary>Put text in the message box (cursor at the end) for the user to finish.</summary>
    internal void Draft(string text)
    {
        Input.Text = text;
        Input.CaretIndex = text.Length;
        FocusInput();
    }

    public void FocusInput() => Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Background);

    /// <summary>Run a prompt from outside (command palette, quick action), expanding {{clipboard}}.</summary>
    internal async Task RunPromptAsync(string template)
    {
        var clip = template.Contains("{{clipboard}}", StringComparison.OrdinalIgnoreCase) ? await ClipboardTextAsync() : null;
        if (clip is not null && string.IsNullOrWhiteSpace(clip))
        {
            ShowProblem("The clipboard is empty: copy some text first.");
            return;
        }
        if (_ai is not null) await _ai.SendAsync(AiService.Expand(template, clip));
    }

    internal Task AskAsync(string question) => _ai?.SendAsync(question) ?? Task.CompletedTask;

    // ---------------------------------------------------------------- sending

    private void SendOrStop()
    {
        if (_ai is null) return;
        if (_ai.IsBusy)
        {
            _ai.Stop();
            return;
        }
        var text = Input.Text?.Trim() ?? "";
        if (_pendingClipboard is { } clip) text = string.IsNullOrEmpty(text) ? clip : $"{text}\n\n```\n{clip}\n```";
        foreach (var (name, body) in _pendingFiles)
            text += $"\n\nAttached file `{name}`:\n```{Path.GetExtension(name).TrimStart('.')}\n{body}\n```";
        if (string.IsNullOrWhiteSpace(Input.Text) && _pendingFiles.Count > 0 && _pendingClipboard is null) text = "Look at the attached file(s)." + text;
        if (string.IsNullOrWhiteSpace(text) && _pendingImages.Count == 0) return;
        if (string.IsNullOrWhiteSpace(text)) text = "What do you see in this screenshot?";
        var images = _pendingImages.Select(i => i.Png).ToList();
        Input.Text = "";
        _pendingImages.Clear();
        _pendingFiles.Clear();
        _pendingClipboard = null;
        RenderAttachments();
        _ = _ai.SendAsync(text, images);
    }

    private async Task<string?> ClipboardTextAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return null;
        try { return await clipboard.TryGetTextAsync(); }
        catch (Exception) { return null; }
    }

    private async Task AttachClipboardAsync()
    {
        var text = await ClipboardTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowProblem("The clipboard has no text.");
            return;
        }
        _pendingClipboard = text;
        RenderAttachments();
        FocusInput();
    }

    private async Task AttachScreenshotAsync()
    {
        var png = CaptureStage is null ? null : await CaptureStage();
        if (png is null)
        {
            ShowProblem("Open a program on stage first. On macOS the screenshot needs the Screen Recording permission.");
            return;
        }
        _pendingImages.Add(("Screenshot", png));
        RenderAttachments();
        FocusInput();
    }

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
    private const int MaxAttachedChars = 150_000;

    /// <summary>
    /// Dropped files: images and text files are attached to the next message; a folder becomes the project folder.
    /// </summary>
    public string DropHint => "Images and text files are attached to your message. A folder becomes the chat's project folder.";

    public void FilesDropped(IReadOnlyList<string> paths) => _ = AttachPathsAsync(paths);

    internal async Task AttachPathsAsync(IEnumerable<string> paths)
    {
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd('/', '\\'));
            try
            {
                if (Directory.Exists(path))
                {
                    _ai?.SetFolder(path);
                    continue;
                }
                if (ImageTypes.Contains(Path.GetExtension(path)))
                {
                    var png = await Task.Run(() => ImageAsPng(path));
                    if (png is null) skipped.Add(name);
                    else _pendingImages.Add((name, png));
                    continue;
                }
                var text = await Task.Run(() => ReadText(path));
                if (text is null) skipped.Add(name);
                else _pendingFiles.Add((name, text));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(name);
            }
        }
        RenderAttachments();
        if (skipped.Count > 0) ShowProblem($"Not attached (not text or an image, or too large): {string.Join(", ", skipped)}");
        FocusInput();
    }

    /// <summary>Any image as a PNG no larger than vision models read (long edge 1568 px).</summary>
    private static byte[]? ImageAsPng(string path)
    {
        try
        {
            using var bitmap = new Bitmap(path);
            var scale = Math.Min(1.0, 1568.0 / Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height));
            using var scaled = bitmap.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)(bitmap.PixelSize.Width * scale)), Math.Max(1, (int)(bitmap.PixelSize.Height * scale))));
            using var ms = new MemoryStream();
            scaled.Save(ms, new PngBitmapEncoderOptions());
            return ms.ToArray();
        }
        catch (Exception) { return null; }
    }

    private static string? ReadText(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxAttachedChars * 2) return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Take(8000).Contains((byte)0)) return null;
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        return text.Length > MaxAttachedChars ? text[..MaxAttachedChars] + "\n… (cut)" : text;
    }

    private void RenderAttachments()
    {
        Attachments.Children.Clear();
        if (_pendingClipboard is { } clip)
            Attachments.Children.Add(AttachmentChip("IconClipboard", $"Clipboard · {clip.Length} chars", () => { _pendingClipboard = null; RenderAttachments(); }));
        for (var i = 0; i < _pendingFiles.Count; i++)
        {
            var index = i;
            Attachments.Children.Add(AttachmentChip("IconNote", _pendingFiles[i].Name, () => { _pendingFiles.RemoveAt(index); RenderAttachments(); }));
        }
        for (var i = 0; i < _pendingImages.Count; i++)
        {
            var index = i;
            Attachments.Children.Add(AttachmentChip("IconCamera", _pendingImages[i].Label, () => { _pendingImages.RemoveAt(index); RenderAttachments(); }));
        }
    }

    private Control AttachmentChip(string icon, string text, Action remove)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small", "accent" }, Data = Res.Icon(icon) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        var x = new Button { Classes = { "icon" }, Width = 18, Height = 18, Content = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconClose"), Height = 8 } };
        x.Click += (_, _) => remove();
        row.Children.Add(x);
        return new Border { Classes = { "chip" }, Padding = new Thickness(8, 3, 3, 3), Margin = new Thickness(0, 0, 6, 0), Child = row };
    }

    // ---------------------------------------------------------------- chat menu

    /// <summary>Asks the host to keep the conversation as a work (title, markdown).</summary>
    public event Action<string, string>? SaveAsWork;

    private void ShowChatMenu()
    {
        if (_ai is null || _ai.Conversation.Count == 0) return;
        var thread = _ai.Current;
        var menu = new ContextMenu
        {
            ItemsSource = new[]
            {
                MenuEntry("Save as a work", "IconNote", () => SaveAsWork?.Invoke(thread.Title, ChatMarkdownText(thread))),
                MenuEntry("Copy as Markdown", "IconCopy", () => _ = CopyAsync(ChatMarkdownText(thread))),
                MenuEntry("Delete this chat", "IconTrash", () => _ai.Delete(thread)),
            },
        };
        menu.Open(ChatMenuButton);
    }

    private static MenuItem MenuEntry(string text, string icon, Action click)
    {
        var item = new MenuItem { Header = text, Icon = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon(icon) } };
        item.Click += (_, _) => click();
        return item;
    }

    private static string ChatMarkdownText(ChatThread thread)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var turn in thread.Turns)
        {
            sb.AppendLine(turn.Role == ChatMessage.User ? "### You" : "### AI");
            if (turn.Actions.Count > 0) sb.AppendLine(string.Join("\n", turn.Actions.Select(a => "- _" + a + "_"))).AppendLine();
            sb.AppendLine(turn.Error is { } error ? "⚠ " + error : turn.Text).AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------------------------------------------------------------- project folder

    /// <summary>Raised after the panel changed a setting itself (edit mode), so the host saves.</summary>
    public event Action? SettingsChanged;

    private void RenderFolder()
    {
        if (_ai is null) return;
        var folder = _ai.Folder;
        FolderText.Text = folder is null ? "No project folder" : FolderName(folder);
        FolderIcon.Classes.Set("accent", folder is not null);
        FolderButton.Classes.Set("on", folder is not null);
        ToolTip.SetTip(FolderButton, folder ?? "Pick a folder: the AI then knows the project, reads its files and answers about it");
        EditModeButton.IsVisible = folder is not null;
        EditModeText.Text = _settings.AiAllowEdits ? "Can edit files" : "Read only";
        EditModeDot.Fill = Res.Brush(_settings.AiAllowEdits ? "SuccessBrush" : "FaintBrush");
        ToolTip.SetTip(EditModeButton, _settings.AiAllowEdits
            ? "The AI may create and change files in this folder (never outside it, never shell commands). Click for read only."
            : "The AI only reads this folder. Click to let it change files.");
    }

    private static string FolderName(string folder) => Path.GetFileName(folder.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : folder;

    private void ShowFolderMenu()
    {
        if (_ai is null) return;
        var panel = new StackPanel { Spacing = 2, Width = 300 };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.TopEdgeAlignedLeft };
        panel.Children.Add(new TextBlock { Text = "PROJECT FOLDER", Classes = { "eyebrow" }, FontSize = 9.5, Margin = new Thickness(6, 0, 0, 2) });
        panel.Children.Add(new TextBlock
        {
            Text = "The AI learns the project, reads its files and answers about it. It is also added to your projects.",
            Classes = { "faint" }, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 6),
        });

        var current = _ai.Folder;
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string folder)
        {
            folder = folder.TrimEnd('/', '\\');
            if (!Directory.Exists(folder) || !shown.Add(folder)) return;
            panel.Children.Add(FolderRow("IconFolder", FolderName(folder), folder, string.Equals(folder, current, StringComparison.OrdinalIgnoreCase), () =>
            {
                flyout.Hide();
                _ai.SetFolder(folder);
            }));
        }
        if (current is not null) Add(current);
        foreach (var folder in _ai.RecentFolders) Add(folder);
        foreach (var project in _settings.Projects) Add(project.Folder);

        panel.Children.Add(FolderRow("IconPlus", "Choose a folder…", null, false, () =>
        {
            flyout.Hide();
            _ = ChooseFolderAsync();
        }));
        if (current is not null)
            panel.Children.Add(FolderRow("IconClose", "No folder", "Chat without a project", false, () =>
            {
                flyout.Hide();
                _ai.SetFolder(null);
            }));
        flyout.ShowAt(FolderButton);
    }

    private Control FolderRow(string icon, string title, string? detail, bool selected, Action click)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail is not null) text.Children.Add(new TextBlock { Text = detail, Classes = { "faint" }, FontSize = 10.5, TextTrimming = TextTrimming.PrefixCharacterEllipsis });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var glyph = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon(icon), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        glyph.Classes.Set("accent", selected || icon != "IconFolder");
        row.Children.Add(glyph);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (selected)
        {
            var check = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small", "accent" }, Data = Res.Icon("IconCheck"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(check, 2);
            row.Children.Add(check);
        }
        var button = new Button { Classes = { "result" }, Content = row, Padding = new Thickness(8, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => click();
        return button;
    }

    internal void ChooseFolder() => _ = ChooseFolderAsync();

    private async Task ChooseFolderAsync()
    {
        if (_ai is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a project folder for the AI", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder) _ai.SetFolder(folder);
        FocusInput();
    }

    // ---------------------------------------------------------------- model picker

    private async Task CheckProviderAsync()
    {
        if (_ai is null) return;
        if (_ai.Model is null && _ai.Provider == _ai.Ollama && (await _ai.ModelsAsync(_ai.Ollama, CancellationToken.None)).Count == 0
            && await _ai.Ollama.CheckAsync(CancellationToken.None) is null)
        {
            ShowProblem("Ollama is running but has no models yet. Open the model menu above to download one.");
            return;
        }
        var problem = await _ai.Provider.CheckAsync(CancellationToken.None);
        if (problem is null) ProblemBar.IsVisible = false;
        else ShowProblem(problem);
    }

    private void ShowProblem(string text)
    {
        ProblemText.Text = text;
        ProblemBar.IsVisible = true;
    }

    private bool SheetOpen => ModelSheet.IsVisible;

    private void ToggleModelSheet()
    {
        if (SheetOpen) HideModelSheet();
        else ShowModelSheet();
    }

    private void ShowModelSheet()
    {
        if (_ai is null) return;
        BuildModelSheet();
        ModelSheet.IsVisible = true;
        var slide = new TranslateTransform();
        ModelSheet.RenderTransform = slide;
        Animator.Run(ModelSheet, 0.22, t =>
        {
            var e = Ease.OutCubic(t);
            ModelSheet.Opacity = e;
            slide.Y = -10 * (1 - e);
        });
    }

    private void HideModelSheet()
    {
        if (!SheetOpen) return;
        Animator.Run(ModelSheet, 0.14, t => ModelSheet.Opacity = 1 - t, () => ModelSheet.IsVisible = false);
    }

    /// <summary>
    /// Three groups: AI apps already installed (signed in with the user's own account), models on this computer
    /// (with downloads when Ollama has none yet) and cloud APIs (with a shortcut to add the key).
    /// </summary>
    private void BuildModelSheet()
    {
        if (_ai is null) return;
        ModelList.Children.Clear();
        _pullViews.Clear();
        var groups = new (AiSourceKind Kind, string Title, string Caption)[]
        {
            (AiSourceKind.App, "YOUR APPS", "Installed on this computer and signed in with your own account. No API key."),
            (AiSourceKind.Local, "ON THIS COMPUTER", "Runs privately on your machine. Nothing leaves it."),
            (AiSourceKind.Cloud, "CLOUD · API KEY", "Pay-as-you-go keys, stored in the system keychain."),
        };
        foreach (var (kind, title, caption) in groups)
        {
            var head = new StackPanel { Margin = new Thickness(4, ModelList.Children.Count == 0 ? 2 : 12, 4, 2) };
            head.Children.Add(new TextBlock { Text = title, Classes = { "eyebrow" }, FontSize = 9.5 });
            head.Children.Add(new TextBlock { Text = caption, Classes = { "faint" }, FontSize = 10.5, TextWrapping = TextWrapping.Wrap });
            ModelList.Children.Add(head);
            foreach (var provider in _ai.Providers.Where(p => AiService.KindOf(p) == kind))
            {
                var body = new StackPanel { Spacing = 8 };
                ModelList.Children.Add(SourceCard(provider, body));
                _ = FillSourceAsync(provider, body);
            }
        }
        ModelList.Children.Add(new TextBlock
        {
            Text = "Websites such as ChatGPT or Gemini on the web live in the web strip at the top.",
            Classes = { "faint" }, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 10, 4, 0),
        });
    }

    private Control SourceCard(IChatProvider provider, StackPanel body)
    {
        var kind = AiService.KindOf(provider);
        var (tint, glyph) = KindLook(kind);
        var tile = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(tint, 0.18),
            Child = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon(IconFor(provider)), Stroke = new SolidColorBrush(tint) },
        };
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        name.Children.Add(new TextBlock { Text = provider.Name, FontWeight = FontWeight.SemiBold, FontSize = 13 });
        name.Children.Add(new TextBlock { Text = Tagline(provider), Classes = { "faint" }, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis });
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(tile);
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        if (_ai?.Provider == provider)
        {
            var inUse = new Border { Classes = { "chip" }, Padding = new Thickness(7, 1), VerticalAlignment = VerticalAlignment.Center, Background = Res.Brush("AccentSoftBrush"),
                Child = new TextBlock { Text = "IN USE", FontSize = 9, FontWeight = FontWeight.Bold, Foreground = Res.Brush("AccentBrush") } };
            Grid.SetColumn(inUse, 2);
            header.Children.Add(inUse);
        }
        body.Children.Add(new TextBlock { Text = "Checking…", Classes = { "faint" }, FontSize = 11 });
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(header);
        stack.Children.Add(body);
        return new Border { Classes = { "sourcecard" }, Child = stack };
    }

    private async Task FillSourceAsync(IChatProvider provider, StackPanel body)
    {
        if (_ai is null) return;
        var problem = await provider.CheckAsync(CancellationToken.None);
        var models = problem is null ? await _ai.ModelsAsync(provider, CancellationToken.None) : Array.Empty<AiModel>();
        body.Children.Clear();

        if (problem is not null)
        {
            body.Children.Add(new TextBlock { Text = problem, Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
            if (AiService.KindOf(provider) == AiSourceKind.Cloud)
                body.Children.Add(ActionButton("IconKey", "Add API key…", () => { HideModelSheet(); OpenAiSettings?.Invoke(); }));
            return;
        }

        if (models.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var model in models.Take(30))
            {
                var chip = new Button { Classes = { "modelchip" }, Content = model.Name };
                chip.Classes.Set("selected", _ai.Provider == provider && _ai.Model == model.Id);
                if (model.Detail is { } detail) ToolTip.SetTip(chip, detail);
                chip.Click += (_, _) =>
                {
                    _ai.Choose(provider, model.Id);
                    HideModelSheet();
                    _ = CheckProviderAsync();
                };
                chips.Children.Add(chip);
            }
            body.Children.Add(chips);
        }
        else if (provider != _ai.Ollama)
            body.Children.Add(new TextBlock { Text = "Running, but no model is loaded yet.", Classes = { "muted" }, FontSize = 11.5 });

        if (provider == _ai.Ollama) AddOllamaDownloads(body, models);
    }

    // Ollama with no models is the usual first-run dead end: offer a short list of good ones to download right here.
    private void AddOllamaDownloads(StackPanel body, IReadOnlyList<AiModel> installed)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var (name, size, about) in OllamaProvider.Suggested)
        {
            if (installed.Any(m => m.Id == name || m.Id == name + ":latest")) continue;
            list.Children.Add(DownloadRow(name, size, about));
        }
        if (list.Children.Count == 0) return;
        if (installed.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No models yet. Pick one to download:", Classes = { "muted" }, FontSize = 11.5 });
            body.Children.Add(list);
            return;
        }
        var more = new Expander { Header = new TextBlock { Text = "Get more models", FontSize = 11.5 }, Content = list, Padding = new Thickness(0, 6, 0, 0) };
        body.Children.Add(more);
    }

    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Status, Button Button)> _pullViews = new();
    private readonly Dictionary<string, (string Status, double? Fraction)> _pulls = new();

    private Control DownloadRow(string name, string size, string about)
    {
        var text = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, FontSize = 12, Classes = { "mono" } });
        title.Children.Add(new TextBlock { Text = size, Classes = { "faint" }, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(title);
        var status = new TextBlock { Text = about, Classes = { "faint" }, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Children.Add(status);
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 3, MinHeight = 3, Margin = new Thickness(0, 5, 0, 0), IsVisible = false,
            Foreground = Res.Brush("AccentBrush"), Background = Res.Brush("Bg3Brush"), CornerRadius = new CornerRadius(2) };
        text.Children.Add(bar);

        var button = new Button { Classes = { "icon" }, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center,
            Content = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small", "accent" }, Data = Res.Icon("IconDownload") } };
        ToolTip.SetTip(button, $"Download {name} ({size})");
        button.Click += (_, _) => _ = PullAsync(name);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(text);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        _pullViews[name] = (bar, status, button);
        if (_pulls.TryGetValue(name, out var running)) ShowPull(name, running.Status, running.Fraction);
        return new Border { Background = Res.Brush("Bg2Brush"), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 7, 4, 7), Child = row };
    }

    private void ShowPull(string name, string status, double? fraction)
    {
        if (!_pullViews.TryGetValue(name, out var view)) return;
        view.Bar.IsVisible = true;
        view.Bar.IsIndeterminate = fraction is null;
        if (fraction is { } f) view.Bar.Value = f;
        view.Status.Text = fraction is { } p ? $"{status} · {p:P0}" : status;
        view.Button.IsEnabled = false;
    }

    private async Task PullAsync(string name)
    {
        if (_ai is null || _pulls.ContainsKey(name)) return;
        _pulls[name] = ("Starting download…", null);
        ShowPull(name, "Starting download…", null);
        var progress = new Progress<(string Status, double? Fraction)>(p =>
        {
            _pulls[name] = p;
            ShowPull(name, p.Status, p.Fraction);
        });
        try
        {
            await _ai.Ollama.PullAsync(name, progress, CancellationToken.None);
            _pulls.Remove(name);
            _ai.ForgetModels(_ai.Ollama.Id);
            _ai.Choose(_ai.Ollama, name);
            _ = CheckProviderAsync();
            if (SheetOpen) BuildModelSheet();
        }
        catch (Exception ex)
        {
            _pulls.Remove(name);
            if (_pullViews.TryGetValue(name, out var view))
            {
                view.Bar.IsVisible = false;
                view.Status.Text = "Download failed: " + ex.Message;
                view.Button.IsEnabled = true;
            }
        }
    }

    private static Button ActionButton(string icon, string text, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small", "accent" }, Data = Res.Icon(icon) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Classes = { "modelchip" }, Content = row, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => click();
        return button;
    }

    private static (Color Tint, string Label) KindLook(AiSourceKind kind) => kind switch
    {
        AiSourceKind.App => (Color.Parse("#B794FF"), "APP"),
        AiSourceKind.Local => (Color.Parse("#4ADE80"), "LOCAL"),
        _ => (Color.Parse("#7FB0FF"), "CLOUD"),
    };

    private static string IconFor(IChatProvider provider) => provider.Id switch
    {
        "claude-code" or "gemini-cli" => "IconTerminal",
        "ollama" or "lmstudio" => "IconServer",
        _ => "IconGlobe",
    };

    private static string Tagline(IChatProvider provider) => provider.Id switch
    {
        "claude-code" => "Uses your Claude account (Pro / Max)",
        "gemini-cli" => "Uses your Google account",
        "ollama" => "Free open models, run locally",
        "lmstudio" => "Whatever model LM Studio has loaded",
        "claude" => "Anthropic API",
        "openai" => "OpenAI API",
        "gemini" => "Google AI Studio API",
        _ => provider.Id,
    };

    internal void DevShow(bool sheet, bool rail)
    {
        if (sheet) ShowModelSheet();
        else if (SheetOpen) HideModelSheet();
        SetRail(rail);
    }

    /// <summary>Asks the host to open Settings → AI (to add an API key).</summary>
    public event Action? OpenAiSettings;

    // ---------------------------------------------------------------- history rail

    private bool _railOpen;
    private double _railWidth = RailClosed;
    private string _historySignature = "";
    private const double RailClosed = 40, RailOpen = 236;

    private void SetRail(bool open)
    {
        if (_railOpen == open) return;
        _railOpen = open;
        HistoryRail.Classes.Set("open", open);
        var from = _railWidth;
        var to = open ? RailOpen : RailClosed;
        Animator.Run(HistoryRail, open ? 0.22 : 0.16, t =>
        {
            var e = Ease.OutCubic(t);
            _railWidth = from + (to - from) * e;
            HistoryRail.Width = _railWidth;
            RailNewChat.Width = _railWidth - 10;
            var fade = Math.Clamp((_railWidth - RailClosed) / (RailOpen - RailClosed), 0, 1);
            foreach (var label in RailLabels()) label.Opacity = fade;
        });
    }

    private IEnumerable<Control> RailLabels()
    {
        yield return RailNewText;
        yield return RailTitle;
        foreach (var child in HistoryList.Children)
        {
            if (child is TextBlock header) yield return header;
            else if (child is Button { Content: Grid row }) foreach (var c in row.Children.Skip(1)) yield return c;
        }
    }

    /// <summary>Rebuilt only when the list itself changes (not on every streamed token).</summary>
    private void RenderHistory()
    {
        if (_ai is null) return;
        var signature = _ai.Current.Id + "|" + string.Join(",", _ai.Threads.Select(t => t.Id + t.Title));
        if (signature == _historySignature) return;
        _historySignature = signature;

        HistoryList.Children.Clear();
        var fade = _railOpen ? 1.0 : 0.0;
        if (_ai.Threads.Count == 0)
        {
            HistoryList.Children.Add(new TextBlock { Text = "Your conversations will appear here.", Classes = { "faint" }, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Width = RailOpen - 30, Margin = new Thickness(8, 4), Opacity = fade });
            return;
        }
        string? lastGroup = null;
        foreach (var thread in _ai.Threads.Take(80))
        {
            var group = DayGroup(thread.Updated);
            if (group != lastGroup)
            {
                lastGroup = group;
                HistoryList.Children.Add(new TextBlock { Text = group, Classes = { "faint" }, FontSize = 10, FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(8, HistoryList.Children.Count == 0 ? 0 : 8, 0, 2), Opacity = fade });
            }
            HistoryList.Children.Add(HistoryRow(thread, fade));
        }
    }

    private Control HistoryRow(ChatThread thread, double fade)
    {
        var current = _ai?.Current == thread;
        var provider = _ai?.Providers.FirstOrDefault(p => p.Id == thread.ProviderId);
        var tint = provider is null ? Color.Parse("#8A8F98") : KindLook(AiService.KindOf(provider)).Tint;
        var initial = thread.Title.FirstOrDefault(char.IsLetterOrDigit);
        var badge = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(7),
            Background = current ? Res.Brush("AccentGradientDiagonal") : new SolidColorBrush(tint, 0.2),
            Child = new TextBlock
            {
                Text = initial == default ? "·" : char.ToUpperInvariant(initial).ToString(), FontSize = 11, FontWeight = FontWeight.Bold,
                Foreground = current ? Brushes.White : new SolidColorBrush(tint), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var text = new StackPanel { Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = fade };
        text.Children.Add(new TextBlock { Text = thread.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal });
        text.Children.Add(new TextBlock { Text = $"{provider?.Name ?? "AI"} · {Ago(thread.Updated)}", Classes = { "faint" }, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis });

        var delete = new Button { Classes = { "icon", "rowdelete" }, Width = 22, Height = 22, Opacity = fade,
            Content = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconTrash"), Height = 11 } };
        ToolTip.SetTip(delete, "Delete conversation");
        delete.Click += (_, e) =>
        {
            e.Handled = true;
            _ai?.Delete(thread);
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Width = RailOpen - 18 };
        row.Children.Add(badge);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(delete, 2);
        row.Children.Add(delete);
        var button = new Button { Classes = { "chatrow" }, Content = row };
        button.Classes.Set("current", current);
        ToolTip.SetTip(button, thread.Title);
        button.Click += (_, _) =>
        {
            _ai?.Open(thread);
            SetRail(false);
            _ = CheckProviderAsync();
        };
        return button;
    }

    private static string DayGroup(DateTime when)
    {
        var days = (DateTime.Today - when.Date).TotalDays;
        return days switch { <= 0 => "Today", <= 1 => "Yesterday", < 7 => "This week", < 31 => "This month", _ => "Older" };
    }

    private static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 7) return when.ToString("dddd");
        return when.ToString("d MMM");
    }

    // ---------------------------------------------------------------- prompts & quick actions

    private void BuildQuickActions()
    {
        QuickActions.Children.Clear();
        foreach (var prompt in _settings.Prompts.Take(4))
        {
            var chip = new Button { Classes = { "webpill" }, Margin = new Thickness(0, 0, 6, 6), Content = new TextBlock { Classes = { "name" }, Text = prompt.Name } };
            ToolTip.SetTip(chip, "Uses the clipboard text");
            chip.Click += (_, _) => _ = RunPromptAsync(prompt.Text);
            QuickActions.Children.Add(chip);
        }
    }

    private void ShowPrompts(Control anchor)
    {
        var panel = new StackPanel { Spacing = 2, Width = 280 };
        panel.Children.Add(new TextBlock { Text = "PROMPT LIBRARY", Classes = { "eyebrow" }, FontSize = 9.5, Margin = new Thickness(6, 0, 0, 6) });
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.TopEdgeAlignedLeft };
        foreach (var prompt in _settings.Prompts)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = prompt.Name, FontWeight = FontWeight.SemiBold, FontSize = 12.5 });
            text.Children.Add(new TextBlock { Text = prompt.Text.Replace("\n", " "), Classes = { "faint" }, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button { Classes = { "result" }, Content = text, Padding = new Thickness(8, 6) };
            button.Click += (_, _) =>
            {
                flyout.Hide();
                _ = RunPromptAsync(prompt.Text);
            };
            panel.Children.Add(button);
        }
        panel.Children.Add(new TextBlock { Text = "Edit prompts in Settings → AI. {{clipboard}} becomes the copied text.", Classes = { "faint" }, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 6, 6, 0) });
        flyout.ShowAt(anchor);
    }

    // ---------------------------------------------------------------- rendering

    private void Render()
    {
        if (_ai is null) return;
        var provider = _ai.Provider;
        var (tint, label) = KindLook(AiService.KindOf(provider));
        ProviderText.Text = provider.Name;
        ModelText.Text = _ai.Model is { } model ? ModelLabel(provider, model) : provider is ClaudeProvider ? ClaudeProvider.DefaultModel : "Choose a model";
        KindText.Text = label;
        KindBadge.Background = new SolidColorBrush(tint, 0.2);
        KindText.Foreground = new SolidColorBrush(tint);
        RenderHistory();
        RenderFolder();
        SpeedText.Text = _ai.LastTokensPerSecond is { } speed ? $"{speed:0} tok/s" : "";
        SendIcon.Data = Res.Icon(_ai.IsBusy ? "IconStop" : "IconSend");
        SendIcon.Fill = _ai.IsBusy ? Brushes.White : null;
        ToolTip.SetTip(SendButton, _ai.IsBusy ? "Stop" : "Send (Enter)");

        EmptyState.IsVisible = _ai.Conversation.Count == 0;
        var stickToBottom = Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - 40;
        RenderMessages();
        if (stickToBottom) Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private readonly Dictionary<AiTurn, (string Signature, Control Bubble)> _bubbles = new();

    /// <summary>
    /// Only bubbles whose content changed are rebuilt: while an answer streams that is the last one, so long chats
    /// with tables and code stay smooth.
    /// </summary>
    private void RenderMessages()
    {
        var turns = _ai!.Conversation;
        foreach (var gone in _bubbles.Keys.Where(t => !turns.Contains(t)).ToList()) _bubbles.Remove(gone);
        var bubbles = new List<Control>(turns.Count);
        foreach (var turn in turns)
        {
            var last = ReferenceEquals(turn, turns[^1]) && !_ai.IsBusy;
            var signature = $"{turn.Text.Length}|{turn.Text.GetHashCode()}|{turn.Error}|{turn.IsStreaming}|{turn.Actions.Count}|{turn.Pending?.GetHashCode()}|{turn.Images.Count}|{last}";
            if (!_bubbles.TryGetValue(turn, out var cached) || cached.Signature != signature)
                _bubbles[turn] = cached = (signature, Bubble(turn));
            bubbles.Add(cached.Bubble);
        }
        if (Messages.Children.Count == bubbles.Count && Messages.Children.Zip(bubbles).All(p => ReferenceEquals(p.First, p.Second))) return;
        // Patch in place when bubbles only changed or were added at the end (streaming); otherwise start over.
        var canPatch = Messages.Children.Count <= bubbles.Count
                       && bubbles.Select((b, i) => b.Parent is null || (i < Messages.Children.Count && ReferenceEquals(Messages.Children[i], b))).All(ok => ok);
        if (!canPatch)
        {
            Messages.Children.Clear();
            Messages.Children.AddRange(bubbles);
            return;
        }
        for (var i = 0; i < Messages.Children.Count; i++)
            if (!ReferenceEquals(Messages.Children[i], bubbles[i])) Messages.Children[i] = bubbles[i];
        for (var i = Messages.Children.Count; i < bubbles.Count; i++) Messages.Children.Add(bubbles[i]);
    }

    /// <summary>The friendly name of a model when the picker already knows it ("Your plan's default" for Claude Code).</summary>
    private static string ModelLabel(IChatProvider provider, string model)
        => provider is ClaudeCodeProvider ? model switch { "default" => "Default model", _ => char.ToUpperInvariant(model[0]) + model[1..] } : model;

    private Control Bubble(AiTurn turn)
    {
        var user = turn.Role == ChatMessage.User;
        var content = new StackPanel { Spacing = 6 };
        foreach (var image in turn.Images)
        {
            try
            {
                using var ms = new MemoryStream(image);
                content.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, MaxHeight = 160, Child = new Image { Source = new Bitmap(ms), Stretch = Stretch.Uniform } });
            }
            catch (Exception) { }
        }

        if (turn.Actions.Count > 0) content.Children.Add(ActionList(turn));
        if (turn.Pending is { } pending) content.Children.Add(ApprovalCard(pending));
        if (turn.Error is { } error)
            content.Children.Add(new SelectableTextBlock { Classes = { "msg" }, Text = "⚠ " + error, Foreground = Res.Brush("DangerBrush") });
        else if (turn.Text.Length == 0 && turn.IsStreaming)
        {
            if (turn.Pending is null) content.Children.Add(new TextBlock { Text = turn.Actions.Count > 0 ? "Working…" : "Thinking…", Classes = { "muted" }, FontSize = 12.5 });
        }
        else
            foreach (var part in user ? new Control[] { new SelectableTextBlock { Classes = { "msg" }, Text = Shortened(turn.Text) } } : ChatMarkdown.Render(turn.Text, CopyAsync)) content.Children.Add(part);

        if (!user && !turn.IsStreaming)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (turn.Error is null && turn.Text.Length > 0)
                row.Children.Add(BubbleButton("IconCopy", "Copy answer", async () => await CopyAsync(turn.Text)));
            // The last answer can be asked again (after an error, or for a different answer).
            if (_ai?.Conversation.LastOrDefault() == turn && _ai.IsBusy == false)
                row.Children.Add(BubbleButton("IconRefresh", turn.Error is null ? "Answer again" : "Try again", () => { _ = _ai.RetryLastAsync(); return Task.CompletedTask; }));
            if (row.Children.Count > 0) content.Children.Add(row);
        }
        return new Border { Classes = { "bubble", user ? "user" : "assistant" }, Child = content };
    }

    private static Button BubbleButton(string icon, string tip, Func<Task> click)
    {
        var button = new Button { Classes = { "icon" }, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, Content = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon(icon) } };
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) => await click();
        return button;
    }

    /// <summary>Your own long messages (attached files, pasted logs) are shown cut; the AI still gets all of it.</summary>
    private static string Shortened(string text)
        => text.Length <= 900 ? text : text[..700].TrimEnd() + $"\n… ({text.Length - 700:N0} more characters sent)";

    /// <summary>The AI wants to do something hard to undo: Allow / Allow all in this chat / Deny.</summary>
    private Control ApprovalCard(AiApproval approval)
    {
        var stack = new StackPanel { Spacing = 10 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        title.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconShield"), Stroke = Res.Brush("WarningBrush"), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = "Allow this?", FontWeight = FontWeight.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(title);
        // The question may carry a code block (a script to run): full Markdown, not just one line.
        foreach (var part in ChatMarkdown.Render(approval.Text, CopyAsync)) stack.Children.Add(part);

        var buttons = new WrapPanel();
        Button Choice(string text, string classes, bool allow, bool always)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), FontSize = 12, Padding = new Thickness(12, 5) };
            button.Classes.Add(classes);
            button.Click += (_, _) => _ai?.Answer(approval, allow, always);
            return button;
        }
        buttons.Children.Add(Choice("Allow", "accent", true, false));
        var all = Choice("Allow all", "ghost", true, true);
        ToolTip.SetTip(all, "Allow this and every further action in this conversation");
        buttons.Children.Add(all);
        buttons.Children.Add(Choice("Deny", "ghost", false, false));
        stack.Children.Add(buttons);
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFB547"), 0.07), BorderBrush = new SolidColorBrush(Color.Parse("#FFB547"), 0.35),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), Child = stack,
        };
    }

    /// <summary>
    /// The things the AI did while answering, one line each. Long lists fold to the last few once the answer is done.
    /// </summary>
    private static Control ActionList(AiTurn turn)
    {
        var lines = new StackPanel { Spacing = 3 };
        for (var i = 0; i < turn.Actions.Count; i++)
        {
            var action = turn.Actions[i];
            var failed = action.StartsWith('⚠');
            var running = turn.IsStreaming && i == turn.Actions.Count - 1 && turn.Text.Length == 0;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Classes = { "icon", "small" }, Height = 10, VerticalAlignment = VerticalAlignment.Center,
                Data = Res.Icon(failed ? "IconAlert" : running ? "IconSparkle" : "IconCheck"),
                Stroke = Res.Brush(failed ? "WarningBrush" : running ? "AccentBrush" : "SuccessBrush"),
            });
            row.Children.Add(new TextBlock
            {
                Text = failed ? action.TrimStart('⚠', ' ') : action, FontSize = 11.5, Classes = { "muted" },
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center,
            });
            ToolTip.SetTip(row, action);
            lines.Children.Add(new Border { Classes = { "actionline" }, HorizontalAlignment = HorizontalAlignment.Left, Child = row });
        }
        if (turn.IsStreaming || turn.Actions.Count <= 6) return lines;
        lines.IsVisible = false;
        var label = new TextBlock { Text = $"{turn.Actions.Count} actions", VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconCheck"), Height = 10, Stroke = Res.Brush("SuccessBrush") });
        header.Children.Add(label);
        header.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconChevronDown"), Height = 7, Opacity = 0.7 });
        var toggle = new Button { Classes = { "footchip" }, Content = header, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-7, 0, 0, 0) };
        toggle.Click += (_, _) => lines.IsVisible = !lines.IsVisible;
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(toggle);
        stack.Children.Add(lines);
        return stack;
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        Copied?.Invoke();
    }

    /// <summary>Raised after something was copied (the host shows a toast).</summary>
    public event Action? Copied;
}
