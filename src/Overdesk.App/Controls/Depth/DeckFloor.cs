using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// The dock's backdrop: a dark room whose floor is a perspective grid running away toward a faint horizon,
/// so the widget slabs read as objects standing in a space instead of panels on a wall.
/// </summary>
public sealed class DeckFloor : Control
{
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<DeckFloor, Color>(nameof(Accent), Color.Parse("#FA1E4E"));

    static DeckFloor() => AffectsRender<DeckFloor>(AccentProperty);

    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 10 || h < 10) return;
        var accent = Accent;

        context.DrawRectangle(Shade.Linear(new Point(0, 0), new Point(0, h),
            Color.FromRgb(0x0E, 0x0E, 0x14), Color.FromRgb(0x09, 0x09, 0x0D)), null, new Rect(0, 0, w, h));

        // Floor: from the horizon (55% down) to the bottom edge; lines converge on a point above the middle.
        var horizon = h * 0.55;
        var vanish = new Point(w * 0.5, horizon - h * 0.05);
        Shade.Glow(context, new Point(w * 0.5, horizon), w * 0.9, h * 0.08, Shade.Alpha(accent, 0.10));
        for (var i = -9; i <= 9; i++)
        {
            var bottom = new Point(w * 0.5 + i * w * 0.16, h);
            var a = 0.10 * (1 - Math.Abs(i) / 10.0);
            context.DrawLine(new Pen(Shade.Linear(vanish, bottom, Shade.Alpha(accent, 0), Shade.Alpha(accent, a)), 0.8),
                Lerp(vanish, bottom, 0.12), bottom);
        }
        // Cross lines: spacing grows toward the viewer (1/z).
        for (var k = 1; k <= 14; k++)
        {
            var t = 1 - 1.0 / (1 + k * 0.32);
            var y = horizon + (h - horizon) * t;
            var a = 0.02 + 0.08 * t;
            context.DrawLine(new Pen(Shade.Linear(new Point(0, y), new Point(w, y), Shade.Alpha(accent, 0), Shade.Alpha(accent, a)), 0.8),
                new Point(0, y), new Point(w / 2, y));
            context.DrawLine(new Pen(Shade.Linear(new Point(w, y), new Point(0, y), Shade.Alpha(accent, 0), Shade.Alpha(accent, a)), 0.8),
                new Point(w, y), new Point(w / 2, y));
        }
        context.DrawLine(new Pen(Shade.Linear(new Point(0, horizon), new Point(w, horizon), Shade.Alpha(accent, 0), Shade.Alpha(accent, 0.22)), 1),
            new Point(0, horizon), new Point(w * 0.5, horizon));
        context.DrawLine(new Pen(Shade.Linear(new Point(w, horizon), new Point(0, horizon), Shade.Alpha(accent, 0), Shade.Alpha(accent, 0.22)), 1),
            new Point(w, horizon), new Point(w * 0.5, horizon));

        // Vignette at the sides so the room has walls.
        context.DrawRectangle(Shade.Linear(new Point(0, 0), new Point(w * 0.25, 0), Color.FromArgb(0x50, 0, 0, 0), Colors.Transparent), null, new Rect(0, 0, w * 0.25, h));
        context.DrawRectangle(Shade.Linear(new Point(w, 0), new Point(w * 0.75, 0), Color.FromArgb(0x50, 0, 0, 0), Colors.Transparent), null, new Rect(w * 0.75, 0, w * 0.25, h));
    }

    private static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
