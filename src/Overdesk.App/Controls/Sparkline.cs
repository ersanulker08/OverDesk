using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls;

/// <summary>A tiny area chart of recent values (0..Maximum), newest on the right.</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<Sparkline, Color>(nameof(Color), Colors.White);

    private IReadOnlyList<double> _values = Array.Empty<double>();

    static Sparkline() => AffectsRender<Sparkline>(ColorProperty);

    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Maximum { get; set; } = 100;
    public int Capacity { get; set; } = 60;

    public void SetValues(IReadOnlyList<double> values)
    {
        _values = values;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 2 || h < 2 || _values.Count < 2) return;

        var step = w / (Capacity - 1);
        var start = w - step * (_values.Count - 1);
        Point At(int i) => new(start + i * step, h - Math.Clamp(_values[i] / Maximum, 0, 1) * (h - 2) - 1);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(At(0), false);
            for (var i = 1; i < _values.Count; i++) ctx.LineTo(At(i));
            ctx.EndFigure(false);
        }
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(start, h), true);
            for (var i = 0; i < _values.Count; i++) ctx.LineTo(At(i));
            ctx.LineTo(new Point(w, h));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(AccentThemes.WithAlpha(Color, 0x55), 0), new GradientStop(AccentThemes.WithAlpha(Color, 0), 1) },
        }, null, area);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 1.5, lineJoin: PenLineJoin.Round), line);
        context.DrawEllipse(new SolidColorBrush(Color), null, At(_values.Count - 1), 2.5, 2.5);
    }
}
