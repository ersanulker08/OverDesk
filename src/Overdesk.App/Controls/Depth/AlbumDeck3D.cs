using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls.Depth;

/// <summary>
/// The album as an object: the cover stands turned toward the card in perspective, a vinyl record slides out of it
/// to the right and spins while the music plays (its grooves turn, the light on it stays put), and the cover is
/// mirrored in the glossy surface below. Without artwork the cover is the accent gradient with a note.
/// </summary>
public sealed class AlbumDeck3D : Panel
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<AlbumDeck3D, Bitmap?>(nameof(Source));
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<AlbumDeck3D, bool>(nameof(IsPlaying));
    public static readonly StyledProperty<double> CoverSizeProperty =
        AvaloniaProperty.Register<AlbumDeck3D, double>(nameof(CoverSize), 46);
    public static readonly StyledProperty<bool> ReflectionProperty =
        AvaloniaProperty.Register<AlbumDeck3D, bool>(nameof(Reflection));

    private readonly Vinyl _vinyl = new();
    private readonly Border _cover = new() { ClipToBounds = true };
    private readonly Border _gloss = new() { IsHitTestVisible = false };
    private readonly Border _mirror = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Image _mirrorImage = new() { Stretch = Stretch.UniformToFill, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                                                   RenderTransform = new ScaleTransform(1, -1) };
    private readonly Avalonia.Controls.Shapes.Path _note = new() { Classes = { "icon" }, Stroke = Brushes.White };
    private readonly DispatcherTimer _spin = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private double _slide; // 0 = record inside the sleeve, 1 = out
    private DateTime _last = DateTime.UtcNow;

    public AlbumDeck3D()
    {
        _note.Data = Res.Icon("IconMusic");
        _cover.Child = new Panel { Children = { _note, _gloss } };
        Children.Add(_vinyl);
        Children.Add(_mirror);
        Children.Add(_cover);
        _spin.Tick += (_, _) => Step();
        AttachedToVisualTree += (_, _) => Restyle();
        DetachedFromVisualTree += (_, _) => _spin.Stop();
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public double CoverSize { get => GetValue(CoverSizeProperty); set => SetValue(CoverSizeProperty, value); }
    public bool Reflection { get => GetValue(ReflectionProperty); set => SetValue(ReflectionProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == CoverSizeProperty || change.Property == ReflectionProperty) Restyle();
        if (change.Property == IsPlayingProperty || change.Property == SourceProperty) UpdateSpin();
    }

    private void UpdateSpin()
    {
        _last = DateTime.UtcNow;
        if (IsPlaying || _slide > 0) _spin.Start();
        // Snapshots and the first frame: show the end state right away.
        if (!_spin.IsEnabled || TopLevel.GetTopLevel(this) is null) _slide = IsPlaying ? 1 : 0;
        _vinyl.Slide = _slide;
        InvalidateArrange();
    }

    private void Step()
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min((now - _last).TotalSeconds, 0.1);
        _last = now;
        _slide = Math.Clamp(_slide + (IsPlaying ? dt : -dt) * 2.2, 0, 1);
        if (IsPlaying) _vinyl.Angle = (_vinyl.Angle + dt * 200) % 360; // 33⅓ rpm
        if (!IsPlaying && _slide <= 0) _spin.Stop();
        if (!IsEffectivelyVisible) return; // the folded player or a hidden dock: keep time, skip drawing
        _vinyl.Slide = _slide;
        _vinyl.InvalidateVisual();
        if (_slide is > 0 and < 1) InvalidateArrange();
    }

    private void Restyle()
    {
        var size = CoverSize;
        _vinyl.Label = Source;
        _cover.Width = _cover.Height = size;
        _cover.CornerRadius = new CornerRadius(Math.Max(4, size * 0.07));
        _cover.Background = Source is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : this.FindResource("AccentGradientDiagonal") as IBrush;
        _note.IsVisible = Source is null;
        _note.Height = size * 0.4;
        _cover.BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 6, OffsetY = size * 0.12, Blur = size * 0.35, Color = Color.FromArgb(0xC0, 0, 0, 0) });
        // Sleeve gloss: a diagonal sheen and a bright top edge.
        _gloss.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x40, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0x08, 255, 255, 255), 0.45),
                new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0x30, 0, 0, 0), 1),
            },
        };
        _gloss.BorderBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x70, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x10, 255, 255, 255), 1) },
        };
        _gloss.BorderThickness = new Thickness(1);
        _gloss.CornerRadius = _cover.CornerRadius;
        // Turned toward the record, in perspective.
        _cover.RenderTransform = new Rotate3DTransform(0, -24, 0, 0, 0, 0, size * 7);

        _mirror.IsVisible = Reflection;
        _mirror.Width = size;
        _mirror.Height = size * 0.38;
        _mirror.CornerRadius = new CornerRadius(0, 0, 6, 6);
        // The cover upside down, its lower edge touching the cover's: only the top of the flipped image shows.
        _mirrorImage.Source = Source;
        _mirrorImage.Width = _mirrorImage.Height = size;
        _mirror.Child = _mirrorImage;
        _mirror.Background = Source is null ? this.FindResource("AccentGradientDiagonal") as IBrush : null;
        _mirror.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x70, 0, 0, 0), 0), new GradientStop(Colors.Transparent, 0.9) },
        };
        _mirror.RenderTransform = new Rotate3DTransform(0, -24, 0, 0, 0, 0, size * 7);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = CoverSize;
        foreach (var c in Children) c.Measure(new Size(s * 1.6, s * 1.5));
        // Room for the record sliding out (about half its width) and the reflection.
        return new Size(s * 1.5, s * (Reflection ? 1.4 : 1.0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = CoverSize;
        var x = 0.0;
        var top = 0.0;
        var disc = s * 0.94;
        var discX = x + (s - disc) / 2 + s * 0.48 * Ease.OutCubic(_slide);
        _vinyl.Arrange(new Rect(discX, top + (s - disc) / 2, disc, disc));
        _cover.Arrange(new Rect(x, top, s, s));
        _mirror.Arrange(new Rect(x, top + s + 2, s, s * 0.38));
        return finalSize;
    }

    /// <summary>The record: grooves and label turn, the two light streaks across it do not.</summary>
    private sealed class Vinyl : Control
    {
        public Bitmap? Label { get; set; }
        public double Angle { get; set; }
        public double Slide { get; set; }

        public override void Render(DrawingContext context)
        {
            var r = Bounds.Width / 2;
            if (r < 4 || Slide <= 0.01) return;
            var c = new Point(r, r);
            context.DrawEllipse(new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x1A, 0x1A, 0x20), 0),
                    new GradientStop(Color.FromRgb(0x0A, 0x0A, 0x0D), 0.7),
                    new GradientStop(Color.FromRgb(0x16, 0x16, 0x1B), 1),
                },
            }, new Pen(new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)), 0.6), c, r, r);

            // Grooves.
            for (var g = r * 0.38; g < r * 0.96; g += Math.Max(1.6, r / 22))
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), 0.5), c, g, g);

            // Label (the artwork) turning with the record, plus a mark so the turning shows.
            using (context.PushTransform(Matrix.CreateTranslation(-r, -r) * Matrix.CreateRotation(Angle * Math.PI / 180) * Matrix.CreateTranslation(r, r)))
            {
                var lr = r * 0.34;
                IBrush label = Label is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromRgb(0xFA, 0x1E, 0x4E));
                context.DrawEllipse(label, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 1), c, lr, lr);
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), 1), new Point(r, r - r * 0.42), new Point(r, r - r * 0.9));
            }
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A)), null, c, r * 0.05, r * 0.05);

            // Fixed light: two soft bands across the record from the upper left (they do not turn with it).
            context.DrawEllipse(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(2 * r, 2 * r, RelativeUnit.Absolute),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.18),
                    new GradientStop(Color.FromArgb(0x26, 255, 255, 255), 0.3),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.42),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6),
                    new GradientStop(Color.FromArgb(0x14, 255, 255, 255), 0.72),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.84),
                },
            }, null, c, r, r);
        }
    }
}
