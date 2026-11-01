using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Overdesk.App.Controls.Depth;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls;

/// <summary>
/// An arcane summoning circle turning slowly on the floor, a column of light rising from its heart, crystals drifting
/// around it and motes climbing the air. Animates on the compositor's frame clock (capped at ~20 fps) only while visible.
/// </summary>
public sealed class SigilBackground : Control
{
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<SigilBackground, Color>(nameof(Accent), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<Color> Accent2Property =
        AvaloniaProperty.Register<SigilBackground, Color>(nameof(Accent2), Color.Parse("#FF6A3D"));
    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<SigilBackground, bool>(nameof(IsAnimated), true);
    public static readonly StyledProperty<double> IntensityProperty =
        AvaloniaProperty.Register<SigilBackground, double>(nameof(Intensity), 1.0);

    private const double FrameInterval = 1.0 / 20;
    private static readonly double StartPhase = Random.Shared.NextDouble() * 100;

    private double _time = StartPhase;
    private TimeSpan? _lastFrame;
    private double _sinceDraw;
    private bool _frameRequested;

    static SigilBackground()
    {
        AffectsRender<SigilBackground>(AccentProperty, Accent2Property, IntensityProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<SigilBackground>(false);
        // The circle is larger than the page; never let it spill over neighbours.
        ClipToBoundsProperty.OverrideDefaultValue<SigilBackground>(true);
    }

    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Color Accent2 { get => GetValue(Accent2Property); set => SetValue(Accent2Property, value); }
    public bool IsAnimated { get => GetValue(IsAnimatedProperty); set => SetValue(IsAnimatedProperty, value); }

    /// <summary>0..1: overall brightness of the circle and its light.</summary>
    public double Intensity { get => GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ArtStyles.Changed += InvalidateVisual;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ArtStyles.Changed -= InvalidateVisual;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsAnimatedProperty || change.Property == IsVisibleProperty) RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested || !IsAnimated || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameRequested = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        _frameRequested = false;
        if (!IsAttachedToVisualTree() || !IsAnimated)
        {
            _lastFrame = null;
            return;
        }

        var dt = _lastFrame is { } last ? Math.Min((now - last).TotalSeconds, 0.1) : 0;
        _lastFrame = now;

        if (IsEffectivelyVisible)
        {
            _time += dt;
            _sinceDraw += dt;
            if (_sinceDraw >= FrameInterval)
            {
                _sinceDraw = 0;
                InvalidateVisual();
            }
        }
        RequestFrame();
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;

    private const double Radius = 8;
    private const int Shards = 7;
    private const int Motes = 26;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 2 || h < 2) return;
        if (ArtStyles.Current == ArtStyle.Classic) { RenderClassic(context, w, h); return; }
        if (ArtStyles.IsLumen) { RenderStudio(context, w, h); return; }

        var t = _time;
        var k = Math.Clamp(Intensity, 0, 1);
        var accent = Accent;
        var accent2 = Accent2;
        var pale = Shade.Mix(accent, Colors.White, 0.35);

        // The circle sits low on the right, big enough that its near arc bleeds off the page.
        var side = Math.Max(w * 0.9, h * 1.15);
        var cam = new Camera3D(new V3(0, 0, 0), 0, 58, 40, 1.6);
        cam.Fit(new V3(-Radius, 0, -Radius), new V3(Radius, 7, Radius), new Rect(w * 0.98 - side, h * 0.92 - side * 0.78, side, side * 0.78));
        var origin = new V3(0, 0, 0);
        var centre = cam.Project(origin);
        var ex = Math.Abs(cam.Project(new V3(Radius, 0, 0)).X - centre.X);
        var ey = Math.Abs(cam.Project(new V3(0, 0, Radius)).Y - centre.Y);
        var pulse = 0.85 + 0.15 * Math.Sin(t * 1.3);

        // Pool of light across the floor.
        Shade.Glow(context, centre, ex * 1.25, ey * 1.25, Shade.Alpha(accent, 0.20 * k * pulse));

        if (Ink.Rough) Smoke(context, centre, ex, ey, t, k, 0);

        // Rings: the outer one still, the others turning against each other.
        Ink.Stroke(context, Crystal3D.Ring(cam, origin, Radius, 96), accent, 0.55 * k, 1.4);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Shade.Alpha(accent, 0.28 * k)), 5), Crystal3D.Ring(cam, origin, Radius, 96));
        Ink.Stroke(context, Crystal3D.Ring(cam, origin, Radius - 0.8, 80, turn: t * 0.05), pale, 0.35 * k, 0.9, new DashStyle(new double[] { 1.5, 3 }, 0));
        Ink.Stroke(context, Crystal3D.Ring(cam, origin, 6.0, 72), accent2, 0.4 * k, 0.9);
        Ink.Stroke(context, Crystal3D.Ring(cam, origin, 3.6, 56), accent, 0.35 * k, 0.9);
        Ink.Stroke(context, Crystal3D.Ring(cam, origin, 2.2, 40, turn: -t * 0.12), pale, 0.3 * k, 0.8, new DashStyle(new double[] { 1, 2.5 }, 0));

        // Glyph ticks around the rim, slowly turning.
        var spin = t * 0.04;
        for (var i = 0; i < 60; i++)
        {
            var (sn, cs) = Math.SinCos(spin + i * Math.PI / 30);
            var inner = i % 5 == 0 ? Radius - 1.6 : Radius - 1.2;
            Ink.Line(context, cam.Project(new V3(sn * inner, 0, cs * inner)), cam.Project(new V3(sn * (Radius - 0.9), 0, cs * (Radius - 0.9))), accent, 0.45 * k, 0.9);
        }

        // Two interlocked triangles, turning the other way, with a node of light at each point.
        var turn = -t * 0.09;
        for (var tri = 0; tri < 2; tri++)
            for (var i = 0; i < 3; i++)
            {
                var a1 = turn + tri * Math.PI / 3 + i * Math.PI * 2 / 3;
                var a2 = a1 + Math.PI * 2 / 3;
                Ink.Line(context, cam.Project(new V3(Math.Sin(a1) * 6.0, 0, Math.Cos(a1) * 6.0)), cam.Project(new V3(Math.Sin(a2) * 6.0, 0, Math.Cos(a2) * 6.0)), accent2, 0.5 * k, 1.0);
            }
        for (var i = 0; i < 6; i++)
        {
            var a = turn + i * Math.PI / 3;
            Shade.Glow(context, cam.Project(new V3(Math.Sin(a) * 6.0, 0, Math.Cos(a) * 6.0)), 7, 4, Shade.Alpha(pale, 0.75 * k));
        }

        // The column of light standing in the middle: wide at its foot, thin and fading as it climbs.
        var foot = cam.Project(new V3(0, 0, 0));
        var peak = cam.Project(new V3(0, 7, 0));
        var half = ex * 0.11;
        context.DrawGeometry(Shade.Linear(foot, peak, Shade.Alpha(pale, 0.38 * k * pulse), Shade.Alpha(accent, 0)), null,
            Shade.Polygon(new Point(foot.X - half, foot.Y), new Point(foot.X + half, foot.Y), new Point(peak.X + half * 0.3, peak.Y), new Point(peak.X - half * 0.3, peak.Y)));
        Shade.Glow(context, foot, half * 1.6, half * 0.7, Shade.Alpha(Colors.White, 0.45 * k * pulse));

        // Smoke drifting across the circle softens it in the organic style.
        if (Ink.Rough) Smoke(context, centre, ex, ey, t, k, 1);

        // Crystals hanging in the air around the circle, bobbing and turning.
        var floating = new List<(double Depth, V3 Foot, double Size, double Spin, Color Color)>(Shards);
        for (var i = 0; i < Shards; i++)
        {
            var phase = i * Math.PI * 2 / Shards + 0.6;
            var orbit = 4.4 + 2.2 * Crystal3D.Hash(i, 2);
            var a = phase + t * (0.06 + 0.03 * Crystal3D.Hash(i, 3));
            var y = 1.2 + 1.8 * Crystal3D.Hash(i, 4) + 0.35 * Math.Sin(t * 0.7 + i * 1.9);
            var pos = new V3(Math.Sin(a) * orbit, y, Math.Cos(a) * orbit);
            floating.Add((cam.Depth(pos), pos, 0.9 + 0.7 * Crystal3D.Hash(i, 5), t * 0.5 + i, Shade.Mix(accent2, accent, Crystal3D.Hash(i, 6))));
        }
        foreach (var f in floating.OrderByDescending(f => f.Depth))
        {
            // A small shadow-glow on the floor under each one.
            Shade.Glow(context, cam.Project(new V3(f.Foot.X, 0, f.Foot.Z)), 14, 6, Shade.Alpha(f.Color, 0.16 * k));
            Crystal3D.Draw(context, cam, f.Foot, 0.22 * f.Size, 1.0 * f.Size, f.Color, 0.5 + 0.5 * k, 0.12, 0.7, f.Spin, spark: 0.45);
        }

        // Motes of light climbing the air.
        for (var i = 0; i < Motes; i++)
        {
            var u = (t * (0.025 + 0.03 * Crystal3D.Hash(i, 7)) + Crystal3D.Hash(i, 8)) % 1.0;
            var a = Crystal3D.Hash(i, 9) * Math.PI * 2 + t * 0.1;
            var r = 0.8 + Radius * 0.9 * Crystal3D.Hash(i, 10);
            var p = cam.Project(new V3(Math.Sin(a) * r, u * 7, Math.Cos(a) * r));
            var life = Math.Sin(Math.PI * u);
            Shade.Glow(context, p, 2.4 + 1.6 * Crystal3D.Hash(i, 11), 2.4 + 1.6 * Crystal3D.Hash(i, 11), Shade.Alpha(pale, 0.8 * life * k));
        }
    }

    /// <summary>Slow dark wisps drifting over the floor (layer 0 under the lines, layer 1 over them).</summary>
    private static void Smoke(DrawingContext context, Point centre, double ex, double ey, double t, double k, int layer)
    {
        var dark = Color.FromRgb(0x06, 0x04, 0x08);
        for (var i = 0; i < 6; i++)
        {
            var seed = i + layer * 10;
            var a = Crystal3D.Hash(seed, 1) * Math.PI * 2 + t * (0.03 + 0.04 * Crystal3D.Hash(seed, 2)) * (i % 2 == 0 ? 1 : -1);
            var d = 0.25 + 0.6 * Crystal3D.Hash(seed, 3);
            var at = new Point(centre.X + Math.Sin(a) * ex * d, centre.Y + Math.Cos(a) * ey * d);
            var size = 0.35 + 0.35 * Crystal3D.Hash(seed, 4);
            Shade.Glow(context, at, ex * size, ey * size * 0.8, Shade.Alpha(dark, (layer == 0 ? 0.5 : 0.38) * k));
        }
    }

    // ---- lumen style: a still life in a dark studio — matte pillars and orbs under two slowly swaying spot lamps

    private void RenderStudio(DrawingContext context, double w, double h)
    {
        var t = _time;
        var k = Math.Clamp(Intensity, 0, 1);
        var key = Shade.Mix(Studio.WarmLight, Accent, 0.18);
        var fill = Shade.Mix(Accent, Colors.White, 0.1);
        var bounce = Accent2;
        var floorY = h * 0.80;

        // Haze along the horizon and a faint sheen on the floor.
        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Shade.Alpha(fill, 0), 0),
                new GradientStop(Shade.Alpha(fill, 0.07 * k), 0.5),
                new GradientStop(Shade.Alpha(fill, 0), 1),
            },
        }, null, new Rect(0, floorY - h * 0.16, w, h * 0.2));
        context.DrawRectangle(Shade.Linear(new Point(0, floorY), new Point(0, h), Shade.Alpha(Accent, 0.08 * k), Shade.Alpha(Colors.Black, 0.25 * k)), null, new Rect(0, floorY, w, h - floorY));

        // Two lamps whose cones sway a little; the cast shadows below follow them.
        var lampKeyX = w * 0.90 + Math.Sin(t * 0.13) * w * 0.05;
        var keyTarget = new Point(w * 0.76 + Math.Sin(t * 0.13) * w * 0.02, floorY + h * 0.05);
        var lampFillX = w * 0.55 + Math.Sin(t * 0.10 + 2) * w * 0.04;
        var fillTarget = new Point(w * 0.64, floorY + h * 0.02);
        var keyApex = new Point(lampKeyX, -h * 0.05);
        var fillApex = new Point(lampFillX, -h * 0.05);
        Studio.Cone(context, keyApex, keyTarget, w * 0.2, key, 0.9 * k);
        Studio.Cone(context, fillApex, fillTarget, w * 0.11, fill, 0.7 * k);
        Studio.Dust(context, keyApex, keyTarget, w * 0.2, key, t, k, 26);
        Studio.Dust(context, fillApex, fillTarget, w * 0.11, fill, t, k, 12);

        var neutral = Color.FromRgb(0x42, 0x40, 0x4E);
        var tinted = Shade.Mix(neutral, Accent, 0.35);
        var rim = Shade.Mix(Accent, Colors.White, 0.1);
        var matNeutral = Studio.Material(neutral, rim, bounce);
        var matTinted = Studio.Material(tinted, rim, bounce);
        double Light(double x) => Math.Clamp((lampKeyX - x) / (w * 0.35), -1, 1);

        // Back to front. Pillar: x, foot y, half width, height, material. Orb: x, foot y, radius, material.
        var pillars = new (double X, double Y, double Rx, double Hgt, Mat M)[]
        {
            (0.88, 0.00, 0.050, 0.40, matNeutral),
            (0.745, 0.015, 0.042, 0.25, matTinted),
            (0.60, 0.035, 0.07, 0.085, matNeutral),
        };
        var orbs = new (double X, double Y, double R, Mat M, double Emit, double Bob)[]
        {
            (0.69, 0.03, 0.036, matNeutral, 0, 0),
            (0.825, 0.045, 0.05, matTinted, 0, 0),
            (0.945, 0.04, 0.03, matNeutral, 0, 0),
            (0.60, 0.035 + 0.003, 0.034, matTinted, 0.0, 0),
        };
        var items = new List<(double Y, Action Draw)>();
        foreach (var p in pillars)
        {
            var foot = new Point(w * p.X, floorY + h * p.Y);
            var rx = w * p.Rx;
            var ry = rx * 0.3;
            var height = h * p.Hgt;
            var lx = Light(foot.X);
            items.Add((foot.Y, () =>
            {
                Studio.CastShadow(context, foot, rx, ry, height, lx, 0.9 * k);
                Studio.Pillar(context, foot, rx, ry, height, p.M, lx, alpha: 0.95 * k);
            }));
        }
        foreach (var o in orbs)
        {
            var floor = new Point(w * o.X, floorY + h * o.Y);
            var r = w * o.R;
            // The small orb on the low pillar rests on its lid.
            var rest = o.X == 0.60 ? h * 0.085 : 0;
            var centre = new Point(floor.X, floor.Y - r - rest);
            var lx = Light(floor.X);
            items.Add((floor.Y + 0.1, () => Studio.Orb(context, centre, r, floor.Y - (rest > 0 ? rest * 0.9 : 0), o.M, lx, o.Emit, 0.95 * k)));
        }

        // Two small lamps bobbing in the air: they light themselves and the floor under them.
        var glowOrbs = new (double X, double Y, double R, Color C, double Phase)[]
        {
            (0.66, 0.52, 0.016, fill, 0.0),
            (0.83, 0.26, 0.022, key, 1.7),
            (0.94, 0.66, 0.014, fill, 3.1),
        };
        foreach (var o in glowOrbs)
        {
            var bob = Math.Sin(t * 0.5 + o.Phase) * h * 0.012;
            var centre = new Point(w * o.X, h * o.Y + bob);
            var r = w * o.R;
            var mat = Studio.Material(Shade.Mix(neutral, o.C, 0.5), o.C, bounce);
            var floor = new Point(centre.X, floorY + h * 0.035);
            var lx = Light(centre.X);
            items.Add((floor.Y + 0.2, () => Studio.Orb(context, centre, r, floor.Y, mat, lx, 0.85, k)));
        }
        foreach (var item in items.OrderBy(i => i.Y)) item.Draw();
    }

    // ---- classic style

    private void RenderClassic(DrawingContext context, double w, double h)
    {

        var t = _time;
        var intensity = Math.Clamp(Intensity, 0, 1);
        var accent = Accent;
        var accent2 = Accent2;

        // Soft accent bloom low on the right, where the ribbon is brightest.
        context.DrawRectangle(new RadialGradientBrush
        {
            Center = new RelativePoint(0.78, 0.92, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.78, 0.92, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(AccentThemes.WithAlpha(accent, (byte)(0x26 * intensity)), 0),
                new GradientStop(AccentThemes.WithAlpha(accent, 0), 1),
            },
        }, null, new Rect(0, 0, w, h));

        const int count = 40;
        const int steps = 32;
        var neonA = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(AccentThemes.WithAlpha(accent, 0), 0),
                new GradientStop(AccentThemes.WithAlpha(accent, (byte)(0xE0 * intensity)), 0.45),
                new GradientStop(AccentThemes.WithAlpha(accent2, (byte)(0xC0 * intensity)), 0.8),
                new GradientStop(AccentThemes.WithAlpha(accent2, 0), 1),
            },
        };

        // One geometry per brightness bucket (plus one for all neon threads): a handful of draw calls instead of one per strand.
        const int buckets = 6;
        var plain = new StreamGeometry[buckets];
        var plainCtx = new StreamGeometryContext[buckets];
        for (var k = 0; k < buckets; k++) { plain[k] = new StreamGeometry(); plainCtx[k] = plain[k].Open(); }
        var neonGeometry = new StreamGeometry();
        var neonCtx = neonGeometry.Open();

        for (var i = 0; i < count; i++)
        {
            var u = i / (double)(count - 1);
            var ctx = IsNeon(i, count) ? neonCtx : plainCtx[Math.Min(buckets - 1, (int)(u * buckets))];
            for (var s = 0; s <= steps; s++)
            {
                var nx = -0.05 + 1.1 * s / steps;
                var center = 0.80 - 0.26 * nx + 0.05 * Math.Sin(nx * 2.6 + t * 0.22);
                var spread = 0.06 + 0.34 * nx * nx;
                var swirl = 0.035 * Math.Sin(nx * 6.0 + u * 4.2 + t * 0.55) * (0.35 + u)
                            + 0.015 * Math.Sin(nx * 13.0 - t * 0.8 + u * 9.0);
                var p = new Point(nx * w, (center + (u - 0.5) * spread + swirl) * h);
                if (s == 0) ctx.BeginFigure(p, false);
                else ctx.LineTo(p);
            }
            ctx.EndFigure(false);
        }

        for (var k = 0; k < buckets; k++)
        {
            plainCtx[k].Dispose();
            // Strands near the top edge of the ribbon catch more light.
            var u = (k + 0.5) / buckets;
            var shade = (byte)(26 + 40 * Math.Pow(u, 1.6));
            var alpha = (byte)((70 + 120 * u) * intensity);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(alpha, shade, shade, (byte)Math.Min(shade + 12, 255))), 1.0), plain[k]);
        }
        neonCtx.Dispose();
        // Faint wide halo under the neon threads, then the threads themselves.
        context.DrawGeometry(null, new Pen(new SolidColorBrush(AccentThemes.WithAlpha(accent, (byte)(0x1C * intensity))), 6), neonGeometry);
        context.DrawGeometry(null, new Pen(neonA, 1.6), neonGeometry);
    }

    private static bool IsNeon(int i, int count)
    {
        var a = (int)(count * 0.28);
        var b = (int)(count * 0.71);
        return i == a || i == a + 2 || i == b || i == count - 4;
    }
}
