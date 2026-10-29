using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Overdesk.App.Theme;
using Overdesk.Core.Works;
using Shapes = Avalonia.Controls.Shapes;

namespace Overdesk.App;

/// <summary>Look shared by every place that draws a work: its color, icon tile and per-work accent.</summary>
internal static class WorkStyle
{
    public static readonly string[] Icons =
    {
        "IconNote", "IconBook", "IconCheck", "IconRocket", "IconSparkle", "IconGlobe", "IconGauge", "IconPin",
        "IconBox", "IconBell", "IconBranch", "IconTimer", "IconMusic", "IconCamera", "IconKey", "IconShield",
    };

    public static Geometry Icon(string? key)
        => Application.Current is { } app && key is not null && app.TryGetResource(key, Avalonia.Styling.ThemeVariant.Dark, out var v) && v is Geometry g
            ? g
            : Res.Icon("IconNote");

    /// <summary>A rounded gradient tile with the work's icon in it.</summary>
    public static Control IconTile(Work work, double size)
    {
        var theme = AccentThemes.Find(work.Color);
        var radius = new CornerRadius(size * 0.3);
        // The glow is its own empty border: a border with a shadow would not draw its children in snapshots.
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Border
        {
            CornerRadius = radius,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = size * 0.45, Color = AccentThemes.WithAlpha(theme.Primary, 0x66) }),
        });
        grid.Children.Add(new Border
        {
            CornerRadius = radius,
            Background = AccentThemes.Gradient(theme.Primary, theme.Secondary, horizontal: false),
            Child = new Shapes.Path { Classes = { "icon" }, Data = Icon(work.Icon), Stroke = Brushes.White, Height = size * 0.5 },
        });
        return grid;
    }

    /// <summary>Make every accent-colored control below <paramref name="root"/> use this work's color instead of the app's.</summary>
    public static void ApplyAccent(Control root, AccentTheme t)
    {
        var a = t.Primary;
        var b = t.Secondary;
        var res = root.Resources;
        res["AccentColor"] = a;
        res["Accent2Color"] = b;
        res["AccentBrush"] = new SolidColorBrush(a);
        res["Accent2Brush"] = new SolidColorBrush(b);
        res["AccentSoftBrush"] = new SolidColorBrush(AccentThemes.WithAlpha(a, 0x2A));
        res["AccentFaintBrush"] = new SolidColorBrush(AccentThemes.WithAlpha(a, 0x14));
        res["AccentBorderBrush"] = new SolidColorBrush(AccentThemes.WithAlpha(a, 0x80));
        res["AccentGradient"] = AccentThemes.Gradient(a, b, horizontal: true);
        res["AccentGradientDiagonal"] = AccentThemes.Gradient(a, b, horizontal: false);
        res["SystemAccentColor"] = a;
        res["SystemAccentColorLight1"] = AccentThemes.Mix(a, Colors.White, 0.15);
        res["SystemAccentColorLight2"] = AccentThemes.Mix(a, Colors.White, 0.30);
        res["SystemAccentColorLight3"] = AccentThemes.Mix(a, Colors.White, 0.45);
        res["SystemAccentColorDark1"] = AccentThemes.Mix(a, Colors.Black, 0.15);
        res["SystemAccentColorDark2"] = AccentThemes.Mix(a, Colors.Black, 0.30);
        res["SystemAccentColorDark3"] = AccentThemes.Mix(a, Colors.Black, 0.45);
    }

    /// <summary>A soft glow of the work's color at the top of a page.</summary>
    public static IBrush Haze(AccentTheme t) => new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        RadiusX = RelativeScalar.Parse("75%"), RadiusY = RelativeScalar.Parse("45%"),
        GradientStops = { new GradientStop(AccentThemes.WithAlpha(t.Primary, 0x2E), 0), new GradientStop(AccentThemes.WithAlpha(t.Primary, 0), 1) },
    };
}
