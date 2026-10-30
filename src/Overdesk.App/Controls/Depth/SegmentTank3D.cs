using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// A level gauge built from a row of 3D cells on a rail: lit cells stand tall and glow, empty ones lie low as dark
/// glass. Changes run cell by cell, each one popping up or dropping down in turn. For disk, swap and battery.
/// </summary>
public sealed class SegmentTank3D : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<SegmentTank3D, double>(nameof(Fraction));
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<SegmentTank3D, Color>(nameof(Color), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<Color> Color2Property =
        AvaloniaProperty.Register<SegmentTank3D, Color>(nameof(Color2), Color.Parse("#FF6A3D"));

    public int Cells { get; set; } = 22;

    private double _shown = -1;   // the level on screen, chasing Fraction
    private bool _animating;

    static SegmentTank3D() => AffectsRender<SegmentTank3D>(ColorProperty, Color2Property);

    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public Color Color2 { get => GetValue(Color2Property); set => SetValue(Color2Property, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != FractionProperty) return;
        if (_shown < 0) _shown = 0; // the first value fills up from empty
        if (_animating || TopLevel.GetTopLevel(this) is not { } top) { InvalidateVisual(); return; }
        _animating = true;
        TimeSpan? last = null;
        void Frame(TimeSpan now)
        {
            var dt = last is { } l ? Math.Min((now - l).TotalSeconds, 0.05) : 1 / 60.0;
            last = now;
            var target = Math.Clamp(Fraction, 0, 1);
            var step = dt * 1.4; // a full sweep in about 0.7 s
            _shown = Math.Abs(target - _shown) <= step ? target : _shown + Math.Sign(target - _shown) * step;
            InvalidateVisual();
            if (Math.Abs(_shown - target) < 1e-4) { _animating = false; return; }
            top.RequestAnimationFrame(Frame);
        }
        top.RequestAnimationFrame(Frame);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 20 || h < 8) return;
        var n = Cells;
        const double tall = 0.95, low = 0.16, depth = 1.0, width = 0.72;
        var cam = new Camera3D(new V3(n / 2.0, 0.4, 0), yawDegrees: 12, pitchDegrees: 34, distance: n * 1.7, focal: 1.5);
        cam.Fit(new V3(-0.2, -0.05, -depth / 2 - 0.25), new V3(n + 0.2, tall, depth / 2 + 0.25), new Rect(1, 1, w - 2, h - 2));

        // The rail the cells sit in.
        var rail = Shade.Polygon(cam.Project(new V3(-0.15, 0, -depth / 2 - 0.2)), cam.Project(new V3(n + 0.15, 0, -depth / 2 - 0.2)),
                                 cam.Project(new V3(n + 0.15, 0, depth / 2 + 0.2)), cam.Project(new V3(-0.15, 0, depth / 2 + 0.2)));
        context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x11)), new Pen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), 0.6), rail);

        var level = Math.Max(_shown, 0) * n;
        for (var i = 0; i < n; i++) // left to right: with a small yaw the left cells are farther away
        {
            // Each cell rises over its own slice of the level, so a change ripples along the row.
            var on = Math.Clamp(level - i, 0, 1);
            var e = Ease.OutBack(on);
            var height = low + (tall - low) * e;
            var x0 = i + (1 - width) / 2;
            var lit = Shade.Mix(Color2, Color, i / (double)Math.Max(n - 1, 1));
            var color = Shade.Mix(Color.FromRgb(0x2A, 0x2A, 0x36), lit, on);
            Box3D.Draw(context, cam, Box3D.Aligned(new V3(x0, 0, -depth / 2), new V3(x0 + width, height, depth / 2)),
                color, 1, 0.22 * on, bevel: 0.4 + 0.6 * on);
        }

        // Light spill at the head of the level.
        if (level > 0.05)
            Shade.Glow(context, cam.Project(new V3(Math.Min(level, n) - 0.2, tall, 0)), 14, 7, Shade.Alpha(Shade.Mix(Color, Colors.White, 0.3), 0.5));
    }
}
