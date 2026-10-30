using Avalonia;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// Draws one lit, solid box from its eight world corners (bit 0 = +u side, bit 1 = top, bit 2 = +v side), so boxes may
/// be turned any way (the focus dial's blocks stand on a circle). Only faces turned toward the camera are drawn, and a
/// convex box's visible faces never overlap, so no sorting is needed inside a box. Sort boxes far-to-near yourself.
/// </summary>
internal static class Box3D
{
    // Each face: four corner indices in outline order, the first two along the bottom where it applies.
    private static readonly int[][] Faces =
    {
        new[] { 0, 4, 6, 2 }, // -u
        new[] { 5, 1, 3, 7 }, // +u
        new[] { 1, 0, 2, 3 }, // -v
        new[] { 4, 5, 7, 6 }, // +v
        new[] { 2, 6, 7, 3 }, // top
    };

    public static void Draw(DrawingContext context, Camera3D cam, V3[] c, Color color, double alpha = 1, double emissive = 0, double bevel = 1)
    {
        var center = new V3(0, 0, 0);
        foreach (var p in c) center += p * 0.125;
        var screen = new Point[8];
        for (var i = 0; i < 8; i++) screen[i] = cam.Project(c[i]);

        var topVisible = false;
        Span<bool> visible = stackalloc bool[5];
        for (var f = 0; f < 5; f++)
        {
            var idx = Faces[f];
            var faceCenter = (c[idx[0]] + c[idx[1]] + c[idx[2]] + c[idx[3]]) * 0.25;
            var normal = (faceCenter - center).Normalized;
            visible[f] = (cam.Eye - faceCenter).Dot(normal) > 0;
            if (!visible[f]) continue;
            if (f == 4)
            {
                topVisible = true;
                var lid = Shade.Lit(color, normal, 0.45, emissive + 0.12);
                context.DrawGeometry(new SolidColorBrush(Shade.Alpha(lid, alpha)), null,
                    Shade.Polygon(screen[idx[0]], screen[idx[1]], screen[idx[2]], screen[idx[3]]));
                continue;
            }
            // Sides: lighter near the lid, darker where they meet the ground.
            var lit = Shade.Lit(color, normal, f >= 2 ? 0.3 : 0.2, emissive);
            Point bl = screen[idx[0]], br = screen[idx[1]], tr = screen[idx[2]], tl = screen[idx[3]];
            var top = new Point((tl.X + tr.X) / 2, (tl.Y + tr.Y) / 2);
            var low = new Point((bl.X + br.X) / 2, (bl.Y + br.Y) / 2);
            context.DrawGeometry(Shade.Linear(top, low, Shade.Alpha(Shade.Scale(lit, 1.1), alpha), Shade.Alpha(Shade.Scale(lit, 0.52), alpha)),
                null, Shade.Polygon(bl, br, tr, tl));
        }

        if (!topVisible || bevel <= 0) return;
        // Catch light on the lid edges that border a visible side: reads as a chamfer.
        var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(140 * alpha * bevel), 255, 255, 255)), 0.8);
        for (var f = 0; f < 4; f++)
        {
            if (!visible[f]) continue;
            var idx = Faces[f];
            context.DrawLine(pen, screen[idx[3]], screen[idx[2]]);
        }
    }

    /// <summary>Corners of an upright box spanning <paramref name="min"/>..<paramref name="max"/>.</summary>
    public static V3[] Aligned(V3 min, V3 max)
    {
        var c = new V3[8];
        for (var i = 0; i < 8; i++)
            c[i] = new V3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        return c;
    }

    /// <summary>
    /// Corners of an upright box standing at <paramref name="center"/> (on the ground), turned so its +v side faces
    /// <paramref name="facing"/> (an angle around the vertical axis, radians).
    /// </summary>
    public static V3[] Turned(V3 center, double width, double depth, double height, double facing)
    {
        var (s, co) = Math.SinCos(facing);
        var u = new V3(co, 0, -s);   // across
        var v = new V3(s, 0, co);    // outward
        var c = new V3[8];
        for (var i = 0; i < 8; i++)
        {
            var du = ((i & 1) == 0 ? -0.5 : 0.5) * width;
            var dv = ((i & 4) == 0 ? -0.5 : 0.5) * depth;
            var dy = (i & 2) == 0 ? 0 : height;
            c[i] = center + u * du + v * dv + new V3(0, dy, 0);
        }
        return c;
    }
}
