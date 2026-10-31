using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Overdesk.App.Theme;

/// <summary>A named pair of accent colors; the second one ends every accent gradient.</summary>
public sealed record AccentTheme(string Name, Color Primary, Color Secondary);

/// <summary>
/// The accent color presets and the code that swaps them at runtime. Every accent-colored brush in the UI
/// is a DynamicResource, so replacing the resources here recolors the whole app instantly.
/// </summary>
public static class AccentThemes
{
    public static readonly IReadOnlyList<AccentTheme> All = new[]
    {
        new AccentTheme("Crimson", Color.Parse("#FA1E4E"), Color.Parse("#FF6A3D")),
        new AccentTheme("Ultraviolet", Color.Parse("#8B5CF6"), Color.Parse("#E040FB")),
        new AccentTheme("Cyber", Color.Parse("#00D1FF"), Color.Parse("#3B6CFF")),
        new AccentTheme("Toxic", Color.Parse("#2BFF88"), Color.Parse("#00BFA6")),
        new AccentTheme("Amber", Color.Parse("#FFB020"), Color.Parse("#FF5A1F")),
        new AccentTheme("Sakura", Color.Parse("#FF4FA3"), Color.Parse("#B44CFF")),
    };

    public static AccentTheme Current { get; private set; } = All[0];

    public static event Action? Changed;

    public static AccentTheme Find(string? name)
        => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static void Apply(string? name)
    {
        if (Application.Current is not { } app) return;
        var theme = Find(name);
        Current = theme;
        var res = app.Resources;
        var a = theme.Primary;
        var b = theme.Secondary;

        res["AccentColor"] = a;
        res["Accent2Color"] = b;
        res["AccentBrush"] = new SolidColorBrush(a);
        res["Accent2Brush"] = new SolidColorBrush(b);
        res["AccentSoftBrush"] = new SolidColorBrush(WithAlpha(a, 0x2A));
        res["AccentFaintBrush"] = new SolidColorBrush(WithAlpha(a, 0x14));
        res["AccentBorderBrush"] = new SolidColorBrush(WithAlpha(a, 0x80));
        res["AccentGradient"] = Gradient(a, b, horizontal: true);
        res["AccentGradientDiagonal"] = Gradient(a, b, horizontal: false);
        res["AccentTabGradient"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(WithAlpha(a, 0x00), 0), new GradientStop(WithAlpha(a, 0x30), 1) },
        };
        res["AccentGlow"] = new BoxShadows(new BoxShadow { Blur = 18, Spread = 0, Color = WithAlpha(a, 0x70) });
        res["AccentGlowSmall"] = new BoxShadows(new BoxShadow { Blur = 10, Spread = 0, Color = WithAlpha(a, 0x90) });
        res["AccentGlowLarge"] = new BoxShadows(new BoxShadow { Blur = 40, Spread = 2, Color = WithAlpha(a, 0x55) });
        // The accent as a physical key: a deep-colored wall under the face, plus its drop shadow.
        var deep = Mix(a, Colors.Black, 0.62);
        res["AccentDeepBrush"] = new SolidColorBrush(deep);
        res["AccentKeyShadow"] = new BoxShadows(
            new BoxShadow { OffsetY = 3, Color = deep },
            [new BoxShadow { OffsetY = 6, Blur = 14, Color = WithAlpha(Mix(a, Colors.Black, 0.5), 0x70) }]);
        res["AccentKeyPressedShadow"] = new BoxShadows(new BoxShadow { OffsetY = 1, Color = deep });

        // Fluent controls (toggle switches, focus rings, selection) follow the accent too.
        res["SystemAccentColor"] = a;
        res["SystemAccentColorLight1"] = Mix(a, Colors.White, 0.15);
        res["SystemAccentColorLight2"] = Mix(a, Colors.White, 0.30);
        res["SystemAccentColorLight3"] = Mix(a, Colors.White, 0.45);
        res["SystemAccentColorDark1"] = Mix(a, Colors.Black, 0.15);
        res["SystemAccentColorDark2"] = Mix(a, Colors.Black, 0.30);
        res["SystemAccentColorDark3"] = Mix(a, Colors.Black, 0.45);

        Changed?.Invoke();
    }

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static LinearGradientBrush Gradient(Color a, Color b, bool horizontal) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = horizontal ? new RelativePoint(1, 0, RelativeUnit.Relative) : new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
    };
}
