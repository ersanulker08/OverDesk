using Avalonia;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>How one matte surface reads under the studio: lit side, mid tone, shadow side, core shadow, rim light and floor bounce.</summary>
internal readonly record struct Mat(Color Lit, Color Mid, Color Shadow, Color Deep, Color Rim, Color Bounce);

/// <summary>
/// The Lumen style: a dark photo studio. Forms are plain matte pillars and orbs, sculpted only by light: a warm key
/// from one side, a colored rim from behind, bounce light off the floor, soft cast shadows, light pools and spot cones.
/// Shadows are never just darker — they fall toward a cool night blue, so warm and cool stay in one harmony.
/// <c>lx</c> everywhere is where the key light comes from, -1 (far left) to 1 (far right); shadows fall the other way.
/// </summary>
internal static class Studio
{
    public static readonly Color WarmLight = Color.FromRgb(255, 236, 208);
    private static readonly Color Night = Color.FromRgb(20, 24, 58);

    public static Mat Material(Color albedo, Color rim, Color bounce) => new(
        Shade.Mix(Shade.Scale(albedo, 1.6), WarmLight, 0.3),
        albedo,
        Shade.Mix(Shade.Scale(albedo, 0.42), Night, 0.38),
        Shade.Mix(Shade.Scale(albedo, 0.17), Night, 0.5),
        Shade.Mix(rim, Colors.White, 0.3),
        bounce);

    /// <summary>The same material while it gives off light: the surface takes on the rim color from within.</summary>
    private static Mat Emitting(Mat m, double e) => new(
        Shade.Mix(m.Lit, Shade.Mix(m.Rim, Colors.White, 0.5), e * 0.55),
        Shade.Mix(m.Mid, m.Rim, e * 0.6),
        Shade.Mix(m.Shadow, m.Rim, e * 0.5),
        Shade.Mix(m.Deep, m.Rim, e * 0.3),
        m.Rim, m.Bounce);

    // ---- light on the floor

    /// <summary>A pool of light on the floor: a wide soft skirt and a brighter core.</summary>
    public static void Pool(DrawingContext g, Point c, double rx, double ry, Color color, double strength)
    {
        if (strength < 0.01) return;
        Shade.Glow(g, c, rx, ry, Shade.Alpha(color, 0.26 * strength));
        Shade.Glow(g, c, rx * 0.5, ry * 0.5, Shade.Alpha(Shade.Mix(color, Colors.White, 0.35), 0.24 * strength));
    }

    /// <summary>A spot cone from <paramref name="apex"/> to the floor: five nested trapezoids give it soft edges, brightest near the lamp.</summary>
    public static void Cone(DrawingContext g, Point apex, Point target, double halfWidth, Color color, double strength)
    {
        if (strength < 0.01) return;
        for (var i = 0; i < 5; i++)
        {
            var hw = halfWidth * (1 - i * 0.17);
            var poly = Shade.Polygon(new Point(apex.X - 2 - i, apex.Y), new Point(apex.X + 2 + i, apex.Y),
                new Point(target.X + hw, target.Y), new Point(target.X - hw, target.Y));
            g.DrawGeometry(Shade.Linear(apex, target, Shade.Alpha(color, 0.10 * strength), Shade.Alpha(color, 0.025 * strength)), null, poly);
        }
        Pool(g, target, halfWidth * 1.15, halfWidth * 0.3, color, strength);
    }

    /// <summary>The shadow of something standing at <paramref name="foot"/>: two layers (penumbra and core) fading along the way.</summary>
    public static void CastShadow(DrawingContext g, Point foot, double rx, double ry, double height, double lx, double alpha)
    {
        if (height < 1 || alpha < 0.02) return;
        var dir = lx > 0 ? -1 : 1;
        var len = height * (0.3 + 0.75 * Math.Abs(lx));
        var dx = dir * len;
        var dy = len * 0.14;
        var far = new Point(foot.X + dx, foot.Y + dy);
        var shadow = Color.FromRgb(4, 4, 12);
        foreach (var (grow, a) in new[] { (1.55, 0.2), (1.0, 0.34) })
        {
            var poly = Shade.Polygon(new Point(foot.X - rx * grow, foot.Y), new Point(foot.X + rx * grow, foot.Y),
                new Point(far.X + rx * grow * 0.85, far.Y + ry * 0.5), new Point(far.X - rx * grow * 0.85, far.Y - ry * 0.3));
            g.DrawGeometry(Shade.Linear(foot, far, Shade.Alpha(shadow, a * alpha), Shade.Alpha(shadow, 0)), null, poly);
        }
        Shade.Glow(g, foot, rx * 1.5, Math.Max(ry * 1.3, 2), Shade.Alpha(Colors.Black, 0.55 * alpha));
    }

    // ---- forms

    /// <summary>A matte cylinder standing at <paramref name="foot"/>. Shaded across its width like a real cylinder.</summary>
    public static void Pillar(DrawingContext g, Point foot, double rx, double ry, double height, Mat m, double lx,
        double emit = 0, double alpha = 1, bool reflect = true)
    {
        if (height < 1 || rx < 0.6 || alpha < 0.02) return;
        var flip = lx > 0;
        var top = foot.Y - height;
        if (emit > 0) m = Emitting(m, emit);
        Color A(Color c) => Shade.Alpha(c, alpha);

        // Reflection in the glossy floor: the body's mid tone, fading quickly.
        if (reflect)
        {
            var depth = Math.Min(height * 0.42, rx * 7);
            g.DrawRectangle(Shade.Linear(foot, new Point(foot.X, foot.Y + depth), A(Shade.Alpha(Shade.Mix(m.Mid, m.Lit, 0.4), 0.26)), A(Shade.Alpha(m.Mid, 0))),
                null, new Rect(foot.X - rx, foot.Y, rx * 2, depth));
        }

        var body = new StreamGeometry();
        using (var c = body.Open())
        {
            c.BeginFigure(new Point(foot.X - rx, top), true);
            c.LineTo(new Point(foot.X - rx, foot.Y));
            c.ArcTo(new Point(foot.X + rx, foot.Y), new Size(rx, ry), 0, false, SweepDirection.CounterClockwise);
            c.LineTo(new Point(foot.X + rx, top));
            c.EndFigure(true);
        }

        // Across the width: lit shoulder, soft falloff, terminator, core shadow, then the bounce and the colored rim light on the far edge.
        var stops = new (double At, Color C)[]
        {
            (0.00, Shade.Mix(m.Mid, m.Lit, 0.45)),
            (0.20, m.Lit),
            (0.42, Shade.Mix(m.Lit, m.Mid, 0.55)),
            (0.66, Shade.Mix(m.Mid, m.Shadow, 0.45)),
            (0.84, m.Shadow),
            (0.91, Shade.Mix(m.Deep, m.Bounce, 0.3)),
            (0.97, Shade.Mix(m.Deep, m.Rim, 0.6)),
            (1.00, Shade.Mix(m.Shadow, m.Bounce, 0.4)),
        };
        var across = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative) };
        foreach (var (at, color) in flip ? stops.Reverse() : stops) across.GradientStops.Add(new GradientStop(A(color), flip ? 1 - at : at));
        g.DrawGeometry(across, null, body);

        // Occlusion toward the floor, light falling off toward the top.
        g.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.62),
                new GradientStop(Color.FromArgb((byte)(120 * alpha), 2, 2, 10), 1),
            },
        }, null, body);

        // Specular streak where the surface turns to face the lamp.
        if (rx >= 2.5)
        {
            var sx = foot.X + (flip ? rx * 0.36 : -rx * 0.5);
            var sw = Math.Max(1, rx * 0.15);
            g.DrawRectangle(Shade.Linear(new Point(sx, top + ry), new Point(sx, top + height * 0.75),
                Shade.Alpha(Colors.White, 0.42 * alpha), Shade.Alpha(Colors.White, 0)), null, new Rect(sx, top + ry, sw, height * 0.75 - ry), sw / 2, sw / 2);
        }

        // The lid faces the sky, so it is the brightest surface of all.
        var lid = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(flip ? 0.62 : 0.38, 0.4, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(A(Shade.Mix(m.Lit, Colors.White, 0.3)), 0),
                new GradientStop(A(m.Lit), 0.65),
                new GradientStop(A(Shade.Mix(m.Lit, m.Mid, 0.7)), 1),
            },
        };
        g.DrawEllipse(lid, new Pen(new SolidColorBrush(Shade.Alpha(Shade.Mix(m.Lit, Colors.White, 0.4), 0.45 * alpha)), 0.8), new Point(foot.X, top), rx, ry);
        if (emit > 0.05) Shade.Glow(g, new Point(foot.X, top), rx * 2.2, ry * 2.4, Shade.Alpha(m.Rim, 0.42 * emit * alpha));
    }

    /// <summary>A matte sphere centered at <paramref name="c"/>; <paramref name="floorY"/> is the floor beneath it, so a lifted orb throws a softer, wider shadow.</summary>
    public static void Orb(DrawingContext g, Point c, double r, double floorY, Mat m, double lx, double emit = 0, double alpha = 1)
    {
        if (r < 0.8 || alpha < 0.02) return;
        var dir = lx > 0 ? 1 : -1;
        var lift = Math.Clamp((floorY - c.Y - r) / Math.Max(r * 3, 1), 0, 1);
        if (emit > 0) m = Emitting(m, emit);
        Color A(Color col) => Shade.Alpha(col, alpha);

        // Shadow on the floor: wide and soft when high, tight and dark when resting.
        var shadowAt = new Point(c.X - dir * r * (0.45 + 0.9 * Math.Abs(lx)) * (1 + lift), floorY);
        Shade.Glow(g, shadowAt, r * (1.5 + 0.7 * lift), Math.Max(r * (0.42 + 0.1 * lift), 2), Shade.Alpha(Color.FromRgb(4, 4, 12), (0.5 - 0.22 * lift) * alpha));
        Shade.Glow(g, new Point(c.X, floorY), r * 0.95, Math.Max(r * 0.26, 1.5), Shade.Alpha(Colors.Black, 0.6 * (1 - lift) * alpha));
        if (emit > 0.05) Shade.Glow(g, new Point(c.X, floorY), r * 3.4, r * 0.95, Shade.Alpha(m.Rim, 0.3 * emit * alpha));
        if (emit > 0.05) Shade.Glow(g, c, r * 2.8, r * 2.8, Shade.Alpha(m.Rim, 0.3 * emit * alpha));

        var body = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5 + dir * 0.18, 0.3, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(A(Shade.Mix(m.Lit, Colors.White, 0.3)), 0),
                new GradientStop(A(m.Lit), 0.3),
                new GradientStop(A(m.Mid), 0.55),
                new GradientStop(A(m.Shadow), 0.8),
                new GradientStop(A(m.Deep), 1),
            },
        };
        g.DrawEllipse(body, null, c, r, r);

        // Bounce from the floor fills the underside on the shadow side; it is the floor's color, not gray.
        g.DrawEllipse(new RadialGradientBrush
        {
            Center = new RelativePoint(0.5 - dir * 0.2, 0.97, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5 - dir * 0.2, 0.97, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.6, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Shade.Alpha(m.Bounce, 0.5 * alpha), 0), new GradientStop(Shade.Alpha(m.Bounce, 0), 1) },
        }, null, c, r, r);

        // Rim light from behind on the shadow edge.
        g.DrawEllipse(null, new Pen(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5 - dir * 0.45, 0.9, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5 + dir * 0.1, 0.4, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Shade.Alpha(m.Rim, 0.75 * alpha), 0), new GradientStop(Shade.Alpha(m.Rim, 0), 1) },
        }, 1.0), c, r - 0.4, r - 0.4);

        // Highlight: a soft sheen and a small hard glint.
        Shade.Glow(g, new Point(c.X + dir * r * 0.4, c.Y - r * 0.42), r * 0.34, r * 0.24, Shade.Alpha(Colors.White, 0.42 * alpha));
        g.DrawEllipse(new SolidColorBrush(Shade.Alpha(Colors.White, 0.85 * alpha)), null, new Point(c.X + dir * r * 0.36, c.Y - r * 0.45), r * 0.09, r * 0.06);
        if (emit > 0.05) Shade.Glow(g, c, r * 0.75, r * 0.75, Shade.Alpha(Colors.White, 0.32 * emit * alpha));
    }

    // ---- air

    /// <summary>Dust hanging in a lamp's beam, twinkling as it drifts.</summary>
    public static void Dust(DrawingContext g, Point apex, Point target, double halfWidth, Color color, double t, double strength, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var u = (Crystal3D.Hash(i, 101) + t * (0.012 + 0.02 * Crystal3D.Hash(i, 102))) % 1.0;
            var side = (Crystal3D.Hash(i, 103) * 2 - 1) * (0.2 + 0.8 * u) + 0.06 * Math.Sin(t * 0.4 + i);
            var p = new Point(apex.X + (target.X - apex.X) * u + side * halfWidth * u, apex.Y + (target.Y - apex.Y) * u);
            var twinkle = 0.45 + 0.55 * Math.Sin(t * (0.8 + Crystal3D.Hash(i, 104)) + i * 2.1);
            var size = 1.1 + 1.5 * Crystal3D.Hash(i, 105);
            Shade.Glow(g, p, size * 1.8, size * 1.8, Shade.Alpha(color, 0.5 * twinkle * strength * (0.4 + 0.6 * (1 - u))));
        }
    }
}
