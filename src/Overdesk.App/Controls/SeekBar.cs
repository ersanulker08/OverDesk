using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls;

/// <summary>Playback progress bar: thin at rest, thicker with a knob on hover, drag or click to seek.</summary>
public sealed class SeekBar : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<SeekBar, double>(nameof(Fraction));
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<SeekBar, Color>(nameof(Accent), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<Color> Accent2Property =
        AvaloniaProperty.Register<SeekBar, Color>(nameof(Accent2), Color.Parse("#FF6A3D"));

    private bool _dragging;
    private bool _hover;
    private double _dragFraction;

    static SeekBar()
    {
        AffectsRender<SeekBar>(FractionProperty, AccentProperty, Accent2Property);
        CursorProperty.OverrideDefaultValue<SeekBar>(new Cursor(StandardCursorType.Hand));
    }

    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Color Accent2 { get => GetValue(Accent2Property); set => SetValue(Accent2Property, value); }

    /// <summary>The user released the bar at this fraction (0..1).</summary>
    public event Action<double>? Seeked;

    /// <summary>While dragging, the fraction under the pointer (for live time labels); null otherwise.</summary>
    public double? PreviewFraction => _dragging ? _dragFraction : null;

    protected override Size MeasureOverride(Size availableSize) => new(0, 14);

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); _hover = true; InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = false; InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        _dragFraction = FractionAt(e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        _dragFraction = FractionAt(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Fraction = _dragFraction;
        Seeked?.Invoke(_dragFraction);
        InvalidateVisual();
    }

    private double FractionAt(Point p) => Bounds.Width <= 0 ? 0 : Math.Clamp(p.X / Bounds.Width, 0, 1);

    /// <summary>
    /// A groove cut into the card (dark channel, shadow on its upper lip, light on the lower one) with a glowing,
    /// slightly raised fill and a polished ball riding at its end.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        if (w <= 0) return;
        var active = _hover || _dragging;
        var t = active ? 7.0 : 5.0;
        var y = Bounds.Height / 2;
        var f = _dragging ? _dragFraction : Math.Clamp(Fraction, 0, 1);
        var groove = new Rect(0, y - t / 2, w, t);

        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, groove.Top, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, groove.Bottom, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromRgb(0x03, 0x03, 0x05), 0), new GradientStop(Color.FromRgb(0x14, 0x14, 0x1A), 1) },
        }, null, new RoundedRect(groove, t / 2));
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x2A, 255, 255, 255)), 1),
            new Point(t / 2, groove.Bottom + 0.5), new Point(w - t / 2, groove.Bottom + 0.5));

        var x = w * f;
        if (f > 0)
        {
            var fill = new Rect(0.5, groove.Top + 1, Math.Max(x - 0.5, t), t - 2);
            context.DrawRectangle(AccentThemes.Gradient(Accent2, Accent, horizontal: true), null, new RoundedRect(fill, (t - 2) / 2),
                new BoxShadows(new BoxShadow { Blur = 10, Color = AccentThemes.WithAlpha(Accent, 0x90) }));
            // Lit top of the raised fill.
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), 1),
                new Point(fill.Left + 2, fill.Top + 0.8), new Point(Math.Max(fill.Right - 2, fill.Left + 2), fill.Top + 0.8));
        }

        // The ball: shadow on the groove, then a sphere lit from the upper left.
        var r = active ? 7.5 : 5.5;
        var ball = new Point(Math.Clamp(x, r, w - r), y);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), null, new Point(ball.X + 1, ball.Y + 2), r, r * 0.9);
        context.DrawEllipse(new RadialGradientBrush
        {
            Center = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Color.FromRgb(0xD8, 0xD8, 0xE2), 0.45),
                new GradientStop(AccentThemes.WithAlpha(Accent, 0xFF) is var a ? Color.FromRgb((byte)(a.R * 0.45), (byte)(a.G * 0.45), (byte)(a.B * 0.45)) : Colors.Gray, 1),
            },
        }, null, ball, r, r);
        if (active)
            context.DrawEllipse(null, new Pen(new SolidColorBrush(AccentThemes.WithAlpha(Accent, 0x90)), 1.2), ball, r + 2.5, r + 2.5);
    }
}
