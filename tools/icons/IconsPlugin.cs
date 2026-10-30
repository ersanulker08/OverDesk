using System.Globalization;
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

namespace Overdesk.Tool.Icons;

/// <summary>Entry point named by <c>typeName</c> in overdesk.plugin.json. Also lets the AI make icon sets.</summary>
public sealed class IconsPlugin : IToolPlugin, IAiToolSource
{
    public object CreateView() => new IconsView();

    public IReadOnlyList<PluginAiTool> AiTools { get; } = new[]
    {
        new PluginAiTool(
            "make_app_icons",
            "Make every app icon file from one image with OverDesk's App Icons tool: macOS .icns, Windows .ico, iOS AppIcon.appiconset, " +
            "Android launcher mipmaps (square + round + Play Store 512) and web favicons (favicon.ico, PNGs, apple-touch-icon, site.webmanifest, " +
            "head.html). Written into a new \"<name> icons\" folder next to the image unless output_folder is given.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Absolute path of the source image (ideally square, 1024 px or more)."},
              "targets":{"type":"array","items":{"type":"string","enum":["macos","windows","ios","android","web"]},"description":"Default: all."},
              "shape":{"type":"string","enum":["as_is","rounded_square","circle"],"description":"Default as_is."},
              "padding":{"type":"number","description":"0-0.4 of the side."},
              "background":{"type":"string","description":"Hex color behind the art, e.g. #1e1e2e; default transparent."},
              "output_folder":{"type":"string"}
            },"required":["path"]}
            """,
            async (json, cancellation) =>
            {
                var args = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
                var path = args["path"]?.ToString() ?? "";
                if (!File.Exists(path)) throw new FileNotFoundException("No such image.", path);
                var targets = (args["targets"] as JsonArray)?.Select(t => t?.ToString() switch
                {
                    "macos" => IconTarget.MacOS, "windows" => IconTarget.Windows, "ios" => IconTarget.iOS, "android" => IconTarget.Android, "web" => IconTarget.Web,
                    var other => throw new InvalidOperationException($"Unknown target {other}."),
                }).ToArray() ?? Enum.GetValues<IconTarget>();
                var options = new IconOptions
                {
                    Shape = args["shape"]?.ToString() switch { "rounded_square" => IconShape.RoundedSquare, "circle" => IconShape.Circle, _ => IconShape.AsIs },
                    Padding = args["padding"] is JsonValue p && float.TryParse(p.ToString(), CultureInfo.InvariantCulture, out var pad) ? pad : 0,
                    Background = args["background"]?.ToString() is { Length: > 0 } hex && SKColor.TryParse(hex, out var color) ? color : null,
                };
                var folder = args["output_folder"]?.ToString() is { Length: > 0 } o ? o : IconsView.DefaultFolder(path);
                var written = await Task.Run(() =>
                {
                    using var source = ImageIO.Load(path);
                    return IconSets.Generate(source, folder, targets, options);
                }, cancellation);
                return $"Wrote {written.Count} files into {folder}:\n" + string.Join("\n", written.Select(f => Path.GetRelativePath(folder, f)));
            })
        {
            Label = json => $"Making app icons from {Path.GetFileName(JsonNode.Parse(json)?["path"]?.ToString() ?? "an image")}",
        },
    };
}

/// <summary>The App Icons panel: drop artwork, pick shape / padding / background, see it at real sizes, generate.</summary>
internal sealed class IconsView : UserControl, IFileDropTarget
{
    private static readonly (string Label, SKColor? Color)[] Backgrounds =
    {
        ("None", null), ("White", SKColors.White), ("Black", SKColors.Black), ("Dark", new SKColor(0x1E, 0x1E, 0x2E)),
    };

    private readonly StackPanel _empty = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _work = new() { Spacing = 12 };
    private readonly TextBlock _name = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _info = new() { FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _previews = new() { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly WrapPanel _shapes = new();
    private readonly WrapPanel _backgrounds = new();
    private readonly TextBox _hex = new() { PlaceholderText = "#hex", Width = 90, FontSize = 12 };
    private readonly Slider _padding = new() { Minimum = 0, Maximum = 0.4, Value = 0, SmallChange = 0.01, LargeChange = 0.05 };
    private readonly WrapPanel _targets = new();
    private readonly Button _generate;
    private readonly TextBlock _result = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _reveal;
    private readonly HashSet<IconTarget> _chosen = new(Enum.GetValues<IconTarget>());
    private string? _path;
    private SKBitmap? _source;
    private IconShape _shape = IconShape.AsIs;
    private SKColor? _background;
    private string? _lastFolder;
    private bool _busy;

    public IconsView()
    {
        _empty.Children.Add(Badge(this, "IconApp"));
        _empty.Children.Add(new TextBlock { Text = "Drop your app's artwork", FontSize = 17, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock
        {
            Text = "One image (1024 px or larger is best) becomes macOS .icns, Windows .ico, the iOS AppIcon set, Android launcher icons and web favicons.",
            FontSize = 12, Opacity = 0.65, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300,
        });
        var browse = new Button { Content = "Choose an image…", HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 7), Margin = new Thickness(0, 6, 0, 0) };
        browse.Classes.Add("accent");
        browse.Click += (_, _) => _ = BrowseAsync();
        _empty.Children.Add(browse);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(_name);
        names.Children.Add(_info);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(names);
        var change = SmallButton("Change…", () => _ = BrowseAsync());
        Grid.SetColumn(change, 1);
        header.Children.Add(change);

        _padding.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) ShowPreviews(); };
        _hex.TextChanged += (_, _) =>
        {
            if (SKColor.TryParse(_hex.Text ?? "", out var color)) { _background = color; Render(); }
        };
        _generate = AccentButton("Generate icons", () => _ = GenerateAsync());
        _reveal = SmallButton("Show in folder", () => { if (_lastFolder is not null) Reveal(_lastFolder); });
        _reveal.IsVisible = false;
        _reveal.HorizontalAlignment = HorizontalAlignment.Left;

        var backgroundRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        backgroundRow.Children.Add(_backgrounds);
        backgroundRow.Children.Add(_hex);

        _work.Children.Add(header);
        _work.Children.Add(new Border
        {
            Padding = new Thickness(14), CornerRadius = new CornerRadius(10), Child = _previews, MinHeight = 150,
            [!Border.BackgroundProperty] = this.GetResourceObservable("Bg2Brush").ToBinding(),
        });
        _work.Children.Add(Eyebrow("SHAPE"));
        _work.Children.Add(_shapes);
        _work.Children.Add(SliderRow("Padding", _padding, "0%"));
        _work.Children.Add(Eyebrow("BACKGROUND"));
        _work.Children.Add(backgroundRow);
        _work.Children.Add(Eyebrow("PLATFORMS"));
        _work.Children.Add(_targets);
        _work.Children.Add(_generate);
        _work.Children.Add(_result);
        _work.Children.Add(_reveal);

        var scroller = new ScrollViewer { Content = new Border { Padding = new Thickness(14, 6, 14, 16), Child = _work } };
        var root = new Panel();
        root.Children.Add(scroller);
        root.Children.Add(_empty);
        Content = root;
        Render();
    }

    public static string DefaultFolder(string path) => FreePath(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " icons", "");

    public string DropHint => "Drop artwork to make app icons from it.";

    public void FilesDropped(IReadOnlyList<string> paths)
    {
        if (paths.FirstOrDefault(ImageIO.CanRead) is { } path) Dispatcher.UIThread.Post(() => _ = LoadAsync(path));
    }

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "App artwork", AllowMultiple = false });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await LoadAsync(path);
    }

    private async Task LoadAsync(string path)
    {
        try
        {
            var bitmap = await Task.Run(() => ImageIO.Load(path));
            _source?.Dispose();
            _source = bitmap;
            _path = path;
            _info.Text = $"{bitmap.Width} × {bitmap.Height}" + (Math.Min(bitmap.Width, bitmap.Height) < 1024 ? " · smaller than 1024 px: big icons will be soft" : "")
                + (bitmap.Width != bitmap.Height ? " · not square: it is centered" : "");
            _result.IsVisible = _reveal.IsVisible = false;
            Render();
        }
        catch (Exception ex)
        {
            _empty.Children.OfType<TextBlock>().Skip(1).First().Text = ex.Message;
        }
    }

    private IconOptions Options() => new() { Shape = _shape, Background = _background, Padding = (float)_padding.Value };

    private void Render()
    {
        _empty.IsVisible = _source is null;
        _work.IsVisible = _source is not null;
        if (_path is not null) _name.Text = Path.GetFileName(_path);

        _shapes.Children.Clear();
        foreach (var (label, shape) in new[] { ("As it is", IconShape.AsIs), ("Rounded square", IconShape.RoundedSquare), ("Circle", IconShape.Circle) })
            _shapes.Children.Add(Chip(label, shape == _shape, () => { _shape = shape; Render(); }));
        _backgrounds.Children.Clear();
        foreach (var (label, color) in Backgrounds)
            _backgrounds.Children.Add(Chip(label, color == _background, () => { _background = color; _hex.Text = ""; Render(); }));
        _targets.Children.Clear();
        foreach (var target in Enum.GetValues<IconTarget>())
        {
            var check = new CheckBox { Content = IconSets.Label(target), FontSize = 12, IsChecked = _chosen.Contains(target), MinWidth = 150 };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) _chosen.Add(target); else _chosen.Remove(target);
                _generate.IsEnabled = _chosen.Count > 0 && !_busy;
            };
            _targets.Children.Add(check);
        }
        _generate.IsEnabled = _chosen.Count > 0 && !_busy;
        _generate.Content = _busy ? "Generating…" : "Generate icons";
        ShowPreviews();
    }

    /// <summary>The icon at a few real sizes, on the panel's own background.</summary>
    private void ShowPreviews()
    {
        _previews.Children.Clear();
        if (_source is null) return;
        var options = Options();
        foreach (var size in new[] { 128, 64, 32, 16 })
        {
            using var rendered = IconSets.Render(_source, size * 2, options);
            var bitmap = new WriteableBitmap(new PixelSize(size * 2, size * 2), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using (var buffer = bitmap.Lock())
            {
                var pixels = ImageIO.Pixels(rendered);
                for (var y = 0; y < size * 2; y++) Marshal.Copy(pixels, y * size * 8, buffer.Address + y * buffer.RowBytes, size * 8);
            }
            var column = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
            column.Children.Add(new Image { Source = bitmap, Width = size, Height = size, Stretch = Stretch.Uniform });
            column.Children.Add(new TextBlock { Text = $"{size}", FontSize = 10, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center });
            _previews.Children.Add(column);
        }
    }

    private async Task GenerateAsync()
    {
        if (_source is not { } source || _path is not { } path || _busy) return;
        _busy = true;
        Render();
        try
        {
            var folder = DefaultFolder(path);
            var targets = _chosen.ToArray();
            var options = Options();
            var written = await Task.Run(() => IconSets.Generate(source, folder, targets, options));
            _lastFolder = folder;
            _result.Text = $"✓ {written.Count} files in “{Path.GetFileName(folder)}”";
            _result[!TextBlock.ForegroundProperty] = this.GetResourceObservable("SuccessBrush").ToBinding();
            _reveal.IsVisible = true;
        }
        catch (Exception ex)
        {
            _result.Text = "✗ " + ex.Message;
            _result[!TextBlock.ForegroundProperty] = this.GetResourceObservable("DangerBrush").ToBinding();
            _reveal.IsVisible = false;
        }
        _result.IsVisible = true;
        _busy = false;
        Render();
    }
}
