using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Overdesk.App.Theme;

namespace Overdesk.App;

/// <summary>
/// Opening screen: the logo ring draws itself, the window tile pops in, the wordmark settles and a loading bar
/// fills. Driven by one frame-clock timeline so every element stays in sync. Click to skip.
/// </summary>
public partial class SplashWindow : Window
{
    public const double Duration = 2.6;
    private const double FadeOut = 0.35;

    private static readonly (double At, string Text)[] Steps =
    {
        (0.00, "STARTING"),
        (0.55, "LOADING SETTINGS"),
        (1.15, "FINDING YOUR PROGRAMS"),
        (1.85, "PREPARING YOUR DESK"),
        (2.40, "READY"),
    };

    private TimeSpan? _start;
    private double _closingAt = -1;
    private bool _completed;

    public SplashWindow()
    {
        InitializeComponent();
        Version.Text = "v" + (typeof(SplashWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        if (Halo.Fill is RadialGradientBrush halo)
        {
            var accent = AccentThemes.Current.Primary;
            halo.GradientStops[0].Color = AccentThemes.WithAlpha(accent, 0x48);
            halo.GradientStops[1].Color = AccentThemes.WithAlpha(accent, 0);
        }
        Card.RenderTransform = new ScaleTransform(0.96, 0.96);
        Opened += (_, _) => RequestAnimationFrame(OnFrame);
        PointerPressed += OnPointerPressed;
    }

    /// <summary>Raised once the intro has played; the app shows the main window and calls <see cref="FadeAway"/>.</summary>
    public event Action? Completed;

    /// <summary>Elapsed seconds since the splash appeared (used by the dev snapshot tool).</summary>
    public double Elapsed { get; private set; }

    public void FadeAway() => _closingAt = Elapsed;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicking skips the rest of the intro.
        if (_completed) return;
        _start = _start is { } s ? s - TimeSpan.FromSeconds(Math.Max(Duration - Elapsed - 0.15, 0)) : null;
    }

    private void OnFrame(TimeSpan now)
    {
        _start ??= now;
        var t = Elapsed = (now - _start.Value).TotalSeconds;

        Card.Opacity = Ease.OutCubic(Range(t, 0, 0.35));
        var cardScale = 0.96 + 0.04 * Ease.OutCubic(Range(t, 0, 0.45));
        Waves.Opacity = 0.9 * Ease.InOutCubic(Range(t, 0.2, 1.4));

        Logo.Progress = Ease.InOutCubic(Range(t, 0.15, 1.05));
        Logo.TileScale = Ease.OutBack(Range(t, 0.95, 1.4));
        var pulse = t > 1.6 ? 0.12 * Math.Sin((t - 1.6) * 4.0) : 0;
        Logo.Glow = Math.Clamp(0.95 * Ease.OutCubic(Range(t, 0.8, 1.6)) + pulse, 0, 1);
        Halo.Opacity = Ease.OutCubic(Range(t, 0.9, 1.8)) * (0.9 + pulse);

        var word = Ease.OutCubic(Range(t, 1.05, 1.75));
        Wordmark.Opacity = word;
        Wordmark.LetterSpacing = 22 - 13 * word;
        Tagline.Opacity = Ease.OutCubic(Range(t, 1.4, 1.95));

        var bar = Ease.InOutCubic(Range(t, 0.25, Duration - 0.1));
        Bar.Width = 260 * bar;
        Status.Text = Steps.Last(s => t >= s.At).Text;

        if (!_completed && t >= Duration)
        {
            _completed = true;
            Completed?.Invoke();
        }

        if (_closingAt >= 0)
        {
            var f = Ease.InCubic(Range(t, _closingAt, _closingAt + FadeOut));
            Card.Opacity = 1 - f;
            cardScale = 1 + 0.04 * f;
            if (f >= 1)
            {
                Close();
                return;
            }
        }

        Card.RenderTransform = new ScaleTransform(cardScale, cardScale);
        RequestAnimationFrame(OnFrame);
    }

    private static double Range(double t, double from, double to) => Math.Clamp((t - from) / (to - from), 0, 1);
}

/// <summary>Easing curves for code-driven animations.</summary>
internal static class Ease
{
    public static double OutCubic(double x) => 1 - Math.Pow(1 - x, 3);
    public static double InCubic(double x) => x * x * x;
    public static double InOutCubic(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

    public static double OutBack(double x)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }
}
