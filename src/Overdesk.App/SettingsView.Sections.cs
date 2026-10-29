using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Settings;

namespace Overdesk.App;

/// <summary>Settings page sections for the web strip, AI, projects and dock cards.</summary>
public partial class SettingsView
{
    private static readonly (string Id, string Name, string Hint)[] KeyProviders =
    {
        ("anthropic", "Anthropic (Claude)", "console.anthropic.com → API keys"),
        ("openai", "OpenAI", "platform.openai.com → API keys"),
        ("gemini", "Google Gemini", "aistudio.google.com → Get API key"),
    };

    internal AiService? Ai { get; set; }
    internal Func<Task>? AddProject { get; set; }

    private void InitializeSections()
    {
        WebToggle.IsCheckedChanged += (_, _) => Update(s => s.ShowWebStrip = WebToggle.IsChecked == true);
        OllamaUrl.LostFocus += (_, _) => Update(s => s.OllamaUrl = OllamaUrl.Text?.Trim() is { Length: > 0 } u ? u : s.OllamaUrl);
        LmStudioUrl.LostFocus += (_, _) => Update(s => s.LmStudioUrl = LmStudioUrl.Text?.Trim() is { Length: > 0 } u ? u : s.LmStudioUrl);
        SystemPrompt.LostFocus += (_, _) => Update(s => s.AiSystemPrompt = SystemPrompt.Text ?? "");
        AddPromptButton.Click += (_, _) =>
        {
            _settings.Prompts.Add(new SavedPrompt { Name = "New prompt", Text = "{{clipboard}}" });
            BuildPrompts();
            Changed?.Invoke();
        };
        AddProjectButton.Click += async (_, _) =>
        {
            if (AddProject is not null) await AddProject();
            BuildProjects();
        };
        FocusMinutes.ValueChanged += (_, _) => Update(s => s.FocusMinutes = (int)(FocusMinutes.Value ?? 25));
        BreakMinutes.ValueChanged += (_, _) => Update(s => s.BreakMinutes = (int)(BreakMinutes.Value ?? 5));
    }

    private void LoadSections()
    {
        _loading = true;
        WebToggle.IsChecked = _settings.ShowWebStrip;
        OllamaUrl.Text = _settings.OllamaUrl;
        LmStudioUrl.Text = _settings.LmStudioUrl;
        SystemPrompt.Text = _settings.AiSystemPrompt;
        FocusMinutes.Value = _settings.FocusMinutes;
        BreakMinutes.Value = _settings.BreakMinutes;
        _loading = false;
        BuildWebPins();
        BuildWorks();
        BuildKeys();
        BuildPrompts();
        BuildProjects();
        BuildCardChips();
    }

    // ---------------------------------------------------------------- web strip

    private void BuildWorks()
    {
        WorkFontControls.Children.Clear();
        WorkFontControls.Children.Add(new TextBlock { Text = "Title font", FontWeight = FontWeight.SemiBold });
        var title = new FontPickerButton { Selected = _settings.WorkTitleFont };
        title.Chosen += name => Update(s => s.WorkTitleFont = name ?? s.WorkTitleFont);
        WorkFontControls.Children.Add(title);
        WorkFontControls.Children.Add(new TextBlock { Text = "Writing font", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        var body = new FontPickerButton { Selected = _settings.WorkBodyFont };
        body.Chosen += name => Update(s => s.WorkBodyFont = name ?? s.WorkBodyFont);
        WorkFontControls.Children.Add(body);
        var size = new NumericUpDown { Minimum = 11, Maximum = 32, Increment = 1, Width = 130, Value = (decimal)_settings.WorkFontSize, FormatString = "0 'px'", HorizontalAlignment = HorizontalAlignment.Left };
        size.ValueChanged += (_, e) =>
        {
            if (!_loading) Update(s => s.WorkFontSize = (double)(e.NewValue ?? 15));
        };
        WorkFontControls.Children.Add(new TextBlock { Text = "Text size", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        WorkFontControls.Children.Add(size);

        WorkFontGallery.Children.Clear();
        foreach (var category in WorkFonts.Categories)
        {
            WorkFontGallery.Children.Add(new TextBlock { Text = category.ToUpperInvariant(), Classes = { "eyebrow" }, FontSize = 9.5, Margin = new Thickness(2, 12, 0, 4) });
            var wrap = new WrapPanel();
            foreach (var font in WorkFonts.All.Where(f => f.Category == category))
            {
                var f = font;
                var card = new StackPanel { Spacing = 2, Margin = new Thickness(4, 2) };
                card.Children.Add(new TextBlock { Text = f.Name, FontFamily = f.Font, FontSize = 22, TextTrimming = TextTrimming.CharacterEllipsis });
                card.Children.Add(new TextBlock { Text = "The quick brown fox", FontFamily = f.Font, FontSize = 12.5, Classes = { "muted" } });
                var chip = new Button { Classes = { "wcard" }, Width = 214, Height = 74, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(14), Content = card, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8) };
                chip.Click += (_, _) => ShowFontUse(chip, f, title, body);
                wrap.Children.Add(chip);
            }
            WorkFontGallery.Children.Add(wrap);
        }
    }

    private void ShowFontUse(Control anchor, WorkFont font, FontPickerButton title, FontPickerButton body)
    {
        Flyout? flyout = null;
        var asTitle = new Button { Content = "Use as title font", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var asBody = new Button { Content = "Use as writing font", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        asTitle.Click += (_, _) =>
        {
            Update(s => s.WorkTitleFont = font.Name);
            title.Selected = font.Name;
            flyout?.Hide();
        };
        asBody.Click += (_, _) =>
        {
            Update(s => s.WorkBodyFont = font.Name);
            body.Selected = font.Name;
            flyout?.Hide();
        };
        flyout = new Flyout { Content = new StackPanel { Width = 210, Spacing = 4, Children = { new TextBlock { Text = font.Name, FontFamily = font.Font, FontSize = 18, Margin = new Thickness(4, 0, 0, 4) }, asTitle, asBody } } };
        flyout.ShowAt(anchor);
    }

    private void BuildWebPins()
    {
        WebPinRows.Children.Clear();
        for (var i = 0; i < _settings.WebPins.Count; i++)
        {
            var pin = _settings.WebPins[i];
            var index = i;
            var service = WebServices.Find(pin.Service);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = service?.Name ?? pin.Name ?? pin.Url ?? "?", FontWeight = FontWeight.SemiBold });
            text.Children.Add(new TextBlock { Text = service?.Url ?? pin.Url ?? "", Classes = { "faint", "mono" }, FontSize = 11 });
            row.Children.Add(text);
            row.Children.Add(Small("IconChevronLeft", "Move up", 1, () => Move(index, -1), index > 0));
            row.Children.Add(Small("IconChevronRight", "Move down", 2, () => Move(index, 1), index < _settings.WebPins.Count - 1));
            row.Children.Add(Small("IconClose", "Remove", 3, () =>
            {
                _settings.WebPins.RemoveAt(index);
                BuildWebPins();
                Changed?.Invoke();
            }, true));
            WebPinRows.Children.Add(new Border { Classes = { "dockrowstatic" }, Padding = new Thickness(12, 8), Child = row });
        }

        void Move(int index, int delta)
        {
            var pin = _settings.WebPins[index];
            _settings.WebPins.RemoveAt(index);
            _settings.WebPins.Insert(index + delta, pin);
            BuildWebPins();
            Changed?.Invoke();
        }
    }

    private static Button Small(string icon, string tip, int column, Action click, bool enabled)
    {
        var button = new Button { Classes = { "icon" }, Width = 28, Height = 28, IsEnabled = enabled, Content = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon(icon) } };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        Grid.SetColumn(button, column);
        return button;
    }

    // ---------------------------------------------------------------- AI

    private void BuildKeys()
    {
        KeyRows.Children.Clear();
        foreach (var (id, name, hint) in KeyProviders)
        {
            var has = !string.IsNullOrEmpty(Ai?.GetKey(id));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*,Auto") };
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, FontSize = 12.5 });
            label.Children.Add(new TextBlock { Text = has ? "✓ saved in keychain" : hint, Classes = { "faint" }, FontSize = 10.5, Foreground = has ? Res.Brush("SuccessBrush") : Res.Brush("FaintBrush") });
            grid.Children.Add(label);
            var box = new TextBox { PasswordChar = '•', PlaceholderText = has ? "•••••••• (stored)" : "Paste the API key", Margin = new Thickness(10, 0) };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var save = new Button { Classes = { "accent" }, Content = "Save", Padding = new Thickness(14, 6) };
            save.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return;
                Ai?.SetKey(id, box.Text.Trim());
                Ai?.ForgetModels(id == "anthropic" ? "claude" : id);
                BuildKeys();
            };
            buttons.Children.Add(save);
            if (has)
            {
                var remove = new Button { Classes = { "ghost" }, Content = "Remove", Padding = new Thickness(12, 6) };
                remove.Click += (_, _) =>
                {
                    Ai?.SetKey(id, null);
                    BuildKeys();
                };
                buttons.Children.Add(remove);
            }
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            KeyRows.Children.Add(grid);
        }
    }

    private void BuildPrompts()
    {
        PromptRows.Children.Clear();
        foreach (var prompt in _settings.Prompts.ToList())
        {
            var name = new TextBox { Text = prompt.Name, FontWeight = FontWeight.SemiBold, Width = 200, VerticalAlignment = VerticalAlignment.Top };
            var text = new TextBox { Text = prompt.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, Margin = new Thickness(8, 0) };
            name.LostFocus += (_, _) => { prompt.Name = name.Text ?? ""; Changed?.Invoke(); };
            text.LostFocus += (_, _) => { prompt.Text = text.Text ?? ""; Changed?.Invoke(); };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(name);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            row.Children.Add(Small("IconClose", "Delete prompt", 2, () =>
            {
                _settings.Prompts.Remove(prompt);
                BuildPrompts();
                Changed?.Invoke();
            }, true));
            PromptRows.Children.Add(row);
        }
    }

    // ---------------------------------------------------------------- projects

    private void BuildProjects()
    {
        ProjectRows.Children.Clear();
        if (_settings.Projects.Count == 0)
            ProjectRows.Children.Add(new TextBlock { Text = "No projects yet.", Classes = { "faint" } });
        foreach (var project in _settings.Projects.ToList())
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto") };
            void Row(int r, string label, Control editor)
            {
                var l = new TextBlock { Text = label, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, r == 0 ? 0 : 8, 0, 0) };
                Grid.SetRow(l, r);
                grid.Children.Add(l);
                editor.Margin = new Thickness(0, r == 0 ? 0 : 8, 0, 0);
                Grid.SetRow(editor, r);
                Grid.SetColumn(editor, 1);
                grid.Children.Add(editor);
            }

            var name = new TextBox { Text = project.Name };
            name.LostFocus += (_, _) => { project.Name = name.Text?.Trim() ?? project.Name; Changed?.Invoke(); };
            Row(0, "Name", name);

            var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var folder = new TextBox { Text = project.Folder, Classes = { "mono" } };
            folder.LostFocus += (_, _) => { project.Folder = folder.Text?.Trim() ?? project.Folder; Changed?.Invoke(); };
            folderRow.Children.Add(folder);
            var browse = new Button { Classes = { "ghost" }, Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is not { } top) return;
                var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Project folder" });
                if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
                {
                    folder.Text = project.Folder = path;
                    Changed?.Invoke();
                }
            };
            Grid.SetColumn(browse, 1);
            folderRow.Children.Add(browse);
            Row(1, "Folder", folderRow);

            var commands = new TextBox { Text = string.Join('\n', project.Commands), AcceptsReturn = true, Classes = { "mono" }, MinHeight = 48, PlaceholderText = "npm run dev   (one command per line)" };
            commands.LostFocus += (_, _) =>
            {
                project.Commands = (commands.Text ?? "").Split('\n').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                Changed?.Invoke();
            };
            Row(2, "Start with", commands);

            var url = new TextBox { Text = project.Url, Classes = { "mono" }, PlaceholderText = "http://localhost:5173  (opened in a web tab)" };
            url.LostFocus += (_, _) => { project.Url = WebServices.NormalizeUrl(url.Text) ?? (string.IsNullOrWhiteSpace(url.Text) ? null : project.Url); Changed?.Invoke(); };
            Row(3, "Open", url);

            var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var editor = new CheckBox { Content = "Open the folder in my code editor", IsChecked = project.OpenEditor };
            editor.IsCheckedChanged += (_, _) => { project.OpenEditor = editor.IsChecked == true; Changed?.Invoke(); };
            bottom.Children.Add(editor);
            var delete = new Button { Classes = { "ghost" }, Content = "Remove project" };
            delete.Click += (_, _) =>
            {
                _settings.Projects.Remove(project);
                BuildProjects();
                Changed?.Invoke();
            };
            Grid.SetColumn(delete, 2);
            bottom.Children.Add(delete);
            Row(4, "", bottom);

            ProjectRows.Children.Add(new Border { Classes = { "pagecard" }, Padding = new Thickness(18, 14), Child = grid });
        }
    }

    // ---------------------------------------------------------------- dock cards

    private void BuildCardChips()
    {
        CardChips.Children.Clear();
        foreach (var (id, label) in SideDock.Cards)
        {
            var chip = new ToggleButton
            {
                Classes = { "gaugechip" }, Width = 176,
                Content = new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, FontSize = 12.5 },
                IsChecked = !_settings.HiddenDockCards.Contains(id),
            };
            chip.IsCheckedChanged += (_, _) =>
            {
                _settings.HiddenDockCards.Remove(id);
                if (chip.IsChecked != true) _settings.HiddenDockCards.Add(id);
                Changed?.Invoke();
            };
            CardChips.Children.Add(chip);
        }
    }
}
