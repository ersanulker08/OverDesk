using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Overdesk.Tools.Shared;

/// <summary>Small building blocks the bundled tools share, styled with OverDesk's own classes. Linked into each tool's assembly.</summary>
internal static class ToolUi
{
    public static Geometry? Icon(string key) => Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null;

    public static TextBlock Eyebrow(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 9.5, VerticalAlignment = VerticalAlignment.Center };
        block.Classes.Add("eyebrow");
        return block;
    }

    public static Button SmallButton(string text, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(9, 3), FontSize = 11.5, Margin = new Thickness(6, 0, 0, 0) };
        button.Classes.Add("ghost");
        button.Click += (_, _) => click();
        return button;
    }

    public static Button AccentButton(string text, Action click)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 9) };
        button.Classes.Add("accent");
        button.Click += (_, _) => click();
        return button;
    }

    public static Button IconButton(string icon, string tip, Action click)
    {
        var path = new Path { Data = Icon(icon), Height = 12, Stretch = Stretch.Uniform };
        path.Classes.Add("icon");
        path.Classes.Add("small");
        var button = new Button { Width = 28, Height = 28, Content = path };
        button.Classes.Add("icon");
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A pill that looks selected while <paramref name="selected"/> is true (the AI model chip style).</summary>
    public static Button Chip(string text, bool selected, Action click)
    {
        var chip = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) };
        chip.Classes.Add("modelchip");
        chip.Classes.Set("selected", selected);
        chip.Click += (_, _) => click();
        return chip;
    }

    /// <summary>A labelled slider row: "Label  [-----o---]  1.5".</summary>
    public static Grid SliderRow(string label, Slider slider, string format = "0.##", double labelWidth = 108)
    {
        var value = new TextBlock { FontSize = 12, Width = 38, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        void Show() => value.Text = slider.Value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        slider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Show(); };
        Show();
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    /// <summary>The round accent badge with an icon used in every tool's empty state.</summary>
    public static Border Badge(Control owner, string icon)
        => new()
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Center,
            [!Border.BackgroundProperty] = owner.GetResourceObservable("AccentGradientDiagonal").ToBinding(),
            Child = new Path { Data = Icon(icon), Stroke = Brushes.White, StrokeThickness = 1.8, Height = 24, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };

    /// <summary>Show a file (or folder) in Finder / Explorer.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", new[] { "-R", path });
            else if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception) { /* nothing to show it with */ }
    }

    public static string FormatSize(long bytes)
        => bytes switch { >= 1 << 30 => $"{bytes / (double)(1 << 30):0.0} GB", >= 1 << 20 => $"{bytes / (double)(1 << 20):0.0} MB", >= 1 << 10 => $"{bytes / 1024} KB", _ => $"{bytes} B" };

    /// <summary>A free path: "name.ext", else "name (2).ext"… An empty extension names a folder.</summary>
    public static string FreePath(string folder, string name, string ext)
    {
        var dot = ext.Length > 0 ? "." + ext : "";
        var path = System.IO.Path.Combine(folder, name + dot);
        for (var i = 2; File.Exists(path) || Directory.Exists(path); i++) path = System.IO.Path.Combine(folder, $"{name} ({i}){dot}");
        return path;
    }
}
