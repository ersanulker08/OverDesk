using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls;

/// <summary>
/// Slowly flowing ribbon of thin strands with a few neon accent threads, in the spirit of gaming-browser
/// wallpapers. Animates on the compositor's frame clock (capped at ~30 fps) only while visible.
/// </summary>
public sealed class WaveBackground : Control
{
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<WaveBackground, Color>(nameof(Accent), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<Color> Accent2Property =
        AvaloniaProperty.Register<WaveBackground, Color>(nameof(Accent2), Color.Parse("#FF6A3D"));
    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<WaveBackground, bool>(nameof(IsAnimated), true);
    public static readonly StyledProperty<int> StrandCountProperty =
        AvaloniaProperty.Register<WaveBackground, int>(nameof(StrandCount), 56);
    public static readonly StyledProperty<double> IntensityProperty =
        AvaloniaProperty.Register<WaveBackground, double>(nameof(Intensity), 1.0);

    private const double FrameInterval = 1.0 / 30;
    private static readonly double StartPhase = Random.Shared.NextDouble() * 100;

    private double _time = StartPhase;
    private TimeSpan? _lastFrame;
    private double _sinceDraw;
    private bool _frameRequested;

    static WaveBackground()
    {
        AffectsRender<WaveBackground>(AccentProperty, Accent2Property, StrandCountProperty, IntensityProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<WaveBackground>(false);
        // Strands start slightly outside the control; never let them spill over neighbours.
        ClipToBoundsProperty.OverrideDefaultValue<WaveBackground>(true);
    }

    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Color Accent2 { get => GetValue(Accent2Property); set => SetValue(Accent2Property, value); }
    public bool IsAnimated { get => GetValue(IsAnimatedProperty); set => SetValue(IsAnimatedProperty, value); }
    public int StrandCount { get => GetValue(StrandCountProperty); set => SetValue(StrandCountProperty, value); }

    /// <summary>0..1: overall brightness of the strands.</summary>
    public double Intensity { get => GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestFrame();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsAnimatedProperty || change.Property == IsVisibleProperty) RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested || !IsAnimated || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameRequested = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        _frameRequested = false;
        if (!IsAttachedToVisualTree() || !IsAnimated)
        {
            _lastFrame = null;
            return;
        }

        var dt = _lastFrame is { } last ? Math.Min((now - last).TotalSeconds, 0.1) : 0;
        _lastFrame = now;

        if (IsEffectivelyVisible)
        {
            _time += dt;
            _sinceDraw += dt;
            if (_sinceDraw >= FrameInterval)
            {
                _sinceDraw = 0;
                InvalidateVisual();
            }
        }
        RequestFrame();
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 2 || h < 2) return;

        var t = _time;
        var intensity = Math.Clamp(Intensity, 0, 1);
        var accent = Accent;
        var accent2 = Accent2;

        // Soft accent bloom low on the right, where the ribbon is brightest.
        context.DrawRectangle(new RadialGradientBrush
        {
            Center = new RelativePoint(0.78, 0.92, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.78, 0.92, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(AccentThemes.WithAlpha(accent, (byte)(0x26 * intensity)), 0),
                new GradientStop(AccentThemes.WithAlpha(accent, 0), 1),
            },
        }, null, new Rect(0, 0, w, h));

        var count = Math.Max(StrandCount, 8);
        const int steps = 90;
        var neonA = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(AccentThemes.WithAlpha(accent, 0), 0),
                new GradientStop(AccentThemes.WithAlpha(accent, (byte)(0xE0 * intensity)), 0.45),
                new GradientStop(AccentThemes.WithAlpha(accent2, (byte)(0xC0 * intensity)), 0.8),
                new GradientStop(AccentThemes.WithAlpha(accent2, 0), 1),
            },
        };

        for (var i = 0; i < count; i++)
        {
            var u = i / (double)(count - 1);
            var neon = IsNeon(i, count);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (var s = 0; s <= steps; s++)
                {
                    var nx = -0.05 + 1.1 * s / steps;
                    var center = 0.80 - 0.26 * nx + 0.05 * Math.Sin(nx * 2.6 + t * 0.22);
                    var spread = 0.06 + 0.34 * nx * nx;
                    var swirl = 0.035 * Math.Sin(nx * 6.0 + u * 4.2 + t * 0.55) * (0.35 + u)
                                + 0.015 * Math.Sin(nx * 13.0 - t * 0.8 + u * 9.0);
                    var y = (center + (u - 0.5) * spread + swirl) * h;
                    var p = new Point(nx * w, y);
                    if (s == 0) ctx.BeginFigure(p, false);
                    else ctx.LineTo(p);
                }
                ctx.EndFigure(false);
            }

            IPen pen;
            if (neon)
            {
                // Faint wide halo under each neon thread, then the thread itself.
                context.DrawGeometry(null, new Pen(new SolidColorBrush(AccentThemes.WithAlpha(accent, (byte)(0x1C * intensity))), 6), geometry);
                pen = new Pen(neonA, 1.6);
            }
            else
            {
                // Strands near the top edge of the ribbon catch more light.
                var shade = (byte)(26 + 40 * Math.Pow(u, 1.6));
                var alpha = (byte)((70 + 120 * u) * intensity);
                pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, shade, shade, (byte)Math.Min(shade + 12, 255))), 1.0);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private static bool IsNeon(int i, int count)
    {
        var a = (int)(count * 0.28);
        var b = (int)(count * 0.71);
        return i == a || i == a + 2 || i == b || i == count - 4;
    }
}
