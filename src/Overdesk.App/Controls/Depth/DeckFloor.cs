using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// The dock's backdrop: a dark chamber whose floor is a huge engraved summoning circle in perspective, so the widget
/// slabs read as objects standing in a space instead of panels on a wall.
/// </summary>
public sealed class DeckFloor : Control
{
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<DeckFloor, Color>(nameof(Accent), Color.Parse("#FA1E4E"));

    static DeckFloor()
    {
        AffectsRender<DeckFloor>(AccentProperty);
        ClipToBoundsProperty.OverrideDefaultValue<DeckFloor>(true);
    }

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

    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 10 || h < 10) return;
        if (ArtStyles.Current == ArtStyle.Classic) { RenderClassic(context, w, h); return; }
        if (ArtStyles.IsLumen) { RenderStudio(context, w, h); return; }
        var accent = Accent;

        context.DrawRectangle(Shade.Linear(new Point(0, 0), new Point(0, h),
            Color.FromRgb(0x0D, 0x0C, 0x14), Color.FromRgb(0x08, 0x08, 0x0D)), null, new Rect(0, 0, w, h));

        // The circle spans more than the dock is wide and sits low, so only its near arc shows.
        const double R = 9;
        var cam = new Camera3D(new V3(0, 0, 0), 0, 64, 40, 1.6);
        cam.Fit(new V3(-R, 0, -R), new V3(R, 0, R), new Rect(-w * 0.55, h * 0.30, w * 2.1, h * 0.95));
        var origin = new V3(0, 0, 0);
        var centre = cam.Project(origin);
        Shade.Glow(context, centre, w * 1.1, h * 0.16, Shade.Alpha(accent, 0.10));
        double[] radii = { 2.2, 4.2, 6.2, 8.2 };
        for (var i = 0; i < radii.Length; i++)
        {
            Ink.Stroke(context, Crystal3D.Ring(cam, origin, radii[i], 72), accent, 0.28 - 0.05 * i, i == 3 ? 1.2 : 0.8,
                i == 1 ? new DashStyle(new double[] { 1.5, 3 }, 0) : null);
        }

        // Hexagram between the rings, glyph ticks on the outer band.
        foreach (var offset in new[] { 0.0, Math.PI / 3 })
            for (var k = 0; k < 3; k++)
            {
                var a1 = offset + k * Math.PI * 2 / 3;
                var a2 = offset + (k + 1) * Math.PI * 2 / 3;
                Ink.Line(context, cam.Project(new V3(Math.Sin(a1) * 6.2, 0, Math.Cos(a1) * 6.2)), cam.Project(new V3(Math.Sin(a2) * 6.2, 0, Math.Cos(a2) * 6.2)), accent, 0.2, 0.8);
            }
        for (var i = 0; i < 48; i++)
        {
            var (sn, cs) = Math.SinCos(i * Math.PI / 24);
            var inner = i % 4 == 0 ? 7.2 : 7.7;
            Ink.Line(context, cam.Project(new V3(sn * inner, 0, cs * inner)), cam.Project(new V3(sn * 8.2, 0, cs * 8.2)), accent, 0.3, 0.8);
        }

        // Haze where the far arc fades into the dark.
        context.DrawRectangle(Shade.Linear(new Point(0, h * 0.28), new Point(0, h * 0.52), Color.FromRgb(0x0B, 0x0A, 0x10), Colors.Transparent), null,
            new Rect(0, h * 0.28, w, h * 0.24));

        // Vignette at the sides so the room has walls.
        context.DrawRectangle(Shade.Linear(new Point(0, 0), new Point(w * 0.25, 0), Color.FromArgb(0x50, 0, 0, 0), Colors.Transparent), null, new Rect(0, 0, w * 0.25, h));
        context.DrawRectangle(Shade.Linear(new Point(w, 0), new Point(w * 0.75, 0), Color.FromArgb(0x50, 0, 0, 0), Colors.Transparent), null, new Rect(w * 0.75, 0, w * 0.25, h));
    }

    // ---- lumen style: the dock stands in a dark studio lit by lamps over the page, alternating warm key and colored fill

    private void RenderStudio(DrawingContext context, double w, double h)
    {
        var accent = Accent;
        var key = Shade.Mix(Studio.WarmLight, accent, 0.18);
        var fill = Shade.Mix(accent, Colors.White, 0.1);
        context.DrawRectangle(Shade.Linear(new Point(0, 0), new Point(0, h), Color.FromRgb(0x0C, 0x0C, 0x13), Color.FromRgb(0x07, 0x07, 0x0B)), null, new Rect(0, 0, w, h));

        var stage = Math.Max(h / 3.2, 260);
        for (var i = 0; i * stage < h + stage; i++)
        {
            var y = stage * (i + 0.55);
            var left = i % 2 == 0;
            var apex = new Point(left ? w * 0.05 : w * 0.95, y - stage * 0.55);
            var target = new Point(w * (left ? 0.62 : 0.38), y + stage * 0.15);
            Studio.Cone(context, apex, target, w * 0.5, left ? key : fill, left ? 0.75 : 0.55);
        }

        // Vignette at the sides so the room has walls.
        context.DrawRectangle(Shade.Linear(new Point(0, 0), new Point(w * 0.25, 0), Color.FromArgb(0x60, 0, 0, 0), Colors.Transparent), null, new Rect(0, 0, w * 0.25, h));
        context.DrawRectangle(Shade.Linear(new Point(w, 0), new Point(w * 0.75, 0), Color.FromArgb(0x60, 0, 0, 0), Colors.Transparent), null, new Rect(w * 0.75, 0, w * 0.25, h));
    }

    // ---- classic style

    private void RenderClassic(DrawingContext context, double w, double h)
    {
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
