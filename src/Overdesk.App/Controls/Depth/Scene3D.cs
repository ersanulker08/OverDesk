using Avalonia;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>A point or direction in the little 3D world the dock's widgets are drawn in (y is up, z toward the viewer).</summary>
internal readonly record struct V3(double X, double Y, double Z)
{
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator *(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public double Dot(V3 o) => X * o.X + Y * o.Y + Z * o.Z;
    public double Length => Math.Sqrt(Dot(this));
    public V3 Normalized => this * (1 / Math.Max(Length, 1e-9));
}

/// <summary>
/// A pinhole camera that orbits a target: yaw turns it around the vertical axis, pitch looks down onto the scene.
/// <see cref="Fit"/> scales and centers a world volume into a control, so widgets think in world units only.
/// </summary>
internal sealed class Camera3D
{
    private double _cy, _sy, _cp, _sp;
    private double _scale = 1, _ox, _oy;

    public Camera3D(V3 target, double yawDegrees, double pitchDegrees, double distance, double focal = 1.6)
    {
        Target = target;
        Distance = distance;
        Focal = focal;
        SetAngles(yawDegrees, pitchDegrees);
    }

    public V3 Target { get; set; }
    public double Distance { get; set; }
    public double Focal { get; set; }

    /// <summary>World position of the eye (for lighting and back-face tests).</summary>
    public V3 Eye { get; private set; }

    public void SetAngles(double yawDegrees, double pitchDegrees)
    {
        var yaw = yawDegrees * Math.PI / 180;
        var pitch = pitchDegrees * Math.PI / 180;
        (_sy, _cy) = Math.SinCos(yaw);
        (_sp, _cp) = Math.SinCos(pitch);
        // Inverse of the view rotation applied to (0, 0, Distance).
        Eye = Target + new V3(_sy * _cp * Distance, _sp * Distance, _cy * _cp * Distance);
    }

    /// <summary>Camera-space coordinates: x right, y up, z = distance in front of the eye.</summary>
    public V3 View(V3 p)
    {
        var d = p - Target;
        // Yaw around Y, then pitch around X.
        var x = d.X * _cy - d.Z * _sy;
        var z1 = d.X * _sy + d.Z * _cy;
        var y = d.Y * _cp - z1 * _sp;
        var z = d.Y * _sp + z1 * _cp;
        return new V3(x, y, Distance - z);
    }

    /// <summary>Raw projection before <see cref="Fit"/>: y grows downward like the screen.</summary>
    private Point Raw(V3 p)
    {
        var v = View(p);
        var k = Focal / Math.Max(v.Z, 0.05);
        return new Point(v.X * k, -v.Y * k);
    }

    public Point Project(V3 p)
    {
        var r = Raw(p);
        return new Point(r.X * _scale + _ox, r.Y * _scale + _oy);
    }

    public double Depth(V3 p) => View(p).Z;

    /// <summary>Make every corner of the box (min..max) land inside <paramref name="area"/>, centered.</summary>
    public void Fit(V3 min, V3 max, Rect area)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var p = Raw(new V3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
            l = Math.Min(l, p.X); r = Math.Max(r, p.X); t = Math.Min(t, p.Y); b = Math.Max(b, p.Y);
        }
        _scale = Math.Min(area.Width / Math.Max(r - l, 1e-6), area.Height / Math.Max(b - t, 1e-6));
        _ox = area.X + (area.Width - (r - l) * _scale) / 2 - l * _scale;
        _oy = area.Y + (area.Height - (b - t) * _scale) / 2 - t * _scale;
    }
}

/// <summary>Light, shading and polygon helpers shared by the 3D widgets.</summary>
internal static class Shade
{
    /// <summary>Key light from the upper left front; a dimmer rim light from behind gives edges a sheen.</summary>
    public static readonly V3 Key = new V3(-0.45, 0.85, 0.55).Normalized;
    public static readonly V3 Rim = new V3(0.6, 0.3, -0.75).Normalized;

    /// <summary>Lambert shading with ambient fill and a tinted rim light.</summary>
    public static Color Lit(Color c, V3 normal, double ambient = 0.32, double emissive = 0)
    {
        var diffuse = Math.Max(0, normal.Dot(Key));
        var rim = Math.Pow(Math.Max(0, normal.Dot(Rim)), 2) * 0.35;
        var k = ambient + 0.78 * diffuse + rim + emissive;
        return Color.FromArgb(c.A, Ch(c.R * k + 255 * emissive * 0.25), Ch(c.G * k + 255 * emissive * 0.25), Ch(c.B * k + 255 * emissive * 0.25));
    }

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(Ch(a.A + (b.A - a.A) * t), Ch(a.R + (b.R - a.R) * t), Ch(a.G + (b.G - a.G) * t), Ch(a.B + (b.B - a.B) * t));
    }

    public static Color Alpha(Color c, double a) => Color.FromArgb(Ch(255 * Math.Clamp(a, 0, 1) * c.A / 255.0), c.R, c.G, c.B);

    public static Color Scale(Color c, double k) => Color.FromArgb(c.A, Ch(c.R * k), Ch(c.G * k), Ch(c.B * k));

    private static byte Ch(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    public static StreamGeometry Polygon(params Point[] points)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(points[0], true);
        for (var i = 1; i < points.Length; i++) ctx.LineTo(points[i]);
        ctx.EndFigure(true);
        return g;
    }

    /// <summary>A gradient that runs between two screen points (absolute coordinates).</summary>
    public static LinearGradientBrush Linear(Point from, Point to, Color a, Color b) => new()
    {
        StartPoint = new RelativePoint(from, RelativeUnit.Absolute),
        EndPoint = new RelativePoint(to, RelativeUnit.Absolute),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
    };

    /// <summary>A soft round light: full color in the middle, nothing at the edge.</summary>
    public static void Glow(DrawingContext context, Point center, double rx, double ry, Color color)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(color, 0), new GradientStop(Alpha(color, 0.35 * color.A / 255.0), 0.45), new GradientStop(Alpha(color, 0), 1) },
        };
        context.DrawEllipse(brush, null, center, rx, ry);
    }
}
