using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// The focus timer as a 3D dial: a ring of blocks lying on a disc, seen from above at an angle. Lit blocks are the
/// time that is left; every passing step drops one block into the disc. The remaining blocks shine brighter the
/// closer they are to the end of the arc, so the "now" edge is easy to find.
/// </summary>
public sealed class DialRing3D : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<DialRing3D, double>(nameof(Fraction), 1);
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<DialRing3D, Color>(nameof(Color), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<Color> Color2Property =
        AvaloniaProperty.Register<DialRing3D, Color>(nameof(Color2), Color.Parse("#FF6A3D"));
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<DialRing3D, bool>(nameof(IsActive));

    private const int Blocks = 48;
    private const double Radius = 5;
    private double _shown = -1;
    private bool _animating;

    static DialRing3D() => AffectsRender<DialRing3D>(ColorProperty, Color2Property, IsActiveProperty);

    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public Color Color2 { get => GetValue(Color2Property); set => SetValue(Color2Property, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != FractionProperty) return;
        if (_shown < 0) { _shown = Math.Clamp(Fraction, 0, 1); InvalidateVisual(); return; }
        if (_animating || TopLevel.GetTopLevel(this) is not { } top) { InvalidateVisual(); return; }
        _animating = true;
        TimeSpan? last = null;
        void Frame(TimeSpan now)
        {
            var dt = last is { } l ? Math.Min((now - l).TotalSeconds, 0.05) : 1 / 60.0;
            last = now;
            var target = Math.Clamp(Fraction, 0, 1);
            var step = dt * (Math.Abs(target - _shown) > 0.1 ? 1.6 : 0.08); // resets sweep, ticks glide
            _shown = Math.Abs(target - _shown) <= step ? target : _shown + Math.Sign(target - _shown) * step;
            InvalidateVisual();
            if (Math.Abs(_shown - target) < 1e-5) { _animating = false; return; }
            top.RequestAnimationFrame(Frame);
        }
        top.RequestAnimationFrame(Frame);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 30 || h < 30) return;

        var cam = new Camera3D(new V3(0, 0.3, 0), yawDegrees: 0, pitchDegrees: 52, distance: 22, focal: 1.6);
        cam.Fit(new V3(-Radius - 0.9, 0, -Radius - 0.9), new V3(Radius + 0.9, 1.4, Radius + 0.9), new Rect(2, 2, w - 4, h - 4));

        // The disc: a dark plate with a soft accent pool in the middle.
        var disc = Ring(cam, Radius + 0.8, 0);
        context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x13)), new Pen(new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)), 0.7), disc);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Shade.Alpha(Color, 0.25)), 0.8), Ring(cam, Radius - 0.85, 0));
        var c = cam.Project(new V3(0, 0, 0));
        var edge = cam.Project(new V3(Radius, 0, 0));
        Shade.Glow(context, c, (edge.X - c.X) * 0.9, (edge.X - c.X) * 0.5, Shade.Alpha(Color, IsActive ? 0.22 : 0.12));

        var left = (_shown < 0 ? Math.Clamp(Fraction, 0, 1) : _shown) * Blocks;
        var boxes = new List<(double Depth, V3[] Corners, Color Color, double Emissive)>(Blocks);
        for (var i = 0; i < Blocks; i++)
        {
            // Clockwise from twelve o'clock, as on a clock face (12 = far side of the disc).
            var angle = Math.PI - i * 2 * Math.PI / Blocks;
            var pos = new V3(Math.Sin(angle) * Radius, 0, Math.Cos(angle) * Radius);
            var on = Math.Clamp(left - i, 0, 1);
            var height = 0.18 + 1.0 * Ease.OutCubic(on);
            var head = on > 0 && left - i < 3 ? 1 - (left - i) / 3 : 0; // the last blocks before the gap glow
            var lit = Shade.Mix(Color2, Color, i / (double)Blocks);
            var color = Shade.Mix(Color.FromRgb(0x26, 0x26, 0x31), lit, on);
            var corners = Box3D.Turned(pos, 0.42, 0.9, height, angle);
            boxes.Add((cam.Depth(pos + new V3(0, height / 2, 0)), corners, color, 0.08 * on + 0.25 * head));
        }
        foreach (var b in boxes.OrderByDescending(b => b.Depth))
            Box3D.Draw(context, cam, b.Corners, b.Color, 1, b.Emissive, bevel: 0.7);
    }

    private static StreamGeometry Ring(Camera3D cam, double r, double y)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        for (var i = 0; i <= 64; i++)
        {
            var a = i * 2 * Math.PI / 64;
            var p = cam.Project(new V3(Math.Sin(a) * r, y, Math.Cos(a) * r));
            if (i == 0) ctx.BeginFigure(p, true); else ctx.LineTo(p);
        }
        ctx.EndFigure(true);
        return g;
    }
}
