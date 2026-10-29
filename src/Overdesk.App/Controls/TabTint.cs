using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Overdesk.App.Services;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls;

/// <summary>
/// Soft, semi-transparent wash of a tab in the main color of its program's icon, plus a glowing underline
/// when the tab is active. Overdesk's own tab uses the accent color instead. Kept gentle on purpose so a row
/// of colorful tabs never tires the eyes.
/// </summary>
public sealed class TabTint : Control
{
    public static readonly StyledProperty<string?> ProgramPathProperty =
        AvaloniaProperty.Register<TabTint, string?>(nameof(ProgramPath));
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<TabTint, bool>(nameof(IsActive));
    public static readonly StyledProperty<bool> UseAccentProperty =
        AvaloniaProperty.Register<TabTint, bool>(nameof(UseAccent));
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<TabTint, Color>(nameof(Accent), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<Color> Accent2Property =
        AvaloniaProperty.Register<TabTint, Color>(nameof(Accent2), Color.Parse("#FF6A3D"));

    private static readonly Dictionary<string, Color> Colors = new(StringComparer.Ordinal);
    private Color? _iconColor;

    static TabTint()
    {
        AffectsRender<TabTint>(IsActiveProperty, UseAccentProperty, AccentProperty, Accent2Property);
        IsHitTestVisibleProperty.OverrideDefaultValue<TabTint>(false);
    }

    public string? ProgramPath { get => GetValue(ProgramPathProperty); set => SetValue(ProgramPathProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool UseAccent { get => GetValue(UseAccentProperty); set => SetValue(UseAccentProperty, value); }
    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Color Accent2 { get => GetValue(Accent2Property); set => SetValue(Accent2Property, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ProgramPathProperty) return;
        var path = ProgramPath;
        _iconColor = null;
        if (string.IsNullOrEmpty(path) || path.StartsWith("overdesk://", StringComparison.Ordinal)) return;
        if (Colors.TryGetValue(path, out var known))
        {
            _iconColor = known;
            return;
        }
        if (IconCache.TryGet(path, out var bitmap, b => SetColorFrom(path, b))) SetColorFrom(path, bitmap);
    }

    private void SetColorFrom(string path, Bitmap? bitmap)
    {
        var color = bitmap is null ? FallbackColor(path) : Dominant(bitmap);
        Colors[path] = color;
        if (ProgramPath != path) return;
        _iconColor = color;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 2 || h < 2) return;

        var main = UseAccent ? Accent : _iconColor ?? Color.FromRgb(150, 150, 170);
        var second = UseAccent ? Accent2 : main;
        var active = IsActive;

        // Overdesk's own tab is a little stronger so it always reads as "the app itself".
        var top = (byte)(UseAccent ? (active ? 0x3A : 0x22) : (active ? 0x2C : 0x14));
        var bottom = (byte)(UseAccent ? (active ? 0x66 : 0x34) : (active ? 0x4A : 0x22));
        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(AccentThemes.WithAlpha(main, top), 0),
                new GradientStop(AccentThemes.WithAlpha(second, bottom), 1),
            },
        }, null, new RoundedRect(new Rect(0, 0, w, h), 10));

        if (!active) return;
        var line = new Rect(10, h - 2, Math.Max(w - 20, 4), 2);
        context.DrawRectangle(AccentThemes.Gradient(main, second, horizontal: true), null, new RoundedRect(line, 1),
            new BoxShadows(new BoxShadow { Blur = 10, Color = AccentThemes.WithAlpha(main, 0xB0) }));
    }

    /// <summary>
    /// The icon's main color: hue buckets weighted by how vivid each pixel is, then softened (no neon,
    /// no near-black) so it works as a background wash. Grey icons give a neutral tint.
    /// </summary>
    private static unsafe Color Dominant(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        if (size.Width <= 0 || size.Height <= 0) return Color.FromRgb(150, 150, 170);
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        fixed (byte* p = pixels)
        {
            bitmap.CopyPixels(new PixelRect(size), (IntPtr)p, pixels.Length, stride);
        }

        const int buckets = 24;
        var weight = new double[buckets];
        var sumR = new double[buckets];
        var sumG = new double[buckets];
        var sumB = new double[buckets];
        double greyWeight = 0;

        // Decoded PNGs are RGBA on macOS and BGRA on Windows: read the channels in the bitmap's own order.
        var rgba = bitmap.Format == PixelFormats.Rgba8888;
        var (ri, bi) = rgba ? (0, 2) : (2, 0);
        for (var i = 0; i < pixels.Length; i += 16) // every 4th pixel is plenty
        {
            var a = pixels[i + 3];
            if (a < 160) continue;
            double r = pixels[i + ri] / 255.0, g = pixels[i + 1] / 255.0, b = pixels[i + bi] / 255.0;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var sat = max <= 0 ? 0 : (max - min) / max;
            if (sat < 0.25 || max < 0.2)
            {
                greyWeight += 1;
                continue;
            }
            var hue = RgbHue(r, g, b);
            var bucket = (int)(hue / 360 * buckets) % buckets;
            var wgt = sat * max;
            weight[bucket] += wgt;
            sumR[bucket] += r * wgt;
            sumG[bucket] += g * wgt;
            sumB[bucket] += b * wgt;
        }

        var best = Array.IndexOf(weight, weight.Max());
        if (weight[best] <= 0 || weight.Sum() < greyWeight * 0.08)
            return Color.FromRgb(160, 160, 178);

        var c = Color.FromRgb((byte)(sumR[best] / weight[best] * 255), (byte)(sumG[best] / weight[best] * 255), (byte)(sumB[best] / weight[best] * 255));
        return Soften(c);
    }

    private static double RgbHue(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        if (d <= 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>Keep the hue, tame saturation and lightness into a pleasant, readable range.</summary>
    private static Color Soften(Color c)
    {
        var hsl = c.ToHsl();
        return HslColor.ToRgb(hsl.H, Math.Clamp(hsl.S, 0.45, 0.8), Math.Clamp(hsl.L, 0.5, 0.65));
    }

    private static Color FallbackColor(string path)
    {
        var hash = 17;
        foreach (var ch in Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\'))) hash = unchecked(hash * 31 + char.ToLowerInvariant(ch));
        return HslColor.ToRgb((uint)hash % 360, 0.6, 0.58);
    }
}
