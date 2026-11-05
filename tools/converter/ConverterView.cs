using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Overdesk.Core.Plugins;
using Overdesk.Tools.Shared;
using static Overdesk.Tools.Shared.ToolUi;

namespace Overdesk.Tool.Converter;

/// <summary>
/// The Converter panel: drop or pick files, choose the format, convert. Results land next to the originals (or in a
/// chosen folder) and can be revealed. Built in code so the plugin ships as one DLL; it uses OverDesk's own styles.
/// </summary>
internal sealed class ConverterView : UserControl, IFileDropTarget
{
    private sealed class Item
    {
        public required string Path { get; init; }
        public required FileKind Kind { get; init; }
        public required IReadOnlyList<string> Targets { get; init; }
        public string? Output;
        public string? Error;
        public double? Progress;
    }

    private static readonly int?[] Sizes = { null, 4096, 2560, 1920, 1280, 1024, 512 };
    private readonly List<Item> _items = new();
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly WrapPanel _targets = new();
    private readonly StackPanel _imageOptions = new() { Spacing = 8 };
    private readonly Slider _quality = new() { Minimum = 40, Maximum = 100, Value = 90, SmallChange = 1, LargeChange = 5 };
    private readonly TextBlock _qualityText = new() { FontSize = 12, Width = 34, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _size = new() { MinWidth = 150 };
    private readonly CheckBox _combine = new() { Content = "Combine into one PDF", FontSize = 12, IsVisible = false };
    private readonly TextBlock _hint = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, IsVisible = false };
    private readonly Button _recheck = new() { Content = "Check again", Padding = new Thickness(10, 4), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private readonly Button _convert = new() { Content = "Convert", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 9) };
    private readonly Button _outputButton = new() { Padding = new Thickness(10, 4), FontSize = 12 };
    private readonly StackPanel _empty = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _work = new() { Spacing = 14 };
    private string? _target;
    private string? _outputFolder;
    private CancellationTokenSource? _running;

    public ConverterView()
    {
        _convert.Classes.Add("accent");
        _outputButton.Classes.Add("ghost");
        _recheck.Classes.Add("ghost");
        _recheck.Click += (_, _) => _ = RecheckToolsAsync();
        _convert.Click += (_, _) => { if (_running is { } running) running.Cancel(); else _ = ConvertAllAsync(); };
        _outputButton.Click += (_, _) => _ = ChooseOutputAsync();
        _quality.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) _qualityText.Text = $"{(int)_quality.Value}"; };
        _qualityText.Text = "90";
        _size.ItemsSource = Sizes.Select(s => s is null ? "Original size" : $"Longest edge {s} px").ToList();
        _size.SelectedIndex = 0;

        // Empty state: what it does, and a way in besides dropping.
        _empty.Children.Add(Badge(this, "IconRefresh"));
        _empty.Children.Add(new TextBlock { Text = "Drop files to convert", FontSize = 17, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock
        {
            Text = "Images (PNG, JPG, WebP, HEIC, PSD, RAW, TGA…), data (JSON, YAML, TOML, CSV, Excel, XML, plist, .env…), Markdown, Word, PDF, audio, video, ZIP / TAR archives, fonts (TTF, OTF, WOFF) and 3D models (OBJ, STL, PLY → glTF).\nAnything else can be zipped. Results go next to the originals.",
            FontSize = 12, Opacity = 0.65, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300,
        });
        var browse = new Button { Content = "Choose files…", HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 7), Margin = new Thickness(0, 6, 0, 0) };
        browse.Classes.Add("accent");
        browse.Click += (_, _) => _ = BrowseAsync();
        _empty.Children.Add(browse);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(Eyebrow("FILES"));
        var add = SmallButton("Add…", () => _ = BrowseAsync());
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        var clear = SmallButton("Clear", () => { _running?.Cancel(); _items.Clear(); Render(); });
        Grid.SetColumn(clear, 2);
        header.Children.Add(clear);

        var qualityRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        qualityRow.Children.Add(new TextBlock { Text = "Quality", FontSize = 12, Width = 70, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_quality, 1);
        qualityRow.Children.Add(_quality);
        Grid.SetColumn(_qualityText, 2);
        qualityRow.Children.Add(_qualityText);
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        sizeRow.Children.Add(new TextBlock { Text = "Size", FontSize = 12, Width = 70, VerticalAlignment = VerticalAlignment.Center });
        sizeRow.Children.Add(_size);
        _imageOptions.Children.Add(qualityRow);
        _imageOptions.Children.Add(sizeRow);
        _imageOptions.Children.Add(_combine);
        _combine.IsCheckedChanged += (_, _) => Render();

        var outputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        outputRow.Children.Add(new TextBlock { Text = "Save to", FontSize = 12, Width = 62, VerticalAlignment = VerticalAlignment.Center });
        outputRow.Children.Add(_outputButton);

        _work.Children.Add(header);
        _work.Children.Add(_list);
        _work.Children.Add(Eyebrow("CONVERT TO"));
        _work.Children.Add(_targets);
        _work.Children.Add(_hint);
        _work.Children.Add(_recheck);
        _work.Children.Add(_imageOptions);
        _work.Children.Add(outputRow);
        _work.Children.Add(_convert);

        var scroller = new ScrollViewer { Content = new Border { Padding = new Thickness(14, 6, 14, 16), Child = _work } };
        var root = new Panel();
        root.Children.Add(scroller);
        root.Children.Add(_empty);
        Content = root;
        Render();
    }

    public string DropHint => "Drop to convert: they are added to the Converter's list.";

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _running?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    public void FilesDropped(IReadOnlyList<string> paths) => Dispatcher.UIThread.Post(() => _ = AddAsync(paths));

    /// <summary>Looks at the files off the UI thread (kind sniffing, font headers and the ffmpeg lookup read from disk), then lists them.</summary>
    private async Task AddAsync(IEnumerable<string> paths)
    {
        var known = _items.Select(i => i.Path).ToHashSet();
        var found = await Task.Run(() => Inspect(paths.ToList(), known));
        foreach (var item in found) if (_items.All(i => i.Path != item.Path)) _items.Add(item);
        Render();
    }

    private static List<Item> Inspect(List<string> paths, HashSet<string> known)
    {
        var result = new List<Item>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                result.AddRange(Inspect(Directory.EnumerateFiles(path).Where(f => Engine.KindOf(f) != FileKind.Unknown).Take(200).ToList(), known));
                continue;
            }
            if (!File.Exists(path) || known.Contains(path) || result.Any(i => i.Path == path)) continue;
            result.Add(Describe(path));
        }
        return result;
    }

    private static Item Describe(string path)
    {
        IReadOnlyList<string> targets;
        try { targets = Engine.TargetsFor(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { targets = Array.Empty<string>(); }
        return new Item { Path = path, Kind = Engine.KindOf(path), Targets = targets };
    }

    private async Task RecheckToolsAsync()
    {
        Engine.ForgetTools();
        var items = _items.ToList();
        var fresh = await Task.Run(() => items.Select(i => Describe(i.Path)).ToList());
        for (var n = 0; n < items.Count; n++)
        {
            var at = _items.IndexOf(items[n]);
            if (at < 0) continue;
            fresh[n].Output = items[n].Output;
            fresh[n].Error = items[n].Error;
            _items[at] = fresh[n];
        }
        Render();
    }

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Files to convert", AllowMultiple = true });
        await AddAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async Task ChooseOutputAsync()
    {
        if (_outputFolder is not null)
        {
            _outputFolder = null;
            Render();
            return;
        }
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where converted files go" });
        _outputFolder = folders.FirstOrDefault()?.TryGetLocalPath();
        Render();
    }

    /// <summary>The formats every listed file can become.</summary>
    private List<string> CommonTargets()
    {
        var pending = _items.Where(i => i.Output is null).ToList();
        if (pending.Count == 0) return new List<string>();
        IEnumerable<string> common = pending[0].Targets;
        foreach (var item in pending.Skip(1)) common = common.Intersect(item.Targets);
        return common.ToList();
    }

    private void Render()
    {
        _empty.IsVisible = _items.Count == 0;
        _work.IsVisible = _items.Count > 0;
        _list.Children.Clear();
        foreach (var item in _items) _list.Children.Add(Row(item));

        var targets = CommonTargets();
        if (_target is not null && !targets.Contains(_target)) _target = null;
        _target ??= targets.FirstOrDefault();
        _targets.Children.Clear();
        foreach (var target in targets)
        {
            var t = target;
            _targets.Children.Add(Chip(Label(target), target == _target, () => { _target = t; Render(); }));
        }
        var open = _items.Where(i => i.Output is null).ToList();
        var kinds = open.Select(i => i.Kind).Distinct().ToList();
        var hint = open.Select(i => Engine.Hint(i.Path)).FirstOrDefault(h => h is not null);
        if (targets.Count == 0 && _items.Any(i => i.Output is null))
            hint = kinds.Count > 1 ? "These files have no format in common: convert one kind at a time." : hint ?? "This kind of file cannot be converted here.";
        _hint.Text = hint ?? "";
        _hint.IsVisible = hint is not null;
        _recheck.IsVisible = Engine.FfmpegMissing && kinds.Any(k => k is FileKind.Audio or FileKind.Video);
        _imageOptions.IsVisible = kinds.All(k => k == FileKind.Image) && kinds.Count > 0;
        _quality.IsEnabled = _target is "jpg" or "webp" or "heic";
        _combine.IsVisible = _target == "pdf" && _items.Count(i => i.Output is null) > 1;
        _outputButton.Content = _outputFolder is null ? "Next to the originals" : $"{System.IO.Path.GetFileName(_outputFolder)} (click to reset)";
        _convert.IsEnabled = _running is not null || _target is not null;
        _convert.Content = _running is not null ? "Stop" : _target is null ? "Convert"
            : _target == "folder" ? "Unpack" : _combine.IsVisible && _combine.IsChecked == true ? "Combine into one PDF" : $"Convert to {Label(_target)}";
    }

    private static string Label(string target) => target switch { "folder" => "Unpack to folder", "gz" => "GZIP", var t => t.ToUpperInvariant() };

    private Control Row(Item item)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        text.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(item.Path), FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = item.Error is { } error ? "✗ " + error
            : item.Output is { } output ? "✓ " + System.IO.Path.GetFileName(output)
            : $"{item.Kind} · {Size(item.Path)}";
        var detailBlock = new TextBlock { Text = detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = item.Output is null && item.Error is null ? 0.6 : 1 };
        if (item.Error is not null) detailBlock[!TextBlock.ForegroundProperty] = this.GetResourceObservable("DangerBrush").ToBinding();
        if (item.Output is not null) detailBlock[!TextBlock.ForegroundProperty] = this.GetResourceObservable("SuccessBrush").ToBinding();
        text.Children.Add(detailBlock);
        if (item.Progress is { } progress)
            text.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = progress, Height = 3, MinHeight = 3, Margin = new Thickness(0, 4, 0, 0), IsIndeterminate = progress <= 0 });

        var badge = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(9),
            [!Border.BackgroundProperty] = this.GetResourceObservable("Bg3Brush").ToBinding(),
            Child = new TextBlock { Text = Engine.Ext(item.Path).ToUpperInvariant() is { Length: > 4 } e ? e[..4] : Engine.Ext(item.Path).ToUpperInvariant(), FontSize = 9.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (item.Output is { } done) actions.Children.Add(IconButton("IconFolder", "Show in folder", () => Reveal(done)));
        actions.Children.Add(IconButton("IconClose", "Remove", () => { _items.Remove(item); Render(); }));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(badge);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        return new Border
        {
            Padding = new Thickness(8), CornerRadius = new CornerRadius(10), Child = row,
            [!Border.BackgroundProperty] = this.GetResourceObservable("Bg2Brush").ToBinding(),
        };
    }

    private async Task ConvertAllAsync()
    {
        if (_target is not { } target || _running is not null) return;
        using var cts = _running = new CancellationTokenSource();
        var options = new ConvertOptions { Quality = (int)_quality.Value, MaxSize = Sizes[Math.Max(0, _size.SelectedIndex)], OutputFolder = _outputFolder };
        Render();
        if (target == "pdf" && _combine.IsVisible && _combine.IsChecked == true)
        {
            var pending = _items.Where(i => i.Output is null).ToList();
            pending.ForEach(i => { i.Error = null; i.Progress = 0; });
            Render();
            try
            {
                var pdf = await Engine.CombineToPdfAsync(pending.Select(i => i.Path).ToList(), options, cts.Token);
                pending.ForEach(i => i.Output = pdf);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { pending.ForEach(i => i.Error = ex.Message); }
            catch (OperationCanceledException) { }
            pending.ForEach(i => i.Progress = null);
            _running = null;
            Render();
            return;
        }
        foreach (var item in _items.Where(i => i.Output is null).ToList())
        {
            if (cts.IsCancellationRequested) break;
            item.Error = null;
            item.Progress = 0;
            Render();
            var progress = new Progress<double>(p =>
            {
                if (item.Progress is null) return;
                item.Progress = p;
                Render();
            });
            try { item.Output = await Engine.ConvertAsync(item.Path, target, options, progress, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { item.Error = ex.Message; }
            item.Progress = null;
            Render();
        }
        _running = null;
        Render();
    }

    private static string Size(string path)
    {
        try { return FormatSize(new FileInfo(path).Length); }
        catch (IOException) { return ""; }
    }
}
