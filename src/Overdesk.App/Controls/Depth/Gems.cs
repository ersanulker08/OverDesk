using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// A card's emblem: a small glossy tile with real thickness (a darker base under a lit, gradient top) and the
/// card's icon engraved in white on it. Sits before every dock card title.
/// </summary>
public sealed class IconGem3D : Control
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<IconGem3D, string?>(nameof(Icon));
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<IconGem3D, Color>(nameof(Color), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<Color> Color2Property =
        AvaloniaProperty.Register<IconGem3D, Color>(nameof(Color2), Color.Parse("#FF6A3D"));

    private const double Base = 2.5;

    static IconGem3D()
    {
        AffectsRender<IconGem3D>(IconProperty, ColorProperty, Color2Property);
        WidthProperty.OverrideDefaultValue<IconGem3D>(20);
        HeightProperty.OverrideDefaultValue<IconGem3D>(20 + Base);
    }

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public Color Color2 { get => GetValue(Color2Property); set => SetValue(Color2Property, value); }

    public override void Render(DrawingContext context)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height - Base);
        if (s < 6) return;
        var r = s * 0.3;
        var top = new Rect(0, 0, s, s);

        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), null, new RoundedRect(new Rect(0.5, Base + 1.5, s - 1, s), r),
            new BoxShadows(new BoxShadow { OffsetY = 2, Blur = 6, Color = Color.FromArgb(0xA0, 0, 0, 0) }));
        context.DrawRectangle(new SolidColorBrush(Shade.Scale(Color, 0.42)), null, new RoundedRect(new Rect(0, Base, s, s), r));
        context.DrawRectangle(Shade.Linear(new Point(0, 0), new Point(s, s), Shade.Mix(Color2, Colors.White, 0.12), Shade.Scale(Color, 0.9)),
            null, new RoundedRect(top, r));
        // Gloss across the upper half and a bright rim on the top edge.
        context.DrawRectangle(Shade.Linear(new Point(0, 0), new Point(0, s * 0.55), Color.FromArgb(0x55, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)),
            null, new RoundedRect(new Rect(1, 1, s - 2, s * 0.55), r * 0.8));
        context.DrawRectangle(null, new Pen(Shade.Linear(new Point(0, 0), new Point(0, s), Color.FromArgb(0x90, 255, 255, 255), Color.FromArgb(0x10, 255, 255, 255)), 0.8),
            new RoundedRect(top.Deflate(0.4), r));

        if (Icon is null || Res.Icon(Icon) is not { } icon) return;
        var b = icon.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        var k = s * 0.52 / Math.Max(b.Width, b.Height);
        var m = Matrix.CreateTranslation(-b.X - b.Width / 2, -b.Y - b.Height / 2) * Matrix.CreateScale(k, k) * Matrix.CreateTranslation(s / 2, s / 2);
        // Engraved: a dark offset copy, then the white stroke.
        using (context.PushTransform(m * Matrix.CreateTranslation(0, 0.8)))
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 1.9 / k, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), icon);
        using (context.PushTransform(m))
            context.DrawGeometry(null, new Pen(Brushes.White, 1.7 / k, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), icon);
    }
}

/// <summary>
/// A status light: a small glass bead lit from the upper left with a specular spot. A live light also glows
/// around itself and slowly breathes.
/// </summary>
public sealed class Led3D : Panel
{
    private readonly Ellipse _halo = new() { Classes = { "ledhalo" }, Width = 16, Height = 16, IsHitTestVisible = false };
    private readonly Bead _bead = new() { Width = 8, Height = 8 };

    public Led3D(Color color, bool live)
    {
        Width = Height = 16;
        Margin = new Thickness(-4, -4, -4, -4);
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _bead.Color = color;
        _bead.Live = live;
        if (live)
        {
            _halo.Fill = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Shade.Alpha(color, 0.8), 0), new GradientStop(Shade.Alpha(color, 0), 1) },
            };
            _halo.Classes.Add("live");
            Children.Add(_halo);
        }
        Children.Add(_bead);
    }

    private sealed class Bead : Control
    {
        public Color Color { get; set; }
        public bool Live { get; set; }

        public override void Render(DrawingContext context)
        {
            var r = Bounds.Width / 2;
            var c = new Point(r, r);
            var dark = Shade.Scale(Color, Live ? 0.45 : 0.3);
            context.DrawEllipse(new RadialGradientBrush
            {
                Center = new RelativePoint(0.38, 0.32, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.38, 0.32, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.7, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Shade.Mix(Color, Colors.White, Live ? 0.55 : 0.2), 0),
                    new GradientStop(Live ? Color : Shade.Scale(Color, 0.6), 0.5),
                    new GradientStop(dark, 1),
                },
            }, new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 0.6), c, r, r);
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(Live ? (byte)0xE0 : (byte)0x70, 255, 255, 255)), null,
                new Point(r * 0.72, r * 0.62), r * 0.24, r * 0.17);
        }
    }
}
