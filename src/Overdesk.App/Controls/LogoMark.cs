using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls;

/// <summary>
/// The OverDesk logo: a bold, open "O" ring with a small window tile slipping over its gap.
/// Drawn from code so it stays crisp at every size and can be animated (ring sweep + tile pop) by the splash.
/// </summary>
public sealed class LogoMark : Control
{
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<LogoMark, Color>(nameof(Accent), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<Color> Accent2Property =
        AvaloniaProperty.Register<LogoMark, Color>(nameof(Accent2), Color.Parse("#FF6A3D"));
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<LogoMark, double>(nameof(Progress), 1.0);
    public static readonly StyledProperty<double> TileScaleProperty =
        AvaloniaProperty.Register<LogoMark, double>(nameof(TileScale), 1.0);
    public static readonly StyledProperty<double> GlowProperty =
        AvaloniaProperty.Register<LogoMark, double>(nameof(Glow), 0.6);
    public static readonly StyledProperty<bool> AppIconBackdropProperty =
        AvaloniaProperty.Register<LogoMark, bool>(nameof(AppIconBackdrop));

    // Ring geometry in a 100 x 100 design box. Angles are clockwise from 3 o'clock.
    private const double Radius = 34;
    private const double Thickness = 13;
    private const double GapStart = -82;   // ring starts after the gap...
    private const double GapEnd = -8;      // ...and sweeps clockwise all the way round to here.
    private const double TileAngle = -45;

    static LogoMark()
    {
        AffectsRender<LogoMark>(AccentProperty, Accent2Property, ProgressProperty, TileScaleProperty, GlowProperty,
            AppIconBackdropProperty);
    }

    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Color Accent2 { get => GetValue(Accent2Property); set => SetValue(Accent2Property, value); }

    /// <summary>0..1: how much of the ring is drawn.</summary>
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    /// <summary>0..1+: scale of the window tile (overshoot allowed for a pop).</summary>
    public double TileScale { get => GetValue(TileScaleProperty); set => SetValue(TileScaleProperty, value); }

    /// <summary>0..1: strength of the soft neon glow.</summary>
    public double Glow { get => GetValue(GlowProperty); set => SetValue(GlowProperty, value); }

    /// <summary>Draw a dark rounded-square backdrop, for app/dock icons.</summary>
    public bool AppIconBackdrop { get => GetValue(AppIconBackdropProperty); set => SetValue(AppIconBackdropProperty, value); }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var scale = size / 100.0;
        var offset = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            if (AppIconBackdrop) DrawBackdrop(context);
            DrawRing(context);
            DrawTile(context);
        }
    }

    private void DrawBackdrop(DrawingContext context)
    {
        var rect = new RoundedRect(new Rect(4, 4, 92, 92), 22);
        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#1D1D29"), 0), new GradientStop(Color.Parse("#0B0B10"), 1) },
        }, new Pen(new SolidColorBrush(AccentThemes.WithAlpha(Accent, 0x50)), 1.2), rect);
    }

    private void DrawRing(DrawingContext context)
    {
        var progress = Math.Clamp(Progress, 0, 1);
        if (progress <= 0) return;

        var sweep = (360 - (GapEnd - GapStart)) * progress;
        var start = GapEnd;
        var geometry = Arc(new Point(50, 50), Radius, start, start + sweep);

        var glow = Math.Clamp(Glow, 0, 1);
        if (glow > 0)
        {
            // A cheap, portable neon bloom: the same arc stroked wider and fainter a few times.
            for (var i = 3; i >= 1; i--)
            {
                var alpha = (byte)(glow * (0x16 + (3 - i) * 0x10));
                var pen = new Pen(new SolidColorBrush(AccentThemes.WithAlpha(Accent, alpha)), Thickness + i * 5, lineCap: PenLineCap.Round);
                context.DrawGeometry(null, pen, geometry);
            }
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.1, 0.9, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.9, 0.1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Accent, 0), new GradientStop(Accent2, 1) },
        };
        context.DrawGeometry(null, new Pen(brush, Thickness, lineCap: PenLineCap.Round), geometry);

        // Thin inner highlight gives the ring some depth.
        var inner = Arc(new Point(50, 50), Radius - Thickness / 2 + 1.2, start + 6, start + Math.Max(sweep - 6, 6));
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)), 1.1, lineCap: PenLineCap.Round), inner);
    }

    private void DrawTile(DrawingContext context)
    {
        var s = TileScale;
        if (s <= 0.01) return;

        var rad = TileAngle * Math.PI / 180;
        var center = new Point(50 + (Radius + 4) * Math.Cos(rad), 50 + (Radius + 4) * Math.Sin(rad));
        var side = 25 * s;
        var rect = new Rect(center.X - side / 2, center.Y - side / 2, side, side);

        var glow = Math.Clamp(Glow, 0, 1);
        if (glow > 0)
            context.DrawRectangle(null, null, new RoundedRect(rect, 7 * s),
                new BoxShadows(new BoxShadow { Blur = 14 * glow, Color = AccentThemes.WithAlpha(Accent2, 0xA0) }));

        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Accent, 0), new GradientStop(Accent2, 1) },
        }, null, new RoundedRect(rect, 7 * s));

        // The "title bar" of the little window.
        var bar = new Rect(rect.X + 5 * s, rect.Y + 5.5 * s, rect.Width - 10 * s, 3.2 * s);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xD8, 255, 255, 255)), null, new RoundedRect(bar, 1.6 * s));
    }

    private static StreamGeometry Arc(Point c, double r, double fromDeg, double toDeg)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        var from = fromDeg * Math.PI / 180;
        var to = toDeg * Math.PI / 180;
        ctx.BeginFigure(new Point(c.X + r * Math.Cos(from), c.Y + r * Math.Sin(from)), false);
        ctx.ArcTo(new Point(c.X + r * Math.Cos(to), c.Y + r * Math.Sin(to)), new Size(r, r), 0,
            toDeg - fromDeg > 180, SweepDirection.Clockwise);
        ctx.EndFigure(false);
        return geometry;
    }
}
