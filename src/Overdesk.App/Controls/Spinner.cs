using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls;

/// <summary>A rotating accent arc with a fading tail, used while a program starts.</summary>
public sealed class Spinner : Control
{
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<Spinner, Color>(nameof(Accent), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<Spinner, double>(nameof(Thickness), 3);

    private double _angle;
    private TimeSpan? _last;
    private bool _requested;

    static Spinner() => AffectsRender<Spinner>(AccentProperty, ThicknessProperty);

    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Request();
    }

    private void Request()
    {
        if (_requested || TopLevel.GetTopLevel(this) is not { } top) return;
        _requested = true;
        top.RequestAnimationFrame(now =>
        {
            _requested = false;
            if (TopLevel.GetTopLevel(this) is null) { _last = null; return; }
            if (IsEffectivelyVisible)
            {
                var dt = _last is { } l ? Math.Min((now - l).TotalSeconds, 0.1) : 0;
                _angle = (_angle + dt * 300) % 360;
                InvalidateVisual();
            }
            _last = now;
            Request();
        });
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var th = Thickness;
        var r = size / 2 - th / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);

        context.DrawEllipse(null, new Pen(new SolidColorBrush(AccentThemes.WithAlpha(Accent, 0x22)), th), c, r, r);

        // Tail drawn as a few segments of increasing opacity.
        const int segments = 12;
        const double sweep = 270;
        for (var i = 0; i < segments; i++)
        {
            var from = _angle + sweep * i / segments;
            var to = _angle + sweep * (i + 1) / segments + 0.5;
            var alpha = (byte)(255 * Math.Pow((i + 1) / (double)segments, 1.6));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Polar(c, r, from), false);
                ctx.ArcTo(Polar(c, r, to), new Size(r, r), 0, false, SweepDirection.Clockwise);
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(AccentThemes.WithAlpha(Accent, alpha)), th,
                lineCap: i == segments - 1 ? PenLineCap.Round : PenLineCap.Flat), g);
        }
    }

    private static Point Polar(Point c, double r, double deg)
    {
        var rad = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}
