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

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        if (w <= 0) return;
        var active = _hover || _dragging;
        var thickness = active ? 5 : 3;
        var y = Bounds.Height / 2;
        var f = _dragging ? _dragFraction : Math.Clamp(Fraction, 0, 1);

        var track = new RoundedRect(new Rect(0, y - thickness / 2.0, w, thickness), thickness / 2.0);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), null, track);
        if (f > 0)
        {
            var fill = new RoundedRect(new Rect(0, y - thickness / 2.0, Math.Max(w * f, thickness), thickness), thickness / 2.0);
            context.DrawRectangle(AccentThemes.Gradient(Accent, Accent2, horizontal: true), null, fill,
                new BoxShadows(new BoxShadow { Blur = 8, Color = AccentThemes.WithAlpha(Accent, 0x80) }));
        }
        if (active)
            context.DrawEllipse(Brushes.White, null, new Point(w * f, y), 6, 6);
    }
}
