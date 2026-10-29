using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overdesk.App.Services;
using Overdesk.Core.Tabs;

namespace Overdesk.App.Controls;

/// <summary>
/// A program's real icon when the OS gives us one, otherwise a colorful monogram tile
/// (the color is derived from the name, so a program always gets the same one).
/// </summary>
public sealed class AppIcon : Control
{
    public static readonly StyledProperty<string?> ProgramPathProperty =
        AvaloniaProperty.Register<AppIcon, string?>(nameof(ProgramPath));

    private Bitmap? _bitmap;
    private string _name = "";

    static AppIcon() => AffectsRender<AppIcon>(ProgramPathProperty);

    public string? ProgramPath { get => GetValue(ProgramPathProperty); set => SetValue(ProgramPathProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ProgramPathProperty) return;
        var path = ProgramPath;
        _name = string.IsNullOrEmpty(path) ? "?" : ProgramNames.FromPath(path);
        // Until the real icon arrives the monogram tile is shown.
        if (!IconCache.TryGet(path, out _bitmap, bitmap =>
            {
                if (ProgramPath != path) return;
                _bitmap = bitmap;
                InvalidateVisual();
            }))
            _bitmap = null;
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var rect = new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size);

        if (_bitmap is not null)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                context.DrawImage(_bitmap, rect);
            return;
        }

        var hue = StableHue(_name);
        var inset = rect.Deflate(size * 0.06);
        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Hsl(hue, 0.85, 0.58), 0), new GradientStop(Hsl(hue + 40, 0.80, 0.42), 1) },
        }, null, new RoundedRect(inset, size * 0.24));

        var letter = _name.Length > 0 ? _name[..1].ToUpperInvariant() : "?";
        var text = new FormattedText(letter, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), size * 0.5, Brushes.White);
        context.DrawText(text, new Point(inset.Center.X - text.Width / 2, inset.Center.Y - text.Height / 2));
    }

    private static double StableHue(string name)
    {
        var hash = 17;
        foreach (var ch in name) hash = unchecked(hash * 31 + char.ToLowerInvariant(ch));
        return (uint)hash % 360;
    }

    private static Color Hsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        double R(double t)
        {
            t = (t + 1) % 1;
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return Color.FromRgb((byte)(R(h + 1.0 / 3) * 255), (byte)(R(h) * 255), (byte)(R(h - 1.0 / 3) * 255));
    }
}
