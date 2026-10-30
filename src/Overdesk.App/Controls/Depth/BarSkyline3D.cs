using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// A live 3D bar chart: every sample is a lit block standing on a glass floor, seen from above and to the side.
/// When a new sample arrives the whole row steps one slot back (a short eased "clunk") while the newest block
/// rises out of the floor; the oldest one sinks into the fog at the far end. Replaces the flat sparkline.
/// </summary>
public sealed class BarSkyline3D : Control
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<BarSkyline3D, Color>(nameof(Color), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<Color> Color2Property =
        AvaloniaProperty.Register<BarSkyline3D, Color>(nameof(Color2), Color.Parse("#FF6A3D"));

    /// <summary>Blocks on stage. Fewer, larger blocks read better than a dense comb at dock width.</summary>
    public int Slots { get; set; } = 20;
    public double Maximum { get; set; } = 100;

    private const double BlockWidth = 0.74;
    private const double BlockDepth = 1.05;
    private const double WorldHeight = 3.0;
    private const double StepSeconds = 0.5;

    private double[] _values = Array.Empty<double>();
    private double _peak;
    private double _shift;      // 1 → 0 while a step plays (slots the row still has to travel)
    private double _rise = 1;   // 0 → 1: height of the newest block
    private TimeSpan? _stepStart;
    private bool _stepping;
    private int _seen;

    static BarSkyline3D()
    {
        AffectsRender<BarSkyline3D>(ColorProperty, Color2Property);
        // The newest block slides in from beyond the front edge, like a conveyor.
        ClipToBoundsProperty.OverrideDefaultValue<BarSkyline3D>(true);
    }

    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public Color Color2 { get => GetValue(Color2Property); set => SetValue(Color2Property, value); }

    public void SetValues(IReadOnlyList<double> values)
    {
        var take = Math.Min(values.Count, Slots + 1);
        var next = new double[take];
        for (var i = 0; i < take; i++) next[i] = values[values.Count - take + i];

        // A fresh sample (the history scrolled) steps the row; the first fill or a rebuild just appears.
        var stepped = _seen > 0 && values.Count > 0;
        _seen++;
        _values = next;
        _peak = next.Length == 0 ? 0 : next.Max();
        if (stepped) StartStep();
        else InvalidateVisual();
    }

    private void StartStep()
    {
        _stepStart = null;
        _shift = 1;
        _rise = 0;
        if (_stepping) return;
        _stepping = true;
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame);
        else { _shift = 0; _rise = 1; _stepping = false; InvalidateVisual(); }
    }

    private void OnFrame(TimeSpan now)
    {
        _stepStart ??= now;
        var t = Math.Clamp((now - _stepStart.Value).TotalSeconds / StepSeconds, 0, 1);
        // The row moves with a mechanical settle (slight overshoot); the new block springs up a beat later.
        _shift = 1 - Ease.OutBack(Math.Min(t * 1.15, 1));
        _rise = Ease.OutBack(Math.Clamp((t - 0.12) / 0.88, 0, 1));
        InvalidateVisual();
        if (t < 1 && TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame);
        else { _shift = 0; _rise = 1; _stepping = false; }
    }

    private readonly record struct Block(double X, double Height, double Fade, bool Newest, double Heat);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 20 || h < 20) return;

        var n = Slots;
        var cam = new Camera3D(new V3(n / 2.0, WorldHeight * 0.32, 0), yawDegrees: 19, pitchDegrees: 17, distance: n * 1.55, focal: 1.5);
        cam.Fit(new V3(-0.6, 0, -BlockDepth / 2 - 0.8), new V3(n + 0.2, WorldHeight, BlockDepth / 2 + 0.6), new Rect(2, 2, w - 4, h - 4));

        DrawFloor(context, cam, n);

        // Newest block sits in the front slot (x = n - 1); history marches toward x = 0, then sinks into the fog.
        var blocks = new List<Block>(_values.Length);
        for (var i = 0; i < _values.Length; i++)
        {
            var age = _values.Length - 1 - i;
            var x = n - 1 - age + _shift;
            if (x < -1 || x > n) continue;
            var newest = age == 0;
            var frac = Math.Clamp(_values[i] / Math.Max(Maximum, 1e-9), 0, 1);
            var height = Math.Max(frac * WorldHeight, 0.06) * (newest ? _rise : 1);
            // Fog: blocks fade and sink as they approach the far end.
            var fade = Math.Clamp((x + 1) / 3.5, 0, 1);
            if (x < 0) height *= Math.Clamp(x + 1, 0, 1);
            blocks.Add(new Block(x, height, fade, newest, frac));
        }

        // Reflections first, under everything.
        foreach (var b in blocks) DrawReflection(context, cam, b);
        // Painter's order: the farthest block first.
        foreach (var b in blocks.OrderByDescending(b => cam.Depth(new V3(b.X + 0.5, b.Height / 2, 0)))) DrawBlock(context, cam, b);

        DrawPeakLine(context, cam, n);
    }

    private Color BlockColor(double heat) => Shade.Mix(Color2, Color, 0.25 + 0.75 * heat);

    private void DrawFloor(DrawingContext context, Camera3D cam, int n)
    {
        double z0 = -BlockDepth / 2 - 0.7, z1 = BlockDepth / 2 + 0.5, x0 = -0.8, x1 = n + 0.1;
        var floor = Shade.Polygon(cam.Project(new V3(x0, 0, z0)), cam.Project(new V3(x1, 0, z0)),
                                  cam.Project(new V3(x1, 0, z1)), cam.Project(new V3(x0, 0, z1)));
        context.DrawGeometry(Shade.Linear(cam.Project(new V3(x0, 0, 0)), cam.Project(new V3(x1, 0, 0)),
            Color.FromArgb(0x00, 0x14, 0x14, 0x1C), Color.FromArgb(0xD0, 0x16, 0x16, 0x20)), null, floor);

        // Grid lines: one per slot, plus a few running front to back. Brighter toward the viewer.
        for (var i = 0; i <= n; i++)
        {
            var x = i - 0.17 + _shift;
            if (x < x0 || x > x1) continue;
            var a = 0.03 + 0.14 * Math.Clamp(x / n, 0, 1);
            context.DrawLine(new Pen(new SolidColorBrush(Shade.Alpha(Color, a)), 0.8), cam.Project(new V3(x, 0, z0)), cam.Project(new V3(x, 0, z1)));
        }
        foreach (var z in new[] { z0, -BlockDepth / 2, BlockDepth / 2, z1 })
        {
            var pen = new Pen(Shade.Linear(cam.Project(new V3(x0, 0, z)), cam.Project(new V3(x1, 0, z)),
                Shade.Alpha(Color, 0), Shade.Alpha(Color, Math.Abs(z - z1) < 1e-6 ? 0.45 : 0.16)), Math.Abs(z - z1) < 1e-6 ? 1.2 : 0.8);
            context.DrawLine(pen, cam.Project(new V3(x0, 0, z)), cam.Project(new V3(x1, 0, z)));
        }
    }

    private void DrawBlock(DrawingContext context, Camera3D cam, Block b)
    {
        double x0 = b.X + (1 - BlockWidth) / 2, x1 = x0 + BlockWidth, y1 = b.Height;
        Box3D.Draw(context, cam, Box3D.Aligned(new V3(x0, 0, -BlockDepth / 2), new V3(x1, y1, BlockDepth / 2)),
            BlockColor(b.Heat), b.Fade, b.Newest ? 0.18 : 0);

        if (b.Newest)
        {
            // The live block glows: a light pooling on its lid and on the floor in front of it.
            var lidCenter = cam.Project(new V3((x0 + x1) / 2, y1, 0));
            Shade.Glow(context, lidCenter, 16, 7, Shade.Alpha(Shade.Mix(Color, Colors.White, 0.35), 0.55 * _rise));
            var floorSpot = cam.Project(new V3((x0 + x1) / 2, 0, BlockDepth / 2 + 0.15));
            Shade.Glow(context, floorSpot, 18, 5, Shade.Alpha(Color, 0.4 * _rise));
        }
    }

    /// <summary>Mirror image in the glass floor, front face only, fading out quickly.</summary>
    private void DrawReflection(DrawingContext context, Camera3D cam, Block b)
    {
        double x0 = b.X + (1 - BlockWidth) / 2, x1 = x0 + BlockWidth, z = BlockDepth / 2;
        var depth = Math.Min(b.Height, WorldHeight * 0.45);
        var top = cam.Project(new V3(x0, 0, z));
        var bottom = cam.Project(new V3(x0, -depth, z));
        var c = BlockColor(b.Heat);
        context.DrawGeometry(Shade.Linear(top, bottom, Shade.Alpha(c, 0.28 * b.Fade), Shade.Alpha(c, 0)), null,
            Shade.Polygon(cam.Project(new V3(x0, 0, z)), cam.Project(new V3(x1, 0, z)),
                          cam.Project(new V3(x1, -depth, z)), cam.Project(new V3(x0, -depth, z))));
    }

    /// <summary>A faint glass line at the highest value on screen, so the eye has a scale to read against.</summary>
    private void DrawPeakLine(DrawingContext context, Camera3D cam, int n)
    {
        if (_values.Length < 2) return;
        var y = Math.Clamp(_peak / Math.Max(Maximum, 1e-9), 0, 1) * WorldHeight;
        if (y < 0.2) return;
        var z = -BlockDepth / 2 - 0.05;
        var a = cam.Project(new V3(0, y, z));
        var b = cam.Project(new V3(n, y, z));
        context.DrawLine(new Pen(Shade.Linear(a, b, Shade.Alpha(Colors.White, 0), Shade.Alpha(Colors.White, 0.22)), 0.8,
            dashStyle: new DashStyle(new double[] { 2, 3 }, 0)), a, b);
    }
}
