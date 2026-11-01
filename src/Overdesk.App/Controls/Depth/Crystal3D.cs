using Avalonia;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// One faceted crystal: a hexagonal prism with a pointed top, lit from inside. Shared by every widget so the whole dock
/// speaks the same arcane language. Only faces turned toward the camera are drawn; a convex shard never overlaps itself.
/// In the organic style the shard is chipped and leaning, outlined in ink, hatched on its dark side and casts a shadow.
/// </summary>
internal static class Crystal3D
{
    private static readonly Color Earth = Color.FromRgb(0x3A, 0x2A, 0x26);
    private static readonly Color InkColor = Color.FromRgb(0x08, 0x05, 0x0A);

    public static double Hash(long id, int salt)
    {
        var x = Math.Sin(id * 12.9898 + salt * 78.233) * 43758.5453;
        return x - Math.Floor(x);
    }

    /// <param name="foot">Center of the base.</param>
    /// <param name="spin">Turn around the vertical axis, radians.</param>
    /// <param name="spark">Brightness of the point's spark (0 = none).</param>
    /// <param name="motes">Drifting light motes around the point.</param>
    public static void Draw(DrawingContext context, Camera3D cam, V3 foot, double r, double h, Color baseColor, double alpha,
        double emissive, double heat, double spin = 0, double spark = 0, bool motes = false, long id = 0)
    {
        if (h < 0.02 || alpha < 0.01) return;
        var rough = Ink.Rough;
        var tip = Math.Min(h, Math.Max(h * 0.32, r * 1.6));
        var body = h - tip;
        if (rough) baseColor = Shade.Mix(baseColor, Earth, 0.12);

        // World corners. The organic shard has uneven sides, uneven shoulders and a point that leans off center.
        var lowW = new V3[6];
        var shoulderW = new V3[6];
        for (var k = 0; k < 6; k++)
        {
            var (s, co) = Math.SinCos(spin + (k * 60 + 30) * Math.PI / 180);
            var rk = rough ? r * (0.78 + 0.42 * Hash(id, 40 + k)) : r;
            var bk = rough ? body * (0.8 + 0.35 * Hash(id, 50 + k)) : body;
            lowW[k] = foot + new V3(s * rk, 0, co * rk);
            shoulderW[k] = foot + new V3(s * rk * (rough ? 0.92 : 1), bk, co * rk * (rough ? 0.92 : 1));
        }
        var apexW = foot + new V3(rough ? (Hash(id, 60) - 0.5) * r * 0.9 : 0, h, rough ? (Hash(id, 61) - 0.5) * r * 0.9 : 0);
        var axisW = foot + new V3(0, h * 0.5, 0);

        var low = new Point[6];
        var shoulder = new Point[6];
        for (var k = 0; k < 6; k++) { low[k] = cam.Project(lowW[k]); shoulder[k] = cam.Project(shoulderW[k]); }
        var apex = cam.Project(apexW);

        if (rough && foot.Y < 0.05) CastShadow(context, cam, low, foot, h, alpha);

        var edge = new Pen(new SolidColorBrush(Color.FromArgb((byte)(120 * alpha), 255, 255, 255)), 0.7);
        // Ink weight follows the shard's size on screen, so tiny dock shards are not buried under their outlines.
        var px = Math.Abs(cam.Project(foot + new V3(r, 0, 0)).X - cam.Project(foot).X);
        var ink = new Pen(new SolidColorBrush(Shade.Alpha(InkColor, 0.85 * alpha)), Math.Clamp(px * 0.14, 0.6, 1.6), lineJoin: PenLineJoin.Round);
        for (var k = 0; k < 6; k++)
        {
            var k2 = (k + 1) % 6;
            if (!Facing(cam, axisW, lowW[k], lowW[k2], shoulderW[k2], shoulderW[k], out var n)) continue;

            var lit = Shade.Lit(baseColor, n, 0.28, emissive);
            var top = new Point((shoulder[k].X + shoulder[k2].X) / 2, (shoulder[k].Y + shoulder[k2].Y) / 2);
            var bot = new Point((low[k].X + low[k2].X) / 2, (low[k].Y + low[k2].Y) / 2);
            var face = Shade.Polygon(low[k], low[k2], shoulder[k2], shoulder[k]);
            context.DrawGeometry(Shade.Linear(top, bot, Shade.Alpha(Shade.Scale(lit, 1.15), alpha * 0.92), Shade.Alpha(Shade.Scale(lit, rough ? 0.36 : 0.4), alpha * (rough ? 0.95 : 0.75))),
                null, face);
            if (!rough)
            {
                context.DrawLine(edge, low[k], shoulder[k]);
                continue;
            }
            if (px >= 5 && n.Dot(Shade.Key) < 0.25) Hatch(context, low[k], low[k2], shoulder[k], alpha);
            else if (px >= 5) Scratch(context, low[k], low[k2], shoulder[k], shoulder[k2], id * 7 + k, alpha);
            context.DrawGeometry(null, ink, face);
        }

        for (var k = 0; k < 6; k++)
        {
            var k2 = (k + 1) % 6;
            if (!Facing(cam, axisW, shoulderW[k], shoulderW[k2], apexW, apexW, out var n)) continue;
            var lit = Shade.Lit(baseColor, n, 0.4, emissive + 0.12);
            var tri = Shade.Polygon(shoulder[k], shoulder[k2], apex);
            context.DrawGeometry(Shade.Linear(apex, new Point((shoulder[k].X + shoulder[k2].X) / 2, (shoulder[k].Y + shoulder[k2].Y) / 2),
                Shade.Alpha(Shade.Mix(lit, Colors.White, rough ? 0.2 : 0.35), alpha), Shade.Alpha(Shade.Scale(lit, 0.85), alpha * 0.92)),
                null, tri);
            if (rough) context.DrawGeometry(null, ink, tri);
            else context.DrawLine(edge, shoulder[k], apex);
        }

        // Light from inside the crystal and a spark on the point. Hand-drawn shards glow less: they read as stone.
        var axis = cam.Project(foot + new V3(0, h * 0.42, 0));
        var wpx = Math.Abs(cam.Project(foot + new V3(r, h * 0.42, 0)).X - axis.X);
        var hpx = Math.Abs(apex.Y - cam.Project(foot).Y) * 0.45;
        var core = Shade.Mix(baseColor, Colors.White, 0.45);
        Shade.Glow(context, axis, wpx * 0.9, hpx, Shade.Alpha(core, (0.22 + 0.35 * heat) * alpha * (rough ? 0.5 : 1)));
        if (spark > 0) Shade.Glow(context, apex, 6 + 4 * heat, 6 + 4 * heat, Shade.Alpha(Colors.White, spark * alpha * (rough ? 0.6 : 1)));

        if (!motes || heat <= 0.4) return;
        for (var i = 0; i < 3; i++)
        {
            var at = foot + new V3((Hash(id, 10 + i) - 0.5) * 0.9, h + 0.15 + Hash(id, 20 + i) * 0.7, (Hash(id, 30 + i) - 0.5) * 0.6);
            Shade.Glow(context, cam.Project(at), 2.6, 2.6, Shade.Alpha(Shade.Mix(baseColor, Colors.White, 0.6), 0.65 * alpha));
        }
    }

    /// <summary>True when the face (corners in outline order) turns toward the camera; its outward normal comes back too.</summary>
    private static bool Facing(Camera3D cam, V3 axis, V3 a, V3 b, V3 c, V3 d, out V3 normal)
    {
        var u = b - a;
        var v = (d == c ? c : d) - a;
        normal = new V3(u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X).Normalized;
        var centre = (a + b + c + d) * 0.25;
        if (normal.Dot(centre - axis) < 0) normal = normal * -1;
        return (cam.Eye - centre).Dot(normal) > 0;
    }

    /// <summary>The shard's shadow on the ground, thrown away from the key light; one geometry so overlaps do not darken.</summary>
    private static void CastShadow(DrawingContext context, Camera3D cam, Point[] low, V3 foot, double h, double alpha)
    {
        var reach = h * 0.9;
        var tipShadow = cam.Project(foot + new V3(-Shade.Key.X / Shade.Key.Y * reach, 0, -Shade.Key.Z / Shade.Key.Y * reach));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.SetFillRule(FillRule.NonZero);
            for (var k = 0; k < 6; k++)
            {
                ctx.BeginFigure(low[k], true);
                ctx.LineTo(low[(k + 1) % 6]);
                ctx.LineTo(tipShadow);
                ctx.EndFigure(true);
            }
        }
        context.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(110 * alpha), 0, 0, 0)), null, g);
    }

    /// <summary>Diagonal ink hatching across a face in shadow.</summary>
    private static void Hatch(DrawingContext context, Point bl, Point br, Point tl, double alpha)
    {
        var pen = new Pen(new SolidColorBrush(Shade.Alpha(InkColor, 0.55 * alpha)), 0.8);
        foreach (var v in new[] { 0.3, 0.55, 0.8 })
            context.DrawLine(pen, Lerp(bl, tl, v), Lerp(bl, br, v));
    }

    /// <summary>A chip or crack catching the light on a lit face.</summary>
    private static void Scratch(DrawingContext context, Point bl, Point br, Point tl, Point tr, long seed, double alpha)
    {
        if (Hash(seed, 70) < 0.4) return;
        var start = Lerp(tl, tr, 0.2 + 0.6 * Hash(seed, 71));
        var end = Lerp(bl, br, 0.2 + 0.6 * Hash(seed, 72));
        var kink = Lerp(start, end, 0.45);
        kink = new Point(kink.X + (Hash(seed, 73) - 0.5) * 3, kink.Y);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(70 * alpha), 255, 255, 255)), 0.7);
        context.DrawLine(pen, start, kink);
        context.DrawLine(pen, kink, Lerp(kink, end, 0.6));
    }

    private static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>
    /// A ring lying on the ground (y = <paramref name="y"/>), as a closed polyline in screen space. In the organic style the
    /// ring wobbles like a circle scratched by hand; the wobble turns with the ring.
    /// </summary>
    public static StreamGeometry Ring(Camera3D cam, V3 center, double r, int segments = 48, double y = 0, double turn = 0)
    {
        var rough = Ink.Rough;
        var seed = r * 3.7;
        var pts = new Point[segments];
        for (var i = 0; i < segments; i++)
        {
            var a = turn + i * Math.PI * 2 / segments;
            var rr = rough ? r * (1 + 0.009 * Math.Sin(a * 5 + seed) + 0.006 * Math.Sin(a * 13 + seed * 2.3)) : r;
            var (s, c) = Math.SinCos(a);
            pts[i] = cam.Project(center + new V3(s * rr, y, c * rr));
        }
        return Shade.Polygon(pts);
    }
}
