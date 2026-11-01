using Avalonia;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

public enum ArtStyle
{
    /// <summary>Lit blocks on a glass grid, flowing strands behind the pages.</summary>
    Classic,
    /// <summary>Clean glowing crystals on rune circles, a turning summoning circle behind the pages.</summary>
    Crystal,
    /// <summary>The crystal world drawn by hand: chipped shards, thick ink lines, hatching and cast shadows.</summary>
    Organic,
    /// <summary>A lit studio: matte pillars and orbs sculpted by key, rim and bounce light, soft cast shadows, spotlight cones. No crystals, runes or grids.</summary>
    Lumen,
}

/// <summary>The look every 3D widget and backdrop draws in. Widgets repaint when it changes.</summary>
public static class ArtStyles
{
    public static ArtStyle Current { get; private set; } = ArtStyle.Crystal;

    public static bool IsLumen => Current == ArtStyle.Lumen;

    public static event Action? Changed;

    public static IReadOnlyList<(ArtStyle Style, string Description)> All { get; } = new[]
    {
        (ArtStyle.Classic, "Blocks, glass grid, flowing strands"),
        (ArtStyle.Crystal, "Glowing crystals on rune circles"),
        (ArtStyle.Organic, "Chipped crystals, ink lines, shadows"),
        (ArtStyle.Lumen, "Studio light: sculpted pillars and orbs, soft shadows"),
    };

    public static void Apply(string? name)
    {
        var style = Enum.TryParse<ArtStyle>(name, true, out var s) ? s : ArtStyle.Crystal;
        if (style == Current) return;
        Current = style;
        Changed?.Invoke();
    }
}

/// <summary>
/// Lines for the rune drawings. In the organic style every line is drawn like ink on stone: a cast shadow, a thick
/// dark outline and then the colored stroke, slightly wobbly. Otherwise it is a plain thin line.
/// </summary>
internal static class Ink
{
    public static bool Rough => ArtStyles.Current == ArtStyle.Organic;

    private static readonly Color Dark = Color.FromRgb(0x07, 0x05, 0x09);

    public static void Stroke(DrawingContext context, Geometry geometry, Color color, double alpha, double width, DashStyle? dash = null)
    {
        if (!Rough)
        {
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Shade.Alpha(color, alpha)), width, dash), geometry);
            return;
        }
        using (context.PushTransform(Matrix.CreateTranslation(1.4, 2.2)))
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Shade.Alpha(Colors.Black, 0.55 * Math.Min(alpha * 2, 1))), width * 1.6 + 1.4, dash), geometry);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Shade.Alpha(Dark, 0.85 * Math.Min(alpha * 2, 1))), width * 1.4 + 1.0, dash), geometry);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Shade.Alpha(color, Math.Min(alpha * 1.4, 1))), width * 1.35, dash), geometry);
    }

    public static void Line(DrawingContext context, Point a, Point b, Color color, double alpha, double width)
    {
        if (!Rough)
        {
            context.DrawLine(new Pen(new SolidColorBrush(Shade.Alpha(color, alpha)), width), a, b);
            return;
        }
        // A hand-drawn line bows a little: bend it at the middle by an amount that depends on where it is.
        var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var bend = (Crystal3D.Hash((long)(a.X * 7 + b.Y * 13), 3) - 0.5) * Math.Min(len * 0.08, 3);
        if (len > 0) mid = new Point(mid.X - dy / len * bend, mid.Y + dx / len * bend);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, false);
            ctx.LineTo(mid);
            ctx.LineTo(b);
            ctx.EndFigure(false);
        }
        Stroke(context, g, color, alpha, width);
    }
}
