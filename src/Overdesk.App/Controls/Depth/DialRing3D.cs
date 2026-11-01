using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// The focus timer as a summoning circle: a ring of crystals standing on a rune disc, seen from above at an angle.
/// Glowing crystals are the time that is left; every passing step one of them sinks back into the disc. The last
/// ones before the gap shine brightest, so the "now" edge is easy to find.
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ArtStyles.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ArtStyles.Changed -= InvalidateVisual;
    }

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
        if (ArtStyles.Current == ArtStyle.Classic) { RenderClassic(context, w, h); return; }
        if (ArtStyles.IsLumen) { RenderStudio(context, w, h); return; }

        var cam = new Camera3D(new V3(0, 0.3, 0), yawDegrees: 0, pitchDegrees: 52, distance: 22, focal: 1.6);
        cam.Fit(new V3(-Radius - 0.9, 0, -Radius - 0.9), new V3(Radius + 0.9, 1.9, Radius + 0.9), new Rect(2, 2, w - 4, h - 4));

        // The disc: a dark plate with rune rings, glyph ticks and a pool of light in the middle.
        var origin = new V3(0, 0, 0);
        context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x0C, 0x0B, 0x13)), new Pen(new SolidColorBrush(Shade.Alpha(Color, 0.35)), 0.8),
            Crystal3D.Ring(cam, origin, Radius + 0.8, 64));
        if (Ink.Rough) Ink.Stroke(context, Crystal3D.Ring(cam, origin, Radius + 0.8, 64), Color, 0.3, 0.8);
        Ink.Stroke(context, Crystal3D.Ring(cam, origin, Radius - 0.9, 64), Color, 0.28, 0.8);
        Ink.Stroke(context, Crystal3D.Ring(cam, origin, Radius - 2.4, 48), Shade.Mix(Color, Colors.White, 0.3), 0.3, 0.7, new DashStyle(new double[] { 1.5, 3 }, 0));
        for (var i = 0; i < 12; i++)
        {
            var (s, co) = Math.SinCos(i * Math.PI / 6);
            Ink.Line(context, cam.Project(new V3(s * (Radius - 1.7), 0, co * (Radius - 1.7))), cam.Project(new V3(s * (Radius - 1.1), 0, co * (Radius - 1.1))), Color, 0.4, 0.8);
        }
        var c = cam.Project(origin);
        var edge = cam.Project(new V3(Radius, 0, 0));
        Shade.Glow(context, c, (edge.X - c.X) * 0.9, (edge.X - c.X) * 0.5, Shade.Alpha(Color, IsActive ? 0.24 : 0.12));

        var left = (_shown < 0 ? Math.Clamp(Fraction, 0, 1) : _shown) * Blocks;
        var shards = new List<(double Depth, V3 Foot, double Angle, double Height, Color Color, double Emissive, double On, double Head)>(Blocks);
        for (var i = 0; i < Blocks; i++)
        {
            // Clockwise from twelve o'clock, as on a clock face (12 = far side of the disc).
            var angle = Math.PI - i * 2 * Math.PI / Blocks;
            var foot = new V3(Math.Sin(angle) * Radius, 0, Math.Cos(angle) * Radius);
            var on = Math.Clamp(left - i, 0, 1);
            var height = 0.28 + 1.15 * Ease.OutCubic(on);
            var head = on > 0 && left - i < 3 ? 1 - (left - i) / 3 : 0; // the last crystals before the gap glow
            var lit = Shade.Mix(Color2, Color, i / (double)Blocks);
            var color = Shade.Mix(Color.FromRgb(0x2A, 0x26, 0x38), lit, on);
            shards.Add((cam.Depth(foot + new V3(0, height / 2, 0)), foot, angle, height, color, 0.08 * on + 0.25 * head, on, head));
        }
        foreach (var b in shards.OrderByDescending(b => b.Depth))
            Crystal3D.Draw(context, cam, b.Foot, 0.27, b.Height, b.Color, 1, b.Emissive, b.On, b.Angle, spark: 0.5 * b.Head);
    }

    // ---- lumen style: a turntable of matte orbs; the ones still counting float up and glow

    private void RenderStudio(DrawingContext context, double w, double h)
    {
        const double lx = -0.6, orb = 0.3, lift = 0.7;
        var cam = new Camera3D(new V3(0, 0.3, 0), yawDegrees: 0, pitchDegrees: 52, distance: 22, focal: 1.6);
        cam.Fit(new V3(-Radius - 0.9, 0, -Radius - 0.9), new V3(Radius + 0.9, 2 * orb + lift + 0.2, Radius + 0.9), new Rect(2, 2, w - 4, h - 4));
        var origin = new V3(0, 0, 0);
        var c = cam.Project(origin);
        var edge = cam.Project(new V3(Radius + 0.8, 0, 0));
        var rpx = edge.X - c.X;

        // The turntable: a polished disc, bright where the lamp hits it and dark toward its rim, with a bevel catching the light.
        var plate = Crystal3D.Ring(cam, origin, Radius + 0.8, 72);
        context.DrawGeometry(new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.42, 0.4, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Shade.Mix(Color.FromRgb(0x2A, 0x28, 0x36), Studio.WarmLight, IsActive ? 0.12 : 0.05), 0),
                new GradientStop(Color.FromRgb(0x17, 0x16, 0x20), 0.7),
                new GradientStop(Color.FromRgb(0x0C, 0x0B, 0x12), 1),
            },
        }, new Pen(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Shade.Alpha(Studio.WarmLight, 0.55), 0), new GradientStop(Shade.Alpha(Color, 0.35), 0.6), new GradientStop(Shade.Alpha(Colors.Black, 0.6), 1) },
        }, 1.6), plate);
        Studio.Pool(context, c, rpx * 0.8, rpx * 0.45, Shade.Mix(Studio.WarmLight, Color, 0.3), IsActive ? 0.9 : 0.5);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Shade.Alpha(Colors.Black, 0.45)), 1.2), Crystal3D.Ring(cam, origin, Radius - 0.9, 64));

        var left = (_shown < 0 ? Math.Clamp(Fraction, 0, 1) : _shown) * Blocks;
        var rim = Shade.Mix(Color, Colors.White, 0.1);
        var off = Color.FromRgb(0x3A, 0x38, 0x46);
        var orbs = new List<(double Depth, Point Centre, Point Floor, double R, Mat Mat, double Emit)>(Blocks);
        for (var i = 0; i < Blocks; i++)
        {
            var angle = Math.PI - i * 2 * Math.PI / Blocks;
            var x = Math.Sin(angle) * Radius;
            var z = Math.Cos(angle) * Radius;
            var on = Math.Clamp(left - i, 0, 1);
            var head = on > 0 && left - i < 3 ? 1 - (left - i) / 3 : 0;
            var y = orb + lift * Ease.OutCubic(on);
            var centre = cam.Project(new V3(x, y, z));
            var rr = Math.Abs(cam.Project(new V3(x + orb, y, z)).X - centre.X);
            var lit = Shade.Mix(Color2, Color, i / (double)Blocks);
            var mat = Studio.Material(Shade.Mix(off, lit, on * 0.55), Shade.Mix(rim, lit, 0.4), lit);
            orbs.Add((cam.Depth(new V3(x, y, z)), centre, cam.Project(new V3(x, 0, z)), rr, mat, 0.55 * on + 0.45 * head));
        }
        foreach (var o in orbs.OrderByDescending(o => o.Depth))
            Studio.Orb(context, o.Centre, o.R, o.Floor.Y, o.Mat, lx, o.Emit);
    }

    // ---- classic style

    private void RenderClassic(DrawingContext context, double w, double h)
    {

        var cam = new Camera3D(new V3(0, 0.3, 0), yawDegrees: 0, pitchDegrees: 52, distance: 22, focal: 1.6);
        cam.Fit(new V3(-Radius - 0.9, 0, -Radius - 0.9), new V3(Radius + 0.9, 1.4, Radius + 0.9), new Rect(2, 2, w - 4, h - 4));

        // The disc: a dark plate with a soft accent pool in the middle.
        var disc = ClassicRing(cam, Radius + 0.8, 0);
        context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x13)), new Pen(new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)), 0.7), disc);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Shade.Alpha(Color, 0.25)), 0.8), ClassicRing(cam, Radius - 0.85, 0));
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

    private static StreamGeometry ClassicRing(Camera3D cam, double r, double y)
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
