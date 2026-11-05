using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    private readonly Dictionary<string, string> _keyErrors = new();

    internal AiService? Ai { get; set; }
    internal Func<Task>? AddProject { get; set; }

    /// <summary>Runs commit when the field loses focus and shortly after typing stops, so a quit mid-edit loses nothing.</summary>
    private static void CommitOnEdit(TextBox box, Action commit)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); commit(); };
        box.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
        box.LostFocus += (_, _) => { timer.Stop(); commit(); };
    }

    private void InitializeSections()
    {
        WebToggle.IsCheckedChanged += (_, _) => Update(s => s.ShowWebStrip = WebToggle.IsChecked == true, SettingsChange.WebStrip);
        CommitOnEdit(OllamaUrl, () => Update(s => s.OllamaUrl = OllamaUrl.Text?.Trim() is { Length: > 0 } u ? u : s.OllamaUrl));
        CommitOnEdit(LmStudioUrl, () => Update(s => s.LmStudioUrl = LmStudioUrl.Text?.Trim() is { Length: > 0 } u ? u : s.LmStudioUrl));
        CommitOnEdit(SystemPrompt, () => Update(s => s.AiSystemPrompt = SystemPrompt.Text ?? ""));
        AiToolsToggle.IsCheckedChanged += (_, _) => Update(s => s.AiTools = AiToolsToggle.IsChecked == true);
        AiEditsToggle.IsCheckedChanged += (_, _) => Update(s => s.AiAllowEdits = AiEditsToggle.IsChecked == true);
        ClearMemoryButton.Click += (_, _) =>
        {
            if (Ai is null) return;
            Ai.Memory.Clear(null);
            foreach (var folder in MemoryFolders()) Ai.Memory.Clear(folder);
            BuildMemory();
        };
        AddPromptButton.Click += (_, _) =>
        {
            _settings.Prompts.Add(new SavedPrompt { Name = "New prompt", Text = "{{clipboard}}" });
            BuildPrompts();
            Changed?.Invoke(SettingsChange.None);
        };
        AddProjectButton.Click += async (_, _) =>
        {
            if (AddProject is not null) await AddProject();
            BuildProjects();
        };
        FocusMinutes.ValueChanged += (_, _) => Update(s => s.FocusMinutes = (int)(FocusMinutes.Value ?? 25), SettingsChange.Focus);
        BreakMinutes.ValueChanged += (_, _) => Update(s => s.BreakMinutes = (int)(BreakMinutes.Value ?? 5), SettingsChange.Focus);
        BuildWorkFonts();
    }

    private void LoadSections()
    {
        _loading = true;
        WebToggle.IsChecked = _settings.ShowWebStrip;
        OllamaUrl.Text = _settings.OllamaUrl;
        LmStudioUrl.Text = _settings.LmStudioUrl;
        SystemPrompt.Text = _settings.AiSystemPrompt;
        AiToolsToggle.IsChecked = _settings.AiTools;
        AiEditsToggle.IsChecked = _settings.AiAllowEdits;
        FocusMinutes.Value = _settings.FocusMinutes;
        BreakMinutes.Value = _settings.BreakMinutes;
        _loading = false;
        BuildWebPins();
        BuildWorks();
        BuildKeys();
        BuildMemory();
        BuildAbilities();
        BuildPrompts();
        BuildProjects();
        BuildCardChips();
    }

    // ---------------------------------------------------------------- AI abilities

    private void BuildAbilities()
    {
        AbilityChips.Children.Clear();
        if (Ai is null) return;
        IReadOnlyList<Overdesk.AI.AiTool> tools;
        try { tools = Ai.AllTools(); }
        catch (Exception) { return; /* a broken plugin must not break the settings page */ }
        foreach (var tool in tools.OrderBy(t => t.IsFileTool).ThenBy(t => t.Name))
        {
            var chip = new ToggleButton
            {
                Classes = { "gaugechip" }, Content = tool.Name.Replace('_', ' '),
                IsChecked = !_settings.AiDisabledTools.Contains(tool.Name),
                // Small pills, not the dock's gauge cards.
                Width = double.NaN, Padding = new Thickness(11, 5), Margin = new Thickness(0, 0, 6, 6), FontSize = 12, CornerRadius = new CornerRadius(14),
            };
            ToolTip.SetTip(chip, new TextBlock { Text = tool.Description + (tool.Approval is not null ? "\n\nAsks you before every use." : ""), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
            var name = tool.Name;
            chip.IsCheckedChanged += (_, _) =>
            {
                _settings.AiDisabledTools.Remove(name);
                if (chip.IsChecked != true) _settings.AiDisabledTools.Add(name);
                Changed?.Invoke(SettingsChange.None);
            };
            AbilityChips.Children.Add(chip);
        }
    }

    // ---------------------------------------------------------------- AI memory

    private IEnumerable<string> MemoryFolders()
        => _settings.AiRecentFolders.Concat(_settings.Projects.Select(p => p.Folder)).Distinct(StringComparer.OrdinalIgnoreCase);

    private int _memoryLoad;

    /// <summary>The note files are read off the UI thread; a newer load wins over an older one still in flight.</summary>
    private async void BuildMemory()
    {
        if (Ai is null) return;
        var memory = Ai.Memory;
        var folders = MemoryFolders().ToList();
        var load = ++_memoryLoad;
        var groups = await Task.Run(() =>
        {
            var list = new List<(string Title, string? Folder, IReadOnlyList<Overdesk.AI.MemoryNote> Notes)> { ("About you", null, memory.Notes(null)) };
            foreach (var folder in folders)
                if (memory.Notes(folder) is { Count: > 0 } notes) list.Add(($"Project {Path.GetFileName(folder.TrimEnd('/', '\\'))}", folder, notes));
            return list;
        });
        if (load != _memoryLoad) return;
        MemoryRows.Children.Clear();
        if (groups.All(g => g.Notes.Count == 0))
        {
            MemoryRows.Children.Add(new TextBlock { Text = "Nothing yet.", Classes = { "faint" }, FontSize = 12 });
            ClearMemoryButton.IsVisible = false;
            return;
        }
        ClearMemoryButton.IsVisible = true;
        foreach (var (title, folder, notes) in groups.Where(g => g.Notes.Count > 0))
        {
            MemoryRows.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Classes = { "eyebrow" }, FontSize = 9.5, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var note in notes)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                row.Children.Add(new TextBlock { Text = note.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
                var delete = new Button { Classes = { "icon" }, Width = 26, Height = 26, Content = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconTrash"), Height = 11 } };
                ToolTip.SetTip(delete, "Forget this");
                var id = note.Id;
                delete.Click += (_, _) =>
                {
                    Ai.Memory.Remove(folder, id);
                    BuildMemory();
                };
                Grid.SetColumn(delete, 1);
                row.Children.Add(delete);
                MemoryRows.Children.Add(row);
            }
        }
    }

    // ---------------------------------------------------------------- web strip

    private readonly FontPickerButton _workTitleFont = new();
    private readonly FontPickerButton _workBodyFont = new();
    private readonly NumericUpDown _workFontSize = new() { Minimum = 11, Maximum = 32, Increment = 1, Width = 130, FormatString = "0 'px'", HorizontalAlignment = HorizontalAlignment.Left };

    private void BuildWorks()
    {
        _loading = true;
        _workTitleFont.Selected = _settings.WorkTitleFont;
        _workBodyFont.Selected = _settings.WorkBodyFont;
        _workFontSize.Value = (decimal)_settings.WorkFontSize;
        _loading = false;
    }

    /// <summary>The font controls and the 41-font gallery are built once; Show only sets their values.</summary>
    private void BuildWorkFonts()
    {
        var title = _workTitleFont;
        var body = _workBodyFont;
        WorkFontControls.Children.Add(new TextBlock { Text = "Title font", FontWeight = FontWeight.SemiBold });
        title.Chosen += name => Update(s => s.WorkTitleFont = name ?? s.WorkTitleFont);
        WorkFontControls.Children.Add(title);
        WorkFontControls.Children.Add(new TextBlock { Text = "Writing font", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        body.Chosen += name => Update(s => s.WorkBodyFont = name ?? s.WorkBodyFont);
        WorkFontControls.Children.Add(body);
        _workFontSize.ValueChanged += (_, e) =>
        {
            if (!_loading) Update(s => s.WorkFontSize = (double)(e.NewValue ?? 15));
        };
        WorkFontControls.Children.Add(new TextBlock { Text = "Text size", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        WorkFontControls.Children.Add(_workFontSize);

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
                Changed?.Invoke(SettingsChange.WebStrip);
            }, true));
            WebPinRows.Children.Add(new Border { Classes = { "dockrowstatic" }, Padding = new Thickness(12, 8), Child = row });
        }

        void Move(int index, int delta)
        {
            var pin = _settings.WebPins[index];
            _settings.WebPins.RemoveAt(index);
            _settings.WebPins.Insert(index + delta, pin);
            BuildWebPins();
            Changed?.Invoke(SettingsChange.WebStrip);
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
            _keyErrors.TryGetValue(id, out var keyError);
            label.Children.Add(new TextBlock
            {
                Text = keyError ?? (has ? "✓ saved in keychain" : hint), Classes = { "faint" }, FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
                Foreground = keyError is not null ? Res.Brush("DangerBrush") : has ? Res.Brush("SuccessBrush") : Res.Brush("FaintBrush"),
            });
            grid.Children.Add(label);
            var box = new TextBox { PasswordChar = '•', PlaceholderText = has ? "•••••••• (stored)" : "Paste the API key", Margin = new Thickness(10, 0) };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var save = new Button { Classes = { "accent" }, Content = "Save", Padding = new Thickness(14, 6) };
            save.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return;
                var key = box.Text.Trim();
                _keyErrors.Remove(id);
                try
                {
                    Ai?.SetKey(id, key);
                    if (Ai?.GetKey(id) != key) _keyErrors[id] = "Could not save to the keychain. Allow access when macOS asks, then try again.";
                }
                catch (Exception ex)
                {
                    _keyErrors[id] = "Could not save to the keychain: " + ex.Message;
                }
                if (!_keyErrors.ContainsKey(id)) Ai?.ForgetModels(id == "anthropic" ? "claude" : id);
                BuildKeys();
            };
            buttons.Children.Add(save);
            if (has)
            {
                var remove = new Button { Classes = { "ghost" }, Content = "Remove", Padding = new Thickness(12, 6) };
                remove.Click += (_, _) =>
                {
                    _keyErrors.Remove(id);
                    try
                    {
                        Ai?.SetKey(id, null);
                        if (!string.IsNullOrEmpty(Ai?.GetKey(id))) _keyErrors[id] = "Could not remove the key from the keychain.";
                    }
                    catch (Exception ex)
                    {
                        _keyErrors[id] = "Could not remove the key: " + ex.Message;
                    }
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
            CommitOnEdit(name, () => { prompt.Name = name.Text ?? ""; Changed?.Invoke(SettingsChange.None); });
            CommitOnEdit(text, () => { prompt.Text = text.Text ?? ""; Changed?.Invoke(SettingsChange.None); });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(name);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            row.Children.Add(Small("IconClose", "Delete prompt", 2, () =>
            {
                _settings.Prompts.Remove(prompt);
                BuildPrompts();
                Changed?.Invoke(SettingsChange.None);
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
            CommitOnEdit(name, () => { project.Name = name.Text?.Trim() ?? project.Name; Changed?.Invoke(SettingsChange.Projects); });
            Row(0, "Name", name);

            var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var folder = new TextBox { Text = project.Folder, Classes = { "mono" } };
            CommitOnEdit(folder, () => { project.Folder = folder.Text?.Trim() ?? project.Folder; Changed?.Invoke(SettingsChange.Projects); });
            folderRow.Children.Add(folder);
            var browse = new Button { Classes = { "ghost" }, Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is not { } top) return;
                var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Project folder" });
                if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
                {
                    folder.Text = project.Folder = path;
                    Changed?.Invoke(SettingsChange.Projects);
                }
            };
            Grid.SetColumn(browse, 1);
            folderRow.Children.Add(browse);
            Row(1, "Folder", folderRow);

            var commands = new TextBox { Text = string.Join('\n', project.Commands), AcceptsReturn = true, Classes = { "mono" }, MinHeight = 48, PlaceholderText = "npm run dev   (one command per line)" };
            CommitOnEdit(commands, () =>
            {
                project.Commands = (commands.Text ?? "").Split('\n').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                Changed?.Invoke(SettingsChange.Projects);
            });
            Row(2, "Start with", commands);

            var url = new TextBox { Text = project.Url, Classes = { "mono" }, PlaceholderText = "http://localhost:5173  (opened in a web tab)" };
            url.LostFocus += (_, _) =>
            {
                var normalized = Overdesk.Core.Tabs.WebUrl.Normalize(url.Text);
                if (normalized is null && !string.IsNullOrWhiteSpace(url.Text)) url.Text = project.Url; // invalid: show what is really kept
                else project.Url = normalized;
                Changed?.Invoke(SettingsChange.None);
            };
            Row(3, "Open", url);

            var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var editor = new CheckBox { Content = "Open the folder in my code editor", IsChecked = project.OpenEditor };
            editor.IsCheckedChanged += (_, _) => { project.OpenEditor = editor.IsChecked == true; Changed?.Invoke(SettingsChange.None); };
            bottom.Children.Add(editor);
            var delete = new Button { Classes = { "ghost" }, Content = "Remove project" };
            delete.Click += (_, _) =>
            {
                _settings.Projects.Remove(project);
                BuildProjects();
                Changed?.Invoke(SettingsChange.Projects);
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
                Changed?.Invoke(SettingsChange.DockCards);
            };
            CardChips.Children.Add(chip);
        }
    }
}
