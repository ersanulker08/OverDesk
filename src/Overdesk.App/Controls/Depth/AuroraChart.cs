using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// A live "mana stream" chart: the history is one smooth luminous ribbon that fades into the fog on the left,
/// with a glowing orb at the newest sample, a light beam down to a glass floor and a mirrored reflection.
/// A fresh sample slides the ribbon one slot to the left. A handful of draw calls per frame.
/// </summary>
public sealed class AuroraChart : Control
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<AuroraChart, Color>(nameof(Color), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<Color> Color2Property =
        AvaloniaProperty.Register<AuroraChart, Color>(nameof(Color2), Color.Parse("#FF6A3D"));

    public int Slots { get; set; } = 30;
    public double Maximum { get; set; } = 100;

    /// <summary>How a sample reads in the inspect label (hover). Null shows the raw number.</summary>
    public Func<double, string>? Format { get; set; }

    /// <summary>Seconds between samples, for the "12 s ago" part of the inspect label.</summary>
    public double SampleSeconds { get; set; } = 1;

    private const double StepSeconds = 0.45;
    private const double FogSlots = 7;      // the oldest samples sink to the floor over this many slots

    private double[] _values = Array.Empty<double>();
    private double _peak;
    private double _shift;                  // 1 → 0 while a step plays
    private double _arrive = 1;             // 0 → 1: the newest sample travels from the previous value to its own
    private TimeSpan? _stepStart;
    private TimeSpan _lastDraw;
    private bool _stepping;
    private int _seen;
    private int _hotAge = -1;
    private Point? _pointer;
    private readonly List<(int Age, Point At)> _points = new();

    static AuroraChart()
    {
        AffectsRender<AuroraChart>(ColorProperty, Color2Property);
        ClipToBoundsProperty.OverrideDefaultValue<AuroraChart>(true);
    }

    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public Color Color2 { get => GetValue(Color2Property); set => SetValue(Color2Property, value); }

    public void SetValues(IReadOnlyList<double> values)
    {
        var take = Math.Min(values.Count, Slots + 2);
        var next = new double[take];
        for (var i = 0; i < take; i++) next[i] = values[values.Count - take + i];

        // A fresh sample (the history scrolled) steps the ribbon; the first fill or a rebuild just appears.
        var stepped = _seen > 0 && values.Count > 0;
        _seen++;
        _values = next;
        _peak = next.Length == 0 ? 0 : next.Max();
        if (stepped && _hotAge >= 0) _hotAge = Math.Min(_hotAge + 1, Slots);
        if (stepped) StartStep();
        else InvalidateVisual();
    }

    private void StartStep()
    {
        _stepStart = null;
        _shift = 1;
        _arrive = 0;
        if (_stepping) return;
        _stepping = true;
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame);
        else { _shift = 0; _arrive = 1; _stepping = false; InvalidateVisual(); }
    }

    private void OnFrame(TimeSpan now)
    {
        _stepStart ??= now;
        var t = Math.Clamp((now - _stepStart.Value).TotalSeconds / StepSeconds, 0, 1);
        _shift = 1 - Ease.OutCubic(t);
        _arrive = Ease.OutCubic(t);
        // Four charts step together every second: drawing them at 30 fps keeps the render thread calm.
        if (t >= 1 || (now - _lastDraw).TotalSeconds >= 1.0 / 30)
        {
            _lastDraw = now;
            InvalidateVisual();
        }
        if (t < 1 && TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame);
        else { _shift = 0; _arrive = 1; _stepping = false; }
    }

    /// <summary>Development snapshots: inspect the sample of this age as if the pointer were on it.</summary>
    internal void DevInspect(int age)
    {
        _hotAge = age;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        var hot = -1;
        var best = double.MaxValue;
        foreach (var (age, at) in _points)
        {
            var d = Math.Abs(at.X - _pointer.Value.X);
            if (d < best) { best = d; hot = age; }
        }
        if (hot == _hotAge) return;
        _hotAge = hot;
        InvalidateVisual();
    }

    protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        _hotAge = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 20 || h < 20) return;

        var color = Color;
        var color2 = Color2;
        var floorY = h * 0.80;
        var top = 7.0;
        var span = floorY - top;
        var dx = (w - 10) / Math.Max(Slots - 1, 1);

        DrawFloor(context, w, floorY, color);

        // Sample positions: newest at the right edge, history receding left and sinking into the fog.
        var pts = new List<Point>(_values.Length);
        _points.Clear();
        for (var i = 0; i < _values.Length; i++)
        {
            var age = _values.Length - 1 - i;
            var x = w - 5 - (age - _shift) * dx;
            if (x < -dx * 1.5) continue;
            var value = _values[i];
            if (age == 0 && _values.Length > 1) value = _values[i - 1] + (value - _values[i - 1]) * _arrive;
            var fog = Math.Clamp(x / (dx * FogSlots), 0, 1);
            var frac = Math.Clamp(value / Math.Max(Maximum, 1e-9), 0, 1) * Ease.OutCubic(fog);
            var p = new Point(x, floorY - Math.Max(frac * span, 1.2));
            pts.Add(p);
            if (x >= 0 && x <= w) _points.Add((age, p));
        }
        if (pts.Count < 2) return;

        var line = Ribbon(pts, top, floorY, closeToFloor: false);
        var area = Ribbon(pts, top, floorY, closeToFloor: true);

        // Reflection in the glass floor: the same area mirrored, faint.
        using (context.PushTransform(new Matrix(1, 0, 0, -1, 0, floorY * 2)))
            context.DrawGeometry(Shade.Linear(new Point(0, floorY - span), new Point(0, floorY), Shade.Alpha(color2, 0.20), Shade.Alpha(color2, 0)), null, area);

        // Body of the ribbon: lit from within, brighter toward the crest.
        context.DrawGeometry(Shade.Linear(new Point(0, top), new Point(0, floorY), Shade.Alpha(color, 0.50), Shade.Alpha(color2, 0.02)), null, area);
        // Slow-moving sheen across the body so the water looks lit from the side.
        context.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(w, 0, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Shade.Alpha(Colors.White, 0.07), 0.62),
                new GradientStop(Colors.Transparent, 1),
            },
        }, null, area);

        // The crest: wide soft halo, the colored line, a thin hot core.
        var crest = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(w, 0, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Shade.Alpha(color2, 0), 0.05),
                new GradientStop(Shade.Alpha(color2, 0.95), 0.45),
                new GradientStop(Shade.Alpha(color, 1), 1),
            },
        };
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Shade.Alpha(color, 0.16)), 7, lineCap: PenLineCap.Round), line);
        context.DrawGeometry(null, new Pen(crest, 2.2, lineCap: PenLineCap.Round), line);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Shade.Alpha(Colors.White, 0.55)), 0.8, lineCap: PenLineCap.Round), line);

        DrawPeak(context, w, floorY, span);

        // Head: light beam to the floor, a pool of light under it, the orb itself.
        var head = pts[^1];
        context.DrawLine(new Pen(Shade.Linear(head, new Point(head.X, floorY), Shade.Alpha(color, 0.55), Shade.Alpha(color, 0)), 1.2), head, new Point(head.X, floorY));
        Shade.Glow(context, new Point(head.X, floorY), 22, 5, Shade.Alpha(color, 0.45));
        Shade.Glow(context, head, 15, 15, Shade.Alpha(Shade.Mix(color, Colors.White, 0.3), 0.55));
        context.DrawEllipse(new SolidColorBrush(Shade.Mix(color, Colors.White, 0.85)), new Pen(new SolidColorBrush(Shade.Alpha(color, 0.9)), 1.2), head, 2.8, 2.8);

        if (_hotAge >= 0 && _points.FirstOrDefault(p => p.Age == _hotAge) is { At: var at } && _points.Any(p => p.Age == _hotAge))
            DrawInspect(context, at, floorY);
    }

    // Smooth ribbon through the points (Catmull-Rom as cubic Béziers, kept inside the plot so it never overshoots).
    private static StreamGeometry Ribbon(IReadOnlyList<Point> p, double top, double floorY, bool closeToFloor)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(closeToFloor ? new Point(p[0].X, floorY) : p[0], closeToFloor);
        if (closeToFloor) ctx.LineTo(p[0]);
        for (var i = 0; i < p.Count - 1; i++)
        {
            var p0 = p[Math.Max(i - 1, 0)];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[Math.Min(i + 2, p.Count - 1)];
            ctx.CubicBezierTo(
                new Point(p1.X + (p2.X - p0.X) / 8, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 8, top, floorY)),
                new Point(p2.X - (p3.X - p1.X) / 8, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 8, top, floorY)),
                p2);
        }
        if (closeToFloor) ctx.LineTo(new Point(p[^1].X, floorY));
        ctx.EndFigure(closeToFloor);
        return g;
    }

    private void DrawFloor(DrawingContext context, double w, double floorY, Color color)
    {
        // Level lines at 25 / 50 / 75 %, and the floor edge as a glowing hairline.
        var span = floorY - 7;
        for (var q = 1; q <= 3; q++)
        {
            var y = floorY - span * q / 4;
            context.DrawLine(new Pen(Shade.Linear(new Point(0, y), new Point(w, y), Shade.Alpha(Colors.White, 0), Shade.Alpha(Colors.White, 0.06)), 0.8,
                dashStyle: new DashStyle(new double[] { 1, 4 }, 0)), new Point(0, y), new Point(w, y));
        }
        context.DrawLine(new Pen(Shade.Linear(new Point(0, floorY), new Point(w, floorY), Shade.Alpha(color, 0), Shade.Alpha(color, 0.55)), 1.2),
            new Point(0, floorY), new Point(w, floorY));
    }

    private void DrawPeak(DrawingContext context, double w, double floorY, double span)
    {
        if (_values.Length < 2) return;
        var frac = Math.Clamp(_peak / Math.Max(Maximum, 1e-9), 0, 1);
        if (frac < 0.08) return;
        var y = floorY - frac * span;
        context.DrawLine(new Pen(Shade.Linear(new Point(0, y), new Point(w, y), Shade.Alpha(Colors.White, 0), Shade.Alpha(Colors.White, 0.22)), 0.8,
            dashStyle: new DashStyle(new double[] { 2, 3 }, 0)), new Point(0, y), new Point(w, y));
    }

    /// <summary>A glass tag floating over the inspected sample: its value and how long ago it was measured.</summary>
    private void DrawInspect(DrawingContext context, Point at, double floorY)
    {
        var age = _hotAge;
        var value = _values.Length - 1 - age is var i and >= 0 ? _values[i] : 0;
        var when = age == 0 ? "now" : $"{age * SampleSeconds:0} s ago";
        var text = new FormattedText($"{Format?.Invoke(value) ?? value.ToString("0")}  ·  {when}",
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(this.TryFindResource("UiFont", out var f) && f is FontFamily ff ? ff : FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            10.5, Brushes.White);
        var pad = new Thickness(7, 3);
        var tw = text.Width + pad.Left + pad.Right;
        var th = text.Height + pad.Top + pad.Bottom;
        var x = Math.Clamp(at.X - tw / 2, 0, Bounds.Width - tw);
        var y = Math.Max(at.Y - th - 9, 0);
        context.DrawLine(new Pen(Shade.Linear(new Point(at.X, y + th), new Point(at.X, floorY), Shade.Alpha(Color, 0.7), Shade.Alpha(Color, 0)), 1), new Point(at.X, y + th), new Point(at.X, floorY));
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x16, 0x1E)),
            new Pen(new SolidColorBrush(Shade.Alpha(Color, 0.7)), 1), new RoundedRect(new Rect(x, y, tw, th), th / 2),
            new BoxShadows(new BoxShadow { OffsetY = 3, Blur = 10, Color = Color.FromArgb(0xC0, 0, 0, 0) }));
        context.DrawText(text, new Point(x + pad.Left, y + pad.Top));
        Shade.Glow(context, at, 10, 10, Shade.Alpha(Colors.White, 0.5));
    }
}
