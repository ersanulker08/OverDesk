using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// Embers rising over the dock. Each ember is its own small element moved with a render transform, so a tick
/// only repaints a few tiny rectangles; one big overlay would repaint every card under it.
/// </summary>
public sealed class EmberField : Canvas
{
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<EmberField, Color>(nameof(Accent), Color.Parse("#FA1E4E"));

    public static readonly StyledProperty<Color> Accent2Property =
        AvaloniaProperty.Register<EmberField, Color>(nameof(Accent2), Color.Parse("#FF7A45"));

    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<EmberField, bool>(nameof(IsAnimated), true);

    private const int Count = 14;
    private const double FrameSeconds = 1.0 / 24;

    private readonly (Ellipse Dot, TranslateTransform Move, double X, double Phase, double Speed, double Size, double Drift)[] _embers = new (Ellipse, TranslateTransform, double, double, double, double, double)[Count];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(FrameSeconds) };
    private double _t = 40;

    public EmberField()
    {
        var rng = new Random(7);
        for (var i = 0; i < Count; i++)
        {
            var size = 8 + rng.NextDouble() * 8;
            var move = new TranslateTransform();
            var dot = new Ellipse { Width = size, Height = size, RenderTransform = move, Opacity = 0, IsHitTestVisible = false };
            _embers[i] = (dot, move, rng.NextDouble(), rng.NextDouble(), 0.05 + rng.NextDouble() * 0.06, size, 6 + rng.NextDouble() * 14);
            Children.Add(dot);
        }
        _timer.Tick += (_, _) => Tick();
        IsHitTestVisible = false;
    }

    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Color Accent2 { get => GetValue(Accent2Property); set => SetValue(Accent2Property, value); }
    public bool IsAnimated { get => GetValue(IsAnimatedProperty); set => SetValue(IsAnimatedProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Recolor();
        if (IsAnimated) _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AccentProperty || change.Property == Accent2Property) Recolor();
        if (change.Property != IsAnimatedProperty || TopLevel.GetTopLevel(this) is null) return;
        if (IsAnimated) _timer.Start();
        else
        {
            _timer.Stop();
            foreach (var e in _embers) e.Dot.Opacity = 0;
        }
    }

    private void Recolor()
    {
        var warm = Shade.Mix(Accent2, Colors.White, 0.4);
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Shade.Alpha(warm, 1), 0),
                new GradientStop(Shade.Alpha(warm, 0.75), 0.16),
                new GradientStop(Shade.Alpha(Accent, 0.28), 0.34),
                new GradientStop(Shade.Alpha(Accent, 0), 1),
            },
        };
        foreach (var e in _embers) e.Dot.Fill = brush;
    }

    private void Tick()
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 40 || h < 40 || !IsEffectivelyVisible) return;
        _t += FrameSeconds;
        foreach (var e in _embers)
        {
            var u = (_t * e.Speed + e.Phase) % 1.0;
            var life = Math.Sin(Math.PI * u);
            var flicker = 0.7 + 0.3 * Math.Sin(_t * 6 + e.Phase * 50);
            e.Move.X = e.X * w + Math.Sin(_t * 0.6 + e.Phase * 40) * e.Drift - e.Size / 2;
            e.Move.Y = h * (1.03 - u * 1.08) - e.Size / 2;
            e.Dot.Opacity = life * life * flicker * 0.9;
        }
    }
}
