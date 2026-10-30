using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// Text with real thickness: the letters are extruded down and to the right in a dark accent tone, the face is lit
/// from above with a bright edge on top. Used for the dock's numbers (gauge values, the focus timer).
/// </summary>
public sealed class Readout3D : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Readout3D, string?>(nameof(Text));
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Readout3D, double>(nameof(Size), 14);
    public static readonly StyledProperty<FontWeight> WeightProperty =
        AvaloniaProperty.Register<Readout3D, FontWeight>(nameof(Weight), FontWeight.Bold);
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<Readout3D, Color>(nameof(Color), Color.Parse("#FA1E4E"));
    public static readonly StyledProperty<double> DepthProperty =
        AvaloniaProperty.Register<Readout3D, double>(nameof(Depth), 3);

    static Readout3D()
    {
        AffectsRender<Readout3D>(TextProperty, SizeProperty, WeightProperty, ColorProperty, DepthProperty);
        AffectsMeasure<Readout3D>(TextProperty, SizeProperty, WeightProperty, DepthProperty);
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public FontWeight Weight { get => GetValue(WeightProperty); set => SetValue(WeightProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Depth { get => GetValue(DepthProperty); set => SetValue(DepthProperty, value); }

    private FormattedText Format(IBrush brush)
    {
        var family = this.TryFindResource("UiFont", out var f) && f is FontFamily ff ? ff : FontFamily.Default;
        return new FormattedText(Text ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyle.Normal, Weight), Size, brush) { };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (string.IsNullOrEmpty(Text)) return default;
        var t = Format(Brushes.White);
        return new Size(t.WidthIncludingTrailingWhitespace + Depth * 0.7 + 1, t.Height + Depth + 1);
    }

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Text)) return;
        // Right-aligned when given more room than needed (values sit on the right edge of their row).
        var probe = Format(Brushes.White);
        var x = Math.Max(0, Bounds.Width - (probe.WidthIncludingTrailingWhitespace + Depth * 0.7 + 1));

        // Extrusion: from the back layer forward, getting lighter toward the face.
        var steps = (int)Math.Ceiling(Depth * 2);
        for (var i = steps; i >= 1; i--)
        {
            var k = i / (double)steps;
            var tone = Shade.Mix(Shade.Scale(Color, 0.55), Color.FromRgb(8, 6, 10), k * 0.85);
            using (context.PushTransform(Matrix.CreateTranslation(x + Depth * 0.7 * k, Depth * k)))
                context.DrawText(Format(new SolidColorBrush(tone)), default);
        }

        // Face: bright at the top, cooling toward the baseline; a thin highlight a hair above.
        var h = probe.Height;
        var face = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, h, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Colors.White, 0.2),
                new GradientStop(Color.FromRgb(0xD9, 0xDB, 0xE6), 0.62),
                new GradientStop(Shade.Mix(Color.FromRgb(0xB8, 0xBA, 0xC8), Color, 0.18), 0.9),
            },
        };
        using (context.PushTransform(Matrix.CreateTranslation(x, -0.5)))
            context.DrawText(Format(new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255))), default);
        using (context.PushTransform(Matrix.CreateTranslation(x, 0)))
            context.DrawText(Format(face), default);
    }
}
