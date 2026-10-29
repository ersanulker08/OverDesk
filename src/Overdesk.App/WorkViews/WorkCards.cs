using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Works;
using Shapes = Avalonia.Controls.Shapes;

namespace Overdesk.App;

/// <summary>Cards and rows that show works and templates (home hub and tools panel).</summary>
internal static class WorkCards
{
    /// <summary>A big card: icon, title in the work's own font, a few lines of content, progress and age.</summary>
    public static Button WorkCard(WorkService svc, Work work, Action<Work> open, Action<Work, Control> menu)
    {
        var theme = AccentThemes.Find(work.Color);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(16, 16, 16, 14) };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        top.Children.Add(WorkStyle.IconTile(work, 38));
        if (work.Pinned)
        {
            var pin = new Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconPin"), Stroke = new SolidColorBrush(theme.Primary) };
            Grid.SetColumn(pin, 2);
            top.Children.Add(pin);
        }
        grid.Children.Add(top);

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(work.Title) ? "Untitled" : work.Title, FontFamily = svc.TitleFont(work), FontSize = 23,
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 14, 0, 4), LineHeight = 27,
        };
        Grid.SetRow(title, 1);
        grid.Children.Add(title);

        var summary = new TextBlock
        {
            Text = work.Summary, FontFamily = svc.BodyFont(work), FontSize = 12.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
            MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(summary, 2);
        grid.Children.Add(summary);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(new TextBlock { Text = WorkTemplates.Find(work.Template).Name, Classes = { "faint" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (work.TodoTotal > 0)
            right.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2), Background = new SolidColorBrush(AccentThemes.WithAlpha(theme.Primary, 0x26)),
                Child = new TextBlock { Text = $"{work.TodoDone}/{work.TodoTotal}", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(theme.Primary) },
            });
        right.Children.Add(new TextBlock { Text = WorkService.Ago(work.Updated), Classes = { "faint" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        // The work's color washes in from the top-left corner.
        var wash = new Border
        {
            IsHitTestVisible = false, CornerRadius = new CornerRadius(18),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.9, 0.9, RelativeUnit.Relative),
                GradientStops = { new GradientStop(AccentThemes.WithAlpha(theme.Primary, 0x30), 0), new GradientStop(AccentThemes.WithAlpha(theme.Primary, 0), 0.7) },
            },
        };
        var content = new Grid();
        content.Children.Add(wash);
        content.Children.Add(grid);

        var button = new Button { Classes = { "wcard" }, Width = 244, Height = 184, Margin = new Thickness(0, 0, 16, 16), Content = content, ClipToBounds = true };
        button.Click += (_, _) => open(work);
        button.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Right) menu(work, button);
        };
        return button;
    }

    /// <summary>The dashed "New work" card.</summary>
    public static Button NewCard(Action onClick)
    {
        var plus = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22), HorizontalAlignment = HorizontalAlignment.Center,
            Background = Res.Brush("AccentFaintBrush"), BorderBrush = Res.Brush("AccentBorderBrush"), BorderThickness = new Thickness(1),
            Child = new Shapes.Path { Classes = { "icon", "accent" }, Data = Res.Icon("IconPlus"), Height = 18 },
        };
        var button = new Button
        {
            Classes = { "wcard" }, Width = 244, Height = 184, Margin = new Thickness(0, 0, 16, 16),
            Content = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, Spacing = 10,
                Children =
                {
                    plus,
                    new TextBlock { Text = "New work", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Pick a template", Classes = { "faint" }, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A template in the gallery: tile, name in its own font, one line of description.</summary>
    public static Button TemplateCard(WorkTemplate template, Action<WorkTemplate> pick)
    {
        var sample = template.Create();
        var theme = AccentThemes.Find(template.Color);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(14, 12) };
        grid.Children.Add(WorkStyle.IconTile(sample, 40));
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = template.Name, FontFamily = WorkFonts.Resolve(template.TitleFont ?? "Playfair Display"), FontSize = 17,
            FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
        });
        text.Children.Add(new TextBlock { Text = template.Description, Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var wash = new Border
        {
            IsHitTestVisible = false, CornerRadius = new CornerRadius(18),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(AccentThemes.WithAlpha(theme.Primary, 0x26), 0), new GradientStop(AccentThemes.WithAlpha(theme.Primary, 0), 0.65) },
            },
        };
        var content = new Grid();
        content.Children.Add(wash);
        content.Children.Add(grid);
        var button = new Button { Classes = { "wcard" }, Width = 244, Height = 108, Margin = new Thickness(0, 0, 16, 14), Content = content, ClipToBounds = true };
        button.Click += (_, _) => pick(template);
        return button;
    }

    /// <summary>A compact list row for the tools panel.</summary>
    public static Button WorkRow(WorkService svc, Work work, Action<Work> open, Action<Work, Control> menu)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(WorkStyle.IconTile(work, 34));
        var text = new StackPanel { Margin = new Thickness(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(work.Title) ? "Untitled" : work.Title, FontFamily = svc.TitleFont(work), FontSize = 17,
            FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var sub = work.Summary.Length > 0 ? work.Summary : WorkTemplates.Find(work.Template).Name;
        text.Children.Add(new TextBlock { Text = sub, Classes = { "faint" }, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var right = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        if (work.TodoTotal > 0)
            right.Children.Add(new TextBlock { Text = $"{work.TodoDone}/{work.TodoTotal}", FontSize = 11, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Res.Brush("AccentBrush") });
        right.Children.Add(new TextBlock { Text = WorkService.Ago(work.Updated), Classes = { "faint" }, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        var button = new Button { Classes = { "wrow" }, Content = grid };
        button.Click += (_, _) => open(work);
        button.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Right) menu(work, button);
        };
        return button;
    }

    /// <summary>The right-click menu of a work.</summary>
    public static void ShowMenu(WorkService svc, Work work, Control anchor, bool includePanel = true)
    {
        var items = new List<Control>
        {
            Entry("Open as a tab", "IconExternal", () => svc.OpenInTab(work)),
        };
        if (includePanel) items.Add(Entry("Open in the side panel", "IconPanel", () => svc.OpenInPanel(work)));
        items.Add(Entry(work.Pinned ? "Unpin" : "Pin to the top", "IconPin", () =>
        {
            work.Pinned = !work.Pinned;
            svc.Library.Touch(work, listChanged: true);
        }));
        items.Add(Entry("Duplicate", "IconCopy", () => svc.Library.Duplicate(work)));
        items.Add(new Separator());
        var delete = Entry("Delete…", "IconTrash", () => AskDelete(svc, work, anchor));
        delete.Classes.Add("danger");
        items.Add(delete);
        new ContextMenu { ItemsSource = items }.Open(anchor);
    }

    public static void AskDelete(WorkService svc, Work work, Control anchor)
    {
        var yes = new Button { Content = "Delete", Classes = { "accent" }, Padding = new Thickness(14, 6) };
        var no = new Button { Content = "Keep", Classes = { "ghost" }, Padding = new Thickness(14, 6) };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 10, Width = 230,
                Children =
                {
                    new TextBlock { Text = $"Delete “{(string.IsNullOrWhiteSpace(work.Title) ? "Untitled" : work.Title)}” forever?", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "This cannot be undone.", Classes = { "muted" }, FontSize = 12 },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { yes, no } },
                },
            },
        };
        yes.Click += (_, _) =>
        {
            flyout.Hide();
            svc.Library.Delete(work);
        };
        no.Click += (_, _) => flyout.Hide();
        flyout.ShowAt(anchor);
    }

    private static MenuItem Entry(string header, string icon, Action onClick)
    {
        var item = new MenuItem { Header = header, Icon = new Shapes.Path { Classes = { "icon" }, Data = Res.Icon(icon) } };
        item.Click += (_, _) => onClick();
        return item;
    }

    public static bool Matches(Work work, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        return work.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
               || work.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)
               || WorkTemplates.Find(work.Template).Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
