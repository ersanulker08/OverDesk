using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// A dock widget as a physical slab instead of a flat panel: a lit face with a chamfered top edge, a visible
/// side wall underneath (the slab's thickness), an accent underglow and a soft contact shadow. Under the pointer
/// the slab lifts and tilts toward it in real perspective while a glare follows the pointer across the face.
/// </summary>
public class DepthCard : Decorator
{
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<DepthCard, Color>(nameof(Accent), Color.Parse("#FA1E4E"));

    /// <summary>Thickness of the slab in pixels (the side wall below the face).</summary>
    public const double Thickness = 6;
    private const double MaxTiltX = 5.5, MaxTiltY = 7;

    private Point _pointer = new(0.5, 0.3);
    private double _hover, _hoverTarget;
    private double _tiltX, _tiltY, _tiltXTarget, _tiltYTarget;
    private double _flash;
    private double _enter = 1;      // 0 = still below and tipped back, 1 = in place
    private double _enterDelay;
    private bool _animating;

    static DepthCard()
    {
        AffectsRender<DepthCard>(AccentProperty, RadiusProperty);
        PaddingProperty.OverrideDefaultValue<DepthCard>(new Thickness(0, 0, 0, Thickness));
    }

    public DepthCard()
    {
        RenderTransformOrigin = RelativePoint.Center;
    }

    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<DepthCard, double>(nameof(Radius), 16);

    /// <summary>False for a big plate that holds a working view: no tilt, lift or glare under the pointer.</summary>
    public static readonly StyledProperty<bool> TiltProperty =
        AvaloniaProperty.Register<DepthCard, bool>(nameof(Tilt), true);

    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public bool Tilt { get => GetValue(TiltProperty); set => SetValue(TiltProperty, value); }

    /// <summary>Development snapshots: show the card as if the pointer rested at (x, y), 0..1 of its size.</summary>
    internal void DevHover(double x, double y)
    {
        _pointer = new Point(x, y);
        _hover = _hoverTarget = 1;
        _tiltX = _tiltXTarget = -(y - 0.5) * 2 * MaxTiltX;
        _tiltY = _tiltYTarget = (x - 0.5) * 2 * MaxTiltY;
        Apply();
    }

    /// <summary>
    /// Rise into place: the slab comes up from below, tipped back, and settles flat. A short delay per card
    /// makes a column of them arrive one after another.
    /// </summary>
    public void Enter(double delaySeconds)
    {
        _enter = 0;
        _enterDelay = delaySeconds;
        Opacity = 0;
        Animate();
    }

    /// <summary>A pulse of accent light around the slab (the focus timer finishing, for example).</summary>
    public void Flash()
    {
        _flash = 1;
        Animate();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (!Tilt) return;
        _hoverTarget = 1;
        Animate();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!Tilt) return;
        var p = e.GetPosition(this);
        var w = Math.Max(Bounds.Width, 1);
        var h = Math.Max(Bounds.Height - Thickness, 1);
        _pointer = new Point(Math.Clamp(p.X / w, 0, 1), Math.Clamp(p.Y / h, 0, 1));
        // Tall cards (the open player) tilt less vertically, or the far edge would swing too much.
        var tall = Math.Clamp(160 / h, 0.35, 1);
        _tiltXTarget = -(_pointer.Y - 0.5) * 2 * MaxTiltX * tall;
        _tiltYTarget = (_pointer.X - 0.5) * 2 * MaxTiltY;
        Animate();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverTarget = 0;
        _tiltXTarget = _tiltYTarget = 0;
        Animate();
    }

    private void Animate()
    {
        if (_animating) return;
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            _hover = _hoverTarget; _tiltX = _tiltXTarget; _tiltY = _tiltYTarget; _flash = 0; _enter = 1;
            Apply();
            return;
        }
        _animating = true;
        TimeSpan? last = null;
        void Frame(TimeSpan now)
        {
            var dt = last is { } l ? Math.Min((now - l).TotalSeconds, 0.05) : 1 / 60.0;
            last = now;
            // Critically damped-ish follow: quick but never springy.
            var k = 1 - Math.Exp(-dt * 12);
            _hover += (_hoverTarget - _hover) * k;
            _tiltX += (_tiltXTarget - _tiltX) * k;
            _tiltY += (_tiltYTarget - _tiltY) * k;
            _flash = Math.Max(0, _flash - dt * 1.1);
            if (_enter < 1)
            {
                if (_enterDelay > 0) _enterDelay -= dt;
                else _enter = Math.Min(1, _enter + dt / 0.55);
            }
            Apply();
            var settled = Math.Abs(_hoverTarget - _hover) < 0.002 && Math.Abs(_tiltXTarget - _tiltX) < 0.01
                          && Math.Abs(_tiltYTarget - _tiltY) < 0.01 && _flash <= 0 && _enter >= 1;
            if (settled) { _animating = false; Apply(); return; }
            top.RequestAnimationFrame(Frame);
        }
        top.RequestAnimationFrame(Frame);
    }

    private void Apply()
    {
        var e = Ease.OutCubic(_enter);
        var lift = -2.5 * _hover + 28 * (1 - e);
        var tipX = _tiltX + 24 * (1 - Ease.OutBack(_enter));
        if (_enter < 1) Opacity = Math.Clamp(_enter * 1.6, 0, 1);
        else ClearValue(OpacityProperty);
        if (Math.Abs(tipX) < 0.01 && Math.Abs(_tiltY) < 0.01 && Math.Abs(lift) < 0.01)
            RenderTransform = null;
        else
            RenderTransform = new TransformGroup
            {
                Children =
                {
                    new Rotate3DTransform(tipX, _tiltY, 0, 0, 0, 0, 900),
                    new TranslateTransform(0, lift),
                },
            };
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height - Thickness;
        if (w < 4 || h < 4) return;
        var face = new Rect(0, 0, w, h);
        var accent = Accent;
        var Radius = this.Radius;

        // Contact shadow on the dock floor, deeper when lifted, plus the accent pulse.
        var shadow = new BoxShadows(
            new BoxShadow { OffsetY = 10 + 6 * _hover, Blur = 22 + 10 * _hover, Color = Color.FromArgb((byte)(150 + 40 * _hover), 0, 0, 0) },
            _flash > 0 || _hover > 0.01
                ? new[] { new BoxShadow { Blur = 18 + 16 * _flash, Spread = 0, Color = Shade.Alpha(accent, 0.22 * _hover + 0.75 * _flash) } }
                : Array.Empty<BoxShadow>());
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(6, 6, 9)), null,
            new RoundedRect(new Rect(0, Thickness, w, h), Radius), shadow);

        // Side wall: the face shifted down by the slab thickness, dark with a lit lower lip and accent underglow.
        var wall = new RoundedRect(new Rect(0, Thickness * 0.15, w, h + Thickness * 0.85), Radius);
        context.DrawRectangle(Shade.Linear(new Point(0, h - Radius), new Point(0, h + Thickness),
            Color.FromRgb(0x0B, 0x0B, 0x10), Shade.Mix(Color.FromRgb(0x18, 0x14, 0x1C), accent, 0.12 + 0.15 * _hover)), null, wall);
        context.DrawRectangle(null, new Pen(Shade.Linear(new Point(0, h), new Point(w, h),
            Shade.Alpha(accent, 0), Shade.Alpha(accent, 0.35 + 0.35 * _hover)), 1), wall);

        // Face: lit from the top left, with the glare (pointer light) mixed in.
        var top = Shade.Mix(Color.FromRgb(0x1D, 0x1D, 0x27), Color.FromRgb(0x24, 0x22, 0x2E), _hover);
        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(w * 0.35, h, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(top, 0),
                new GradientStop(Color.FromRgb(0x15, 0x15, 0x1D), 0.55),
                new GradientStop(Color.FromRgb(0x10, 0x10, 0x16), 1),
            },
        }, null, new RoundedRect(face, Radius));

        // Faint machined texture: fine horizontal lines that catch the light near the top, kept inside the corners.
        // (No PushClip with a rounded rect here: in 2x RenderTargetBitmap renders it displaced the children.)
        for (var y = 3.0; y < Math.Min(h, 70); y += 3)
        {
            var inset = y < Radius ? Radius - Math.Sqrt(Radius * Radius - (Radius - y) * (Radius - y)) + 1 : 1;
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(7 * (1 - y / 70)), 255, 255, 255)), 1), new Point(inset, y), new Point(w - inset, y));
        }

        // Molded surface: light pooling along the top, ambient occlusion where the face turns down into the wall.
        var poolH = Math.Min(h * 0.5, 44);
        context.DrawRectangle(Shade.Linear(new Point(0, 0), new Point(0, poolH),
            Color.FromArgb(0x1E, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)), null,
            new RoundedRect(new Rect(1, 1, w - 2, poolH), Radius - 1));
        var aoH = Math.Min(h * 0.4, 56);
        context.DrawRectangle(Shade.Linear(new Point(0, h - aoH), new Point(0, h),
            Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0x58, 0, 0, 0)), null,
            new RoundedRect(new Rect(1, h - aoH, w - 2, aoH - 1), Radius - 1));

        if (_hover > 0.01)
        {
            // Glare: the face filled again with a radial light centered on the pointer.
            var g = new Point(_pointer.X * w, _pointer.Y * h);
            context.DrawRectangle(new RadialGradientBrush
            {
                Center = new RelativePoint(g, RelativeUnit.Absolute),
                GradientOrigin = new RelativePoint(g, RelativeUnit.Absolute),
                RadiusX = new RelativeScalar(w * 0.75, RelativeUnit.Absolute),
                RadiusY = new RelativeScalar(w * 0.75, RelativeUnit.Absolute),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(34 * _hover), 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb((byte)(10 * _hover), 255, 255, 255), 0.4),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            }, null, new RoundedRect(face, Radius));
        }

        // Chamfer: a bright edge along the top, fading down the sides; a dark seam where face meets wall.
        context.DrawRectangle(null, new Pen(Shade.Linear(new Point(0, 0), new Point(0, h * 0.6),
            Color.FromArgb((byte)(58 + 30 * _hover), 255, 255, 255), Color.FromArgb(0, 255, 255, 255)), 1),
            new RoundedRect(face.Deflate(0.5), Radius - 0.5));
        context.DrawRectangle(null, new Pen(Shade.Linear(new Point(0, h * 0.6), new Point(0, h),
            Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xB0, 0, 0, 0)), 1), new RoundedRect(face.Deflate(0.5), Radius - 0.5));
    }
}
