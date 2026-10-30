using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Overdesk.Core.Plugins;
using Overdesk.Tools.Shared;
using SkiaSharp;
using static Overdesk.Tools.Shared.ToolUi;

namespace Overdesk.Tool.Sprites;

/// <summary>Entry point named by <c>typeName</c> in overdesk.plugin.json. Also lets the AI pack and slice sprite sheets.</summary>
public sealed class SpritesPlugin : IToolPlugin, IAiToolSource
{
    public object CreateView() => new SpritesView();

    public IReadOnlyList<PluginAiTool> AiTools { get; } = new[]
    {
        new PluginAiTool(
            "pack_sprites",
            "Pack images (or every image in a folder) into one sprite atlas PNG plus a TexturePacker-style JSON (hash format: Phaser, PixiJS, Cocos…) " +
            "with OverDesk's Sprite Sheet tool. Transparent borders are trimmed by default.",
            """
            {"type":"object","properties":{
              "paths":{"type":"array","items":{"type":"string"},"description":"Absolute paths of images or folders."},
              "output":{"type":"string","description":"Atlas PNG path; default \"atlas.png\" next to the first image."},
              "padding":{"type":"integer","description":"Pixels between sprites (default 2)."},
              "trim":{"type":"boolean"},
              "power_of_two":{"type":"boolean"}
            },"required":["paths"]}
            """,
            async (json, cancellation) =>
            {
                var args = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
                var paths = SpritesView.Expand((args["paths"] as JsonArray)?.Select(p => p?.ToString() ?? "") ?? Array.Empty<string>()).ToList();
                if (paths.Count == 0) throw new InvalidOperationException("No images found.");
                var output = args["output"]?.ToString() is { Length: > 0 } o ? o : FreePath(Path.GetDirectoryName(paths[0])!, "atlas", "png");
                var options = new PackOptions
                {
                    Padding = args["padding"] is JsonValue p && int.TryParse(p.ToString(), out var pad) ? pad : 2,
                    Trim = args["trim"] is not JsonValue t || !t.TryGetValue<bool>(out var trim) || trim,
                    PowerOfTwo = args["power_of_two"] is JsonValue q && q.TryGetValue<bool>(out var pow) && pow,
                };
                var result = await Task.Run(() => Sprites.Pack(paths, output, options), cancellation);
                return $"Packed {result.Count} sprites into {result.Width} × {result.Height}:\n{result.Image}\n{result.Json}";
            })
        {
            Label = json => $"Packing {(JsonNode.Parse(json)?["paths"] as JsonArray)?.Count ?? 0} sprite sources",
        },
        new PluginAiTool(
            "slice_sprite_sheet",
            "Cut a sprite sheet into separate PNG frames with OverDesk's Sprite Sheet tool: by a grid (columns × rows) or, without columns/rows, " +
            "by detecting each separate shape on a transparent background. Frames go into a new \"<name> frames\" folder.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Absolute path of the sheet."},
              "columns":{"type":"integer"},
              "rows":{"type":"integer"},
              "output_folder":{"type":"string"}
            },"required":["path"]}
            """,
            async (json, cancellation) =>
            {
                var args = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
                var path = args["path"]?.ToString() ?? "";
                if (!File.Exists(path)) throw new FileNotFoundException("No such image.", path);
                int? Int(string key) => args[key] is JsonValue v && int.TryParse(v.ToString(), out var n) ? n : null;
                var folder = args["output_folder"]?.ToString() is { Length: > 0 } o ? o : SpritesView.FramesFolder(path);
                var written = await Task.Run(() =>
                {
                    using var sheet = ImageIO.Load(path);
                    var cells = Int("columns") is { } c ? Sprites.Grid(sheet.Width, sheet.Height, c, Int("rows") ?? 1) : Sprites.Detect(sheet);
                    return Sprites.Slice(sheet, cells, folder, Path.GetFileNameWithoutExtension(path));
                }, cancellation);
                return $"Wrote {written.Count} frames into {folder}";
            })
        {
            Label = json => $"Slicing {Path.GetFileName(JsonNode.Parse(json)?["path"]?.ToString() ?? "a sheet")}",
        },
    };
}

/// <summary>The Sprite Sheet panel: Pack (many images → atlas + JSON) or Slice (sheet → frames, by grid or by shape).</summary>
internal sealed class SpritesView : UserControl, IFileDropTarget
{
    private readonly WrapPanel _modes = new();
    private readonly StackPanel _pack = new() { Spacing = 10 };
    private readonly StackPanel _slice = new() { Spacing = 10 };
    private readonly StackPanel _files = new() { Spacing = 4 };
    private readonly CheckBox _trim = new() { Content = "Trim transparent borders", IsChecked = true, FontSize = 12 };
    private readonly CheckBox _pow2 = new() { Content = "Power-of-two size", FontSize = 12 };
    private readonly Slider _padding = new() { Minimum = 0, Maximum = 16, Value = 2, SmallChange = 1, LargeChange = 2, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly Button _packButton;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 280 };
    private readonly Border _previewFrame;
    private readonly WrapPanel _sliceModes = new();
    private readonly NumericUpDown _columns = new() { Minimum = 1, Maximum = 256, Value = 4, Increment = 1, FormatString = "0", Width = 120 };
    private readonly NumericUpDown _rows = new() { Minimum = 1, Maximum = 256, Value = 4, Increment = 1, FormatString = "0", Width = 120 };
    private readonly StackPanel _gridOptions = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly TextBlock _sheetInfo = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly Button _sliceButton;
    private readonly TextBlock _result = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _reveal;
    private readonly List<string> _paths = new();
    private bool _packing = true;
    private bool _byGrid;
    private string? _sheetPath;
    private SKBitmap? _sheet;
    private List<SKRectI> _cells = new();
    private string? _lastOutput;
    private bool _busy;

    public SpritesView()
    {
        _packButton = AccentButton("Pack", () => _ = PackAsync());
        _sliceButton = AccentButton("Slice", () => _ = SliceAsync());
        _reveal = SmallButton("Show in folder", () => { if (_lastOutput is not null) Reveal(_lastOutput); });
        _reveal.HorizontalAlignment = HorizontalAlignment.Left;
        _reveal.IsVisible = false;
        _columns.ValueChanged += (_, _) => FindCells();
        _rows.ValueChanged += (_, _) => FindCells();

        var packHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        packHeader.Children.Add(Eyebrow("SPRITES"));
        var add = SmallButton("Add…", () => _ = BrowseAsync(multiple: true));
        Grid.SetColumn(add, 1);
        packHeader.Children.Add(add);
        var clear = SmallButton("Clear", () => { _paths.Clear(); Render(); });
        Grid.SetColumn(clear, 2);
        packHeader.Children.Add(clear);
        _pack.Children.Add(packHeader);
        _pack.Children.Add(_files);
        _pack.Children.Add(_trim);
        _pack.Children.Add(_pow2);
        _pack.Children.Add(SliderRow("Padding", _padding, "0 px"));
        _pack.Children.Add(_packButton);

        var sheetHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        sheetHeader.Children.Add(_sheetInfo);
        var choose = SmallButton("Choose…", () => _ = BrowseAsync(multiple: false));
        Grid.SetColumn(choose, 1);
        sheetHeader.Children.Add(choose);
        _gridOptions.Children.Add(Labeled("Columns", _columns));
        _gridOptions.Children.Add(Labeled("Rows", _rows));
        _slice.Children.Add(sheetHeader);
        _slice.Children.Add(_sliceModes);
        _slice.Children.Add(_gridOptions);
        _slice.Children.Add(_sliceButton);

        _previewFrame = new Border
        {
            Padding = new Thickness(8), CornerRadius = new CornerRadius(10), Child = _preview, IsVisible = false,
            [!Border.BackgroundProperty] = this.GetResourceObservable("Bg2Brush").ToBinding(),
        };

        var work = new StackPanel { Spacing = 12 };
        work.Children.Add(_modes);
        work.Children.Add(_pack);
        work.Children.Add(_slice);
        work.Children.Add(_previewFrame);
        work.Children.Add(_result);
        work.Children.Add(_reveal);
        Content = new ScrollViewer { Content = new Border { Padding = new Thickness(14, 6, 14, 16), Child = work } };
        Render();
    }

    public static string FramesFolder(string path) => FreePath(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " frames", "");

    /// <summary>Images among the paths, folders opened one level deep, in name order.</summary>
    public static IEnumerable<string> Expand(IEnumerable<string> paths)
        => paths.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p).Order(StringComparer.OrdinalIgnoreCase) : (IEnumerable<string>)new[] { p })
                .Where(p => File.Exists(p) && ImageIO.CanRead(p));

    public string DropHint => _packing ? "Drop images (or a folder) to pack them into an atlas." : "Drop a sprite sheet to slice it.";

    public void FilesDropped(IReadOnlyList<string> paths) => Dispatcher.UIThread.Post(() =>
    {
        var images = Expand(paths).ToList();
        if (images.Count == 0) return;
        if (_packing) Add(images);
        else _ = LoadSheetAsync(images[0]);
    });

    private void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths) if (!_paths.Contains(path)) _paths.Add(path);
        Render();
    }

    private async Task BrowseAsync(bool multiple)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = multiple ? "Sprites" : "Sprite sheet", AllowMultiple = multiple });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (multiple) Add(Expand(paths));
        else if (paths.FirstOrDefault() is { } path) await LoadSheetAsync(path);
    }

    private async Task LoadSheetAsync(string path)
    {
        try
        {
            var bitmap = await Task.Run(() => ImageIO.Load(path));
            _sheet?.Dispose();
            _sheet = bitmap;
            _sheetPath = path;
            _result.IsVisible = _reveal.IsVisible = false;
            FindCells();
        }
        catch (Exception ex)
        {
            ShowResult(false, ex.Message);
        }
    }

    private void FindCells()
    {
        if (_sheet is null) { Render(); return; }
        try { _cells = _byGrid ? Sprites.Grid(_sheet.Width, _sheet.Height, (int)(_columns.Value ?? 1), (int)(_rows.Value ?? 1)) : Sprites.Detect(_sheet); }
        catch (Exception ex) { _cells = new(); ShowResult(false, ex.Message); }
        Render();
        ShowSheet();
    }

    /// <summary>The sheet with every cell outlined in the accent color.</summary>
    private void ShowSheet()
    {
        if (_sheet is null) return;
        var accent = this.TryFindResource("AccentColor", out var value) && value is Color c ? new SKColor(c.R, c.G, c.B) : new SKColor(0xFF, 0x3B, 0x5C);
        using var surface = SKSurface.Create(new SKImageInfo(_sheet.Width, _sheet.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawBitmap(_sheet, 0, 0);
        using var paint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, Math.Max(_sheet.Width, _sheet.Height) / 400f), IsAntialias = false };
        foreach (var cell in _cells) surface.Canvas.DrawRect(cell.Left + 0.5f, cell.Top + 0.5f, cell.Width - 1, cell.Height - 1, paint);
        using var snapshot = surface.Snapshot();
        using var premul = SKBitmap.FromImage(snapshot);
        using var rgba = ImageIO.ToRgba(premul);
        Show(rgba);
    }

    private void Show(SKBitmap rgba)
    {
        var bitmap = new WriteableBitmap(new PixelSize(rgba.Width, rgba.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var buffer = bitmap.Lock())
        {
            var pixels = ImageIO.Pixels(rgba);
            for (var y = 0; y < rgba.Height; y++) Marshal.Copy(pixels, y * rgba.Width * 4, buffer.Address + y * buffer.RowBytes, rgba.Width * 4);
        }
        var old = _preview.Source as IDisposable;
        _preview.Source = bitmap;
        old?.Dispose();
        _previewFrame.IsVisible = true;
    }

    private void Render()
    {
        _modes.Children.Clear();
        _modes.Children.Add(Chip("Pack into an atlas", _packing, () => { _packing = true; _previewFrame.IsVisible = false; Render(); }));
        _modes.Children.Add(Chip("Slice a sheet", !_packing, () => { _packing = false; Render(); ShowSheet(); }));
        _pack.IsVisible = _packing;
        _slice.IsVisible = !_packing;

        _files.Children.Clear();
        if (_paths.Count == 0)
            _files.Children.Add(new TextBlock { Text = "Drop images or a folder here, or use Add…", FontSize = 12, Opacity = 0.6 });
        foreach (var path in _paths.Take(8))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var remove = IconButton("IconClose", "Remove", () => { _paths.Remove(path); Render(); });
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _files.Children.Add(row);
        }
        if (_paths.Count > 8) _files.Children.Add(new TextBlock { Text = $"and {_paths.Count - 8} more", FontSize = 12, Opacity = 0.6 });
        _packButton.IsEnabled = _paths.Count > 0 && !_busy;
        _packButton.Content = _busy && _packing ? "Packing…" : $"Pack {_paths.Count} sprite{(_paths.Count == 1 ? "" : "s")}";

        _sliceModes.Children.Clear();
        _sliceModes.Children.Add(Chip("Find shapes", !_byGrid, () => { _byGrid = false; FindCells(); }));
        _sliceModes.Children.Add(Chip("Grid", _byGrid, () => { _byGrid = true; FindCells(); }));
        _gridOptions.IsVisible = _byGrid;
        _sheetInfo.Text = _sheet is null ? "Drop a sprite sheet here, or choose one."
            : $"{Path.GetFileName(_sheetPath)} · {_sheet.Width} × {_sheet.Height} · {_cells.Count} frame{(_cells.Count == 1 ? "" : "s")}";
        _sliceButton.IsEnabled = _sheet is not null && _cells.Count > 0 && !_busy;
        _sliceButton.Content = _busy && !_packing ? "Slicing…" : $"Slice into {_cells.Count} frames";
    }

    private async Task PackAsync()
    {
        if (_paths.Count == 0 || _busy) return;
        _busy = true;
        Render();
        try
        {
            var output = FreePath(Path.GetDirectoryName(_paths[0])!, "atlas", "png");
            var options = new PackOptions { Trim = _trim.IsChecked == true, PowerOfTwo = _pow2.IsChecked == true, Padding = (int)_padding.Value };
            var paths = _paths.ToList();
            var result = await Task.Run(() => Sprites.Pack(paths, output, options));
            using (var atlas = ImageIO.Load(result.Image)) Show(atlas);
            _lastOutput = result.Image;
            ShowResult(true, $"✓ {result.Count} sprites in {result.Width} × {result.Height}: {Path.GetFileName(result.Image)} + {Path.GetFileName(result.Json)}");
        }
        catch (Exception ex) { ShowResult(false, ex.Message); }
        _busy = false;
        Render();
    }

    private async Task SliceAsync()
    {
        if (_sheet is not { } sheet || _sheetPath is not { } path || _busy) return;
        _busy = true;
        Render();
        try
        {
            var folder = FramesFolder(path);
            var cells = _cells.ToList();
            var written = await Task.Run(() => Sprites.Slice(sheet, cells, folder, Path.GetFileNameWithoutExtension(path)));
            _lastOutput = written.FirstOrDefault() ?? folder;
            ShowResult(true, $"✓ {written.Count} frames in “{Path.GetFileName(folder)}”");
        }
        catch (Exception ex) { ShowResult(false, ex.Message); }
        _busy = false;
        Render();
    }

    private void ShowResult(bool ok, string text)
    {
        _result.Text = ok ? text : "✗ " + text;
        _result[!TextBlock.ForegroundProperty] = this.GetResourceObservable(ok ? "SuccessBrush" : "DangerBrush").ToBinding();
        _result.IsVisible = true;
        _reveal.IsVisible = ok;
    }

    private static StackPanel Labeled(string label, Control control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Opacity = 0.7 });
        panel.Children.Add(control);
        return panel;
    }
}
