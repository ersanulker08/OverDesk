using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

namespace Overdesk.Tool.Texture;

/// <summary>
/// The Texture Maps panel: drop a texture, watch its maps update live on a small copy while the sliders move, look at
/// any map (or the lit result, the light follows the pointer, or 2×2 tiled to check seams), then export full size
/// for an engine.
/// </summary>
internal sealed class TextureView : UserControl, IFileDropTarget
{
    private const int PreviewSize = 320;
    private static readonly (string Label, Preset Preset)[] Presets =
    {
        ("Unity URP", Preset.Unity), ("Unity HDRP", Preset.UnityHdrp), ("Unreal", Preset.Unreal), ("Godot", Preset.Godot), ("Blender", Preset.Blender), ("All", Preset.All),
    };
    private static readonly int?[] Sizes = { null, 4096, 2048, 1024, 512 };

    private readonly StackPanel _empty = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _work = new() { Spacing = 12 };
    private readonly TextBlock _name = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _info = new() { FontSize = 11, Opacity = 0.6 };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly WrapPanel _viewChips = new();
    private readonly CheckBox _tiled = new() { Content = "2×2 tiled", FontSize = 12 };
    private readonly TextBlock _lightHint = new() { Text = "Move the pointer over the image to move the light", FontSize = 11, Opacity = 0.55 };
    private readonly WrapPanel _presetChips = new();
    private readonly WrapPanel _mapChecks = new();
    private readonly WrapPanel _formatChips = new();
    private readonly ComboBox _size = new() { MinWidth = 150 };
    private readonly Button _outputButton = new() { Padding = new Thickness(10, 4), FontSize = 12 };
    private readonly Button _export;
    private readonly TextBlock _result = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _revealResult;

    private readonly Slider _heightBlur = Range(0, 8, 1.5);
    private readonly Slider _heightContrast = Range(0.2, 3, 1);
    private readonly CheckBox _invertHeight = Check("Invert height (dark = high)");
    private readonly Slider _strength = Range(0.1, 5, 1);
    private readonly Slider _aoStrength = Range(0, 3, 1);
    private readonly Slider _aoRadius = Range(2, 40, 12);
    private readonly Slider _roughLevel = Range(0, 1, 0.6);
    private readonly Slider _roughContrast = Range(0, 3, 1);
    private readonly CheckBox _invertRough = Check("Invert roughness");
    private readonly Slider _metallic = Range(0, 1, 0);
    private readonly Slider _delight = Range(0, 1, 0);
    private readonly CheckBox _tiling = Check("Tiling texture (wrap at the edges)", true);
    private readonly CheckBox _seamless = Check("Make seamless (blend the edges)");

    private string? _path;
    private TextureSource? _small;
    private Dictionary<MapKind, byte[]>? _maps;
    private MapKind? _view = MapKind.Normal; // null = lit
    private Preset _preset = Preset.Unity;
    private bool _directX;
    private readonly HashSet<MapKind> _chosen = new(Maps.MapsFor(Preset.Unity));
    private string _format = "png";
    private string? _outputFolder;
    private Vector _light = new(0.3, 0.25);
    private CancellationTokenSource? _pending;
    private int _generation;
    private bool _exporting;

    public TextureView()
    {
        _empty.Children.Add(Badge(this, "IconSliders"));
        _empty.Children.Add(new TextBlock { Text = "Drop a texture", FontSize = 17, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock
        {
            Text = "Any image (PNG, JPG, TGA, WebP, HEIC, PSD…) becomes normal, height, AO, roughness, metallic, curvature and packed ORM / mask maps for Unity, Unreal, Godot or Blender.",
            FontSize = 12, Opacity = 0.65, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300,
        });
        var browse = new Button { Content = "Choose a texture…", HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 7), Margin = new Thickness(0, 6, 0, 0) };
        browse.Classes.Add("accent");
        browse.Click += (_, _) => _ = BrowseAsync();
        _empty.Children.Add(browse);

        // Header: name + size, change / clear.
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(_name);
        names.Children.Add(_info);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(names);
        var change = SmallButton("Change…", () => _ = BrowseAsync());
        Grid.SetColumn(change, 1);
        header.Children.Add(change);
        var clear = SmallButton("Clear", () => { _path = null; _small = null; _maps = null; Render(); });
        Grid.SetColumn(clear, 2);
        header.Children.Add(clear);

        var stage = new Border
        {
            CornerRadius = new CornerRadius(10), ClipToBounds = true, Height = 260, Child = _preview,
            [!Border.BackgroundProperty] = this.GetResourceObservable("Bg2Brush").ToBinding(),
        };
        stage.PointerMoved += (_, e) =>
        {
            if (_view is not null) return;
            var p = e.GetPosition(_preview);
            if (_preview.Bounds.Width < 1) return;
            _light = new Vector(Math.Clamp(p.X / _preview.Bounds.Width, 0, 1), Math.Clamp(p.Y / _preview.Bounds.Height, 0, 1));
            ShowPreview();
        };
        _tiled.IsCheckedChanged += (_, _) => ShowPreview();
        var viewRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        viewRow.Children.Add(_lightHint);
        Grid.SetColumn(_tiled, 1);
        viewRow.Children.Add(_tiled);

        foreach (var slider in new[] { _heightBlur, _heightContrast, _strength, _aoStrength, _aoRadius, _roughLevel, _roughContrast, _metallic, _delight })
            slider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) Regenerate(); };
        foreach (var check in new[] { _invertHeight, _invertRough, _tiling, _seamless })
            check.IsCheckedChanged += (_, _) => Regenerate();

        _size.ItemsSource = Sizes.Select(s => s is null ? "Original size" : $"Longest edge {s} px").ToList();
        _size.SelectedIndex = 0;
        _outputButton.Classes.Add("ghost");
        _outputButton.Click += (_, _) => _ = ChooseOutputAsync();
        _export = AccentButton("Export maps", () => _ = ExportAsync());
        _revealResult = SmallButton("Show in folder", () => { });
        _revealResult.IsVisible = false;

        var outputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        outputRow.Children.Add(new TextBlock { Text = "Save to", FontSize = 12, Width = 62, VerticalAlignment = VerticalAlignment.Center });
        outputRow.Children.Add(_outputButton);
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizeRow.Children.Add(new TextBlock { Text = "Size", FontSize = 12, Width = 62, VerticalAlignment = VerticalAlignment.Center });
        sizeRow.Children.Add(_size);

        _work.Children.Add(header);
        _work.Children.Add(stage);
        _work.Children.Add(_viewChips);
        _work.Children.Add(viewRow);
        _work.Children.Add(Eyebrow("HEIGHT & NORMAL"));
        _work.Children.Add(SliderRow("Detail blur", _heightBlur));
        _work.Children.Add(SliderRow("Depth contrast", _heightContrast));
        _work.Children.Add(SliderRow("Normal strength", _strength));
        _work.Children.Add(_invertHeight);
        _work.Children.Add(Eyebrow("OCCLUSION & SURFACE"));
        _work.Children.Add(SliderRow("AO strength", _aoStrength));
        _work.Children.Add(SliderRow("AO radius", _aoRadius, "0"));
        _work.Children.Add(SliderRow("Roughness", _roughLevel));
        _work.Children.Add(SliderRow("Rough contrast", _roughContrast));
        _work.Children.Add(_invertRough);
        _work.Children.Add(SliderRow("Metallic", _metallic));
        _work.Children.Add(SliderRow("Remove shading", _delight));
        _work.Children.Add(_tiling);
        _work.Children.Add(_seamless);
        _work.Children.Add(Eyebrow("EXPORT FOR"));
        _work.Children.Add(_presetChips);
        _work.Children.Add(_mapChecks);
        _work.Children.Add(Eyebrow("FORMAT"));
        _work.Children.Add(_formatChips);
        _work.Children.Add(sizeRow);
        _work.Children.Add(outputRow);
        _work.Children.Add(_export);
        var resultRow = new StackPanel { Spacing = 4 };
        resultRow.Children.Add(_result);
        resultRow.Children.Add(_revealResult);
        _work.Children.Add(resultRow);

        var scroller = new ScrollViewer { Content = new Border { Padding = new Thickness(14, 6, 14, 16), Child = _work } };
        var root = new Panel();
        root.Children.Add(scroller);
        root.Children.Add(_empty);
        Content = root;
        Render();
    }

    public string DropHint => "Drop a texture to make its maps.";

    public void FilesDropped(IReadOnlyList<string> paths)
    {
        if (paths.FirstOrDefault(ImageIO.CanRead) is { } path) Dispatcher.UIThread.Post(() => _ = LoadAsync(path));
    }

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Texture", AllowMultiple = false });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await LoadAsync(path);
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
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where the maps go" });
        _outputFolder = folders.FirstOrDefault()?.TryGetLocalPath();
        Render();
    }

    private async Task LoadAsync(string path)
    {
        try
        {
            var (small, width, height) = await Task.Run(() =>
            {
                using var full = ImageIO.Load(path);
                var scale = Math.Min(1, PreviewSize / (double)Math.Max(full.Width, full.Height));
                using var resized = ImageIO.Resize(full, (int)Math.Round(full.Width * scale), (int)Math.Round(full.Height * scale));
                return (Maps.FromBitmap(resized), full.Width, full.Height);
            });
            _path = path;
            _small = small;
            _info.Text = $"{width} × {height} · {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}";
            _result.IsVisible = _revealResult.IsVisible = false;
            Render();
            Regenerate();
        }
        catch (Exception ex)
        {
            _path = null;
            Render();
            _empty.Children.OfType<TextBlock>().Skip(1).First().Text = ex.Message;
        }
    }

    /// <summary>The knobs as settings (the normal flavor follows the preset).</summary>
    private MapSettings Settings() => new()
    {
        HeightBlur = (float)_heightBlur.Value,
        HeightContrast = (float)_heightContrast.Value,
        InvertHeight = _invertHeight.IsChecked == true,
        NormalStrength = (float)_strength.Value,
        DirectX = _directX,
        AoStrength = (float)_aoStrength.Value,
        AoRadius = (float)_aoRadius.Value,
        RoughnessLevel = (float)_roughLevel.Value,
        RoughnessContrast = (float)_roughContrast.Value,
        InvertRoughness = _invertRough.IsChecked == true,
        Metallic = (float)_metallic.Value,
        RemoveShading = (float)_delight.Value,
        Tiling = _tiling.IsChecked == true,
        MakeSeamless = _seamless.IsChecked == true,
    };

    /// <summary>Recompute the preview maps shortly after the last change (newest wins).</summary>
    private void Regenerate()
    {
        if (_small is not { } small) return;
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        var generation = ++_generation;
        var settings = Settings();
        _ = Task.Run(async () =>
        {
            await Task.Delay(90, cts.Token);
            var maps = Maps.Generate(small, settings, Maps.Single.Concat(Maps.Packed).ToArray(), cts.Token);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _generation) return;
                _maps = maps;
                ShowPreview();
            });
        }, cts.Token);
    }

    private void ShowPreview()
    {
        if (_maps is null || _small is null) return;
        int w = _small.Width, h = _small.Height;
        var pixels = _view is { } kind ? _maps[kind] : Lit(w, h);
        var tiled = _tiled.IsChecked == true;
        var (tw, th) = tiled ? (w * 2, h * 2) : (w, h);
        var bitmap = new WriteableBitmap(new PixelSize(tw, th), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var buffer = bitmap.Lock())
        {
            for (var y = 0; y < th; y++)
                Marshal.Copy(pixels, (y % h) * w * 4, buffer.Address + y * buffer.RowBytes, w * 4);
            if (tiled)
                for (var y = 0; y < th; y++)
                    Marshal.Copy(pixels, (y % h) * w * 4, buffer.Address + y * buffer.RowBytes + w * 4, w * 4);
        }
        var old = _preview.Source as IDisposable;
        _preview.Source = bitmap;
        old?.Dispose();
    }

    /// <summary>Base color lit through the normal map by a light where the pointer is, plus a little AO and shine.</summary>
    private byte[] Lit(int w, int h)
    {
        var albedo = _maps![MapKind.Albedo];
        var normal = _maps[MapKind.Normal];
        var ao = _maps[MapKind.AmbientOcclusion];
        var rough = _maps[MapKind.Roughness];
        var result = new byte[w * h * 4];
        var flip = _directX ? -1f : 1f;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                float nx = normal[i] / 127.5f - 1, ny = (normal[i + 1] / 127.5f - 1) * flip, nz = normal[i + 2] / 127.5f - 1;
                // Light above the pointer, a little in front of the surface (y up in tangent space).
                float lx = (float)_light.X - x / (float)w, ly = y / (float)h - (float)_light.Y, lz = 0.45f;
                var length = MathF.Sqrt(lx * lx + ly * ly + lz * lz);
                lx /= length; ly /= length; lz /= length;
                var diffuse = MathF.Max(0, nx * lx + ny * ly + nz * lz);
                var hz = lz + 1;
                var hl = MathF.Sqrt(lx * lx + ly * ly + hz * hz);
                var spec = MathF.Pow(MathF.Max(0, (nx * lx + ny * ly + nz * hz) / hl), 8 + (1 - rough[i] / 255f) * 90) * (1 - rough[i] / 255f) * 0.6f;
                var light = (0.18f + diffuse * 0.95f) * (0.35f + 0.65f * ao[i] / 255f);
                for (var c = 0; c < 3; c++) result[i + c] = (byte)Math.Clamp((int)(albedo[i + c] * light + spec * 255), 0, 255);
                result[i + 3] = 255;
            }
        });
        return result;
    }

    private void Render()
    {
        _empty.IsVisible = _path is null;
        _work.IsVisible = _path is not null;
        if (_path is not null) _name.Text = Path.GetFileName(_path);

        _viewChips.Children.Clear();
        _viewChips.Children.Add(Chip("Lit", _view is null, () => { _view = null; Render(); ShowPreview(); }));
        foreach (var kind in new[] { MapKind.Albedo, MapKind.Normal, MapKind.Height, MapKind.AmbientOcclusion, MapKind.Roughness, MapKind.Metallic, MapKind.Curvature, MapKind.Orm })
        {
            var k = kind;
            _viewChips.Children.Add(Chip(Maps.Suffix(kind) is "BaseColor" ? "Color" : Maps.Suffix(kind), _view == kind, () => { _view = k; Render(); ShowPreview(); }));
        }
        _lightHint.Opacity = _view is null ? 0.55 : 0;

        _presetChips.Children.Clear();
        foreach (var (label, preset) in Presets)
            _presetChips.Children.Add(Chip(label, preset == _preset, () =>
            {
                _preset = preset;
                _directX = MapSettings.For(preset).DirectX;
                _chosen.Clear();
                _chosen.UnionWith(Maps.MapsFor(preset));
                Render();
                Regenerate();
            }));
        var normalFlavor = Chip(_directX ? "Normal: DirectX (Y−)" : "Normal: OpenGL (Y+)", true, () => { _directX = !_directX; Render(); Regenerate(); });
        ToolTip.SetTip(normalFlavor, "Unity, Blender and Godot use OpenGL normal maps; Unreal uses DirectX. Click to switch.");
        _presetChips.Children.Add(normalFlavor);

        _mapChecks.Children.Clear();
        foreach (var kind in Maps.Single.Concat(Maps.Packed))
        {
            var check = new CheckBox { Content = Maps.Label(kind), FontSize = 12, IsChecked = _chosen.Contains(kind), Margin = new Thickness(0, 0, 12, 0), MinWidth = 140 };
            var k = kind;
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) _chosen.Add(k); else _chosen.Remove(k);
                UpdateExport();
            };
            _mapChecks.Children.Add(check);
        }

        _formatChips.Children.Clear();
        foreach (var format in ImageIO.Writable.Where(f => f is not "ico" and not "bmp" and not "heic"))
        {
            var f = format;
            _formatChips.Children.Add(Chip(format.ToUpperInvariant(), format == _format, () => { _format = f; Render(); }));
        }
        _outputButton.Content = _outputFolder is null ? "Next to the texture" : $"{Path.GetFileName(_outputFolder)} (click to reset)";
        UpdateExport();
    }

    private void UpdateExport()
    {
        _export.IsEnabled = !_exporting && _chosen.Count > 0 && _path is not null;
        _export.Content = _exporting ? "Exporting…" : $"Export {_chosen.Count} map{(_chosen.Count == 1 ? "" : "s")}";
    }

    private async Task ExportAsync()
    {
        if (_path is not { } path || _exporting) return;
        _exporting = true;
        UpdateExport();
        try
        {
            var kinds = Maps.Single.Concat(Maps.Packed).Where(_chosen.Contains).ToArray();
            var written = await TextureExport.RunAsync(path, Settings(), kinds, _format, Sizes[Math.Max(0, _size.SelectedIndex)], _outputFolder, CancellationToken.None);
            _result.Text = $"✓ {written.Count} maps: {string.Join(", ", written.Select(Path.GetFileName))}";
            _result[!TextBlock.ForegroundProperty] = this.GetResourceObservable("SuccessBrush").ToBinding();
            _revealResult.IsVisible = written.Count > 0;
            _revealResult.Click -= RevealClicked;
            _revealedPath = written.FirstOrDefault();
            _revealResult.Click += RevealClicked;
        }
        catch (Exception ex)
        {
            _result.Text = "✗ " + ex.Message;
            _result[!TextBlock.ForegroundProperty] = this.GetResourceObservable("DangerBrush").ToBinding();
            _revealResult.IsVisible = false;
        }
        _result.IsVisible = true;
        _exporting = false;
        UpdateExport();
    }

    private string? _revealedPath;

    private void RevealClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_revealedPath is { } path) Reveal(path);
    }

    private static Slider Range(double min, double max, double value)
        => new() { Minimum = min, Maximum = max, Value = value, SmallChange = (max - min) / 100, LargeChange = (max - min) / 10 };

    private static CheckBox Check(string text, bool on = false) => new() { Content = text, FontSize = 12, IsChecked = on };
}
