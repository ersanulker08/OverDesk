using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Overdesk.AI;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Tabs;
using Overdesk.Core.Works;
using Overdesk.Core.Settings;

namespace Overdesk.App;

// What the AI can do inside OverDesk: see and switch tabs, open programs, sites and tools, read and change works,
// look at the program on stage, use the clipboard and open project files in the editor. Every tool that touches the
// UI runs on the UI thread; providers call them from their own threads (and Claude Code / Gemini CLI over MCP).
public partial class MainWindow
{

    private static Task<T> Ui<T>(Func<T> action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
    private static Task<T> Ui<T>(Func<Task<T>> action) => Dispatcher.UIThread.InvokeAsync(action);

    private IReadOnlyList<AiTool> BuildAiTools()
    {
        var tools = new List<AiTool>();
        tools.AddRange(AppTools());
        tools.AddRange(WorkTools());
        tools.AddRange(DeskTools());
        if (_settings.AiFolder is { } folder && Directory.Exists(folder)) tools.Add(RunCommandTool(folder));
        return tools;
    }

    // ---------------------------------------------------------------- OverDesk itself

    private IEnumerable<AiTool> AppTools()
    {
        yield return new AiTool
        {
            Name = "overdesk_state",
            Description = "What is open in OverDesk right now: program tabs (the active one marked), web tabs, the workspace, " +
                          "the tools in the right-hand panel, the user's projects and the project folder chosen for this chat.",
            Schema = AiTool.Object(),
            Label = _ => "Looking at OverDesk",
            Run = (_, _) => Ui(() => new AiToolResult(DescribeState())),
        };
        yield return new AiTool
        {
            Name = "open_program",
            Description = "Open an installed program as a tab in OverDesk (or switch to its tab when it is already open). " +
                          "Give its name as the user would say it (\"Visual Studio Code\", \"Spotify\", \"Calculator\").",
            Schema = AiTool.Object(("name", "string", "Program name or full path.", true)),
            Label = a => $"Opening {AiToolbox.Str(a, "name")}",
            Run = async (a, _) =>
            {
                var name = AiToolbox.Str(a, "name") ?? "";
                var path = await FindProgramAsync(name);
                if (path is null) return AiToolResult.Error($"No installed program matches “{name}”.");
                return await Ui(() =>
                {
                    OpenProgram(path);
                    return new AiToolResult($"Opened {ProgramNames.FromPath(path)}.");
                });
            },
        };
        yield return new AiTool
        {
            Name = "switch_tab",
            Description = "Bring a program tab, web tab or the home page to the front. Use the title from overdesk_state, or \"home\".",
            Schema = AiTool.Object(("title", "string", "Tab title (a part of it is enough), or home.", true)),
            Label = a => $"Switching to {AiToolbox.Str(a, "title")}",
            Run = (a, _) => Ui(() =>
            {
                var title = AiToolbox.Str(a, "title") ?? "";
                if (title.Equals("home", StringComparison.OrdinalIgnoreCase))
                {
                    ShowHome();
                    return new AiToolResult("Showing the home page.");
                }
                if (FindTab(title) is { } tab)
                {
                    _activeWeb = null;
                    _tabs.Activate(tab);
                    return new AiToolResult($"Switched to {tab.Title}.");
                }
                if (_webTabs.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) is { } web)
                {
                    ShowWeb(web);
                    return new AiToolResult($"Switched to {web.Title}.");
                }
                return AiToolResult.Error($"No tab matches “{title}”.");
            }),
        };
        yield return new AiTool
        {
            Name = "close_tab",
            Description = "Close a program tab or web tab. Only when the user asked for it: closing quits a program OverDesk started.",
            Schema = AiTool.Object(("title", "string", "Tab title (a part of it is enough).", true)),
            Label = a => $"Closing {AiToolbox.Str(a, "title")}",
            Approval = a => $"Close the tab “{AiToolbox.Str(a, "title")}”?",
            Run = (a, _) => Ui(() =>
            {
                var title = AiToolbox.Str(a, "title") ?? "";
                if (FindTab(title) is { } tab)
                {
                    CloseTab(tab);
                    return new AiToolResult($"Closed {tab.Title}.");
                }
                if (_webTabs.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) is { } web)
                {
                    CloseWeb(web);
                    return new AiToolResult($"Closed {web.Title}.");
                }
                return AiToolResult.Error($"No tab matches “{title}”.");
            }),
        };
        yield return new AiTool
        {
            Name = "open_website",
            Description = "Open a web page as a web tab inside OverDesk.",
            Schema = AiTool.Object(("url", "string", "Full URL (https://...).", true), ("title", "string", "Tab title.", false)),
            Label = a => $"Opening {AiToolbox.Str(a, "url")}",
            Run = (a, _) => Ui(() =>
            {
                var url = AiToolbox.Str(a, "url") ?? "";
                if (!url.Contains("://")) url = "https://" + url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                    return AiToolResult.Error("Only http and https addresses can be opened.");
                OpenWeb(uri.ToString(), AiToolbox.Str(a, "title") ?? uri.Host);
                return new AiToolResult($"Opened {uri.Host}.");
            }),
        };
        yield return new AiTool
        {
            Name = "open_tool",
            Description = "Open one of the tools of OverDesk's right-hand panel (built-in or plugins the user added) by name.",
            Schema = AiTool.Object(("name", "string", "Tool name from overdesk_state.", true)),
            Label = a => $"Opening the {AiToolbox.Str(a, "name")} tool",
            Run = (a, _) => Ui(() =>
            {
                var name = AiToolbox.Str(a, "name") ?? "";
                var tool = _plugins.Tools.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                           ?? _plugins.Tools.FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (tool is null) return AiToolResult.Error($"No tool is called “{name}”.");
                if (tool.Id == AiToolId) return new AiToolResult("The AI panel is already open (this chat).");
                OpenTool(tool);
                return new AiToolResult($"Opened {tool.Name}.");
            }),
        };
        yield return new AiTool
        {
            Name = "look_at_screen",
            Description = "Take a screenshot of the program on stage (the active program tab) to see what the user sees.",
            Schema = AiTool.Object(),
            Label = _ => "Looking at the screen",
            Run = async (_, _) =>
            {
                var png = await Ui(CaptureStagePngAsync);
                if (png is not null) return new AiToolResult("Screenshot of the program on stage.", png);
                return await Ui(() => AiToolResult.Error(_tabs.Active is { Status: TabStatus.Running, IsInternal: false } && _activeWeb is null
                    ? "Could not capture the program: OverDesk needs the Screen Recording permission (System Settings → Privacy & Security)."
                    : "There is no program window on stage to capture (" + StageSummary() + ") Use overdesk_state, read_work or the other tools instead."));
            },
        };
        yield return new AiTool
        {
            Name = "read_clipboard",
            Description = "Read the text the user has copied.",
            Schema = AiTool.Object(),
            Label = _ => "Reading the clipboard",
            Run = (_, _) => Ui(async () =>
            {
                var text = Clipboard is { } c ? await c.TryGetTextAsync() : null;
                return string.IsNullOrEmpty(text) ? AiToolResult.Error("The clipboard has no text.") : new AiToolResult(text);
            }),
        };
        yield return new AiTool
        {
            Name = "copy_to_clipboard",
            Description = "Put text on the clipboard so the user can paste it anywhere.",
            Schema = AiTool.Object(("text", "string", "Text to copy.", true)),
            Label = _ => "Copying to the clipboard",
            Run = (a, _) => Ui(async () =>
            {
                if (Clipboard is { } c) await c.SetTextAsync(a["text"]?.GetValue<string>() ?? "");
                ShowToast("AI copied text to the clipboard", "IconCopy");
                return new AiToolResult("Copied.");
            }),
        };
        yield return new AiTool
        {
            Name = "open_in_editor",
            Description = "Open a file (and line) of the project folder in the user's code editor (VS Code, Cursor...) and bring the " +
                          "editor's tab to the front. Use it to show changes you made.",
            Schema = AiTool.Object(("path", "string", "File path relative to the project folder (or the folder itself when empty).", false),
                                   ("line", "integer", "Line to jump to.", false)),
            Label = a => $"Opening {AiToolbox.Str(a, "path") ?? "the project"} in the editor",
            Run = (a, _) => Ui(() => OpenInEditor(AiToolbox.Str(a, "path"), AiToolbox.Int(a, "line"))),
        };
    }

    /// <summary>
    /// A shell command in the project folder (build, test, lint, git status...). Every run asks the user first and
    /// shows up as a task in the dock with its live log.
    /// </summary>
    private AiTool RunCommandTool(string folder) => new()
    {
        Name = "run_command",
        Description = "Run a shell command in the project folder and get its output, e.g. to build, run tests or a linter, or check git status. " +
                      "The user approves every command. Commands that keep running (dev servers) are left running in the background after the wait.",
        Schema = AiTool.Object(("command", "string", "The command line, as typed in a terminal in the project folder.", true),
                               ("wait_seconds", "integer", "How long to wait for it to finish (5-600, default 120).", false)),
        Label = a => $"Running `{AiToolbox.Str(a, "command")}`",
        Approval = a => $"Run `{AiToolbox.Str(a, "command")}` in {Path.GetFileName(folder.TrimEnd('/', '\\'))}?",
        Run = async (a, ct) =>
        {
            var command = AiToolbox.Str(a, "command");
            if (string.IsNullOrWhiteSpace(command)) return AiToolResult.Error("No command given.");
            var task = await Ui(() => _runner.Start("AI: " + (command.Length > 40 ? command[..39] + "…" : command), command, folder));
            var wait = TimeSpan.FromSeconds(Math.Clamp(AiToolbox.Int(a, "wait_seconds") ?? 120, 5, 600));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (task.IsRunning && clock.Elapsed < wait) await Task.Delay(250, ct);
            }
            catch (OperationCanceledException)
            {
                _runner.Stop(task);
                throw;
            }
            var output = task.Output;
            if (output.Length > 12000) output = "…" + output[^12000..];
            if (task.IsRunning)
                return new AiToolResult($"Still running after {wait.TotalSeconds:0} s (left running as a task in the dock). Output so far:\n{output}");
            return new AiToolResult($"Exit code {task.ExitCode}.\n{output}", IsError: task.ExitCode != 0);
        },
    };

    private string DescribeState()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Workspace: {CurrentWorkspace.Name} (others: {string.Join(", ", _settings.Workspaces.Where(w => w != CurrentWorkspace).Select(w => w.Name).DefaultIfEmpty("none"))})");
        sb.AppendLine("Program tabs:");
        foreach (var tab in _tabs.Tabs)
        {
            var kind = tab.IsWork ? "work" : tab.IsSettings ? "OverDesk settings" : tab.Status.ToString().ToLowerInvariant();
            sb.AppendLine($"- {tab.Title} [{kind}]{(tab == _tabs.Active && _activeWeb is null ? "  ← on stage" : "")}{(tab.Badge is { } b ? $" (unread: {b})" : "")}");
        }
        if (_tabs.Tabs.Count == 0) sb.AppendLine("- none");
        if (_tabs.Active is null && _activeWeb is null) sb.AppendLine("The home page is showing.");
        if (_webTabs.Count > 0)
        {
            sb.AppendLine("Web tabs:");
            foreach (var web in _webTabs) sb.AppendLine($"- {web.Title} ({web.Url}){(web == _activeWeb ? "  ← on stage" : "")}");
        }
        sb.AppendLine("Tools in the right-hand panel: " + string.Join(", ", _plugins.Tools.Select(t => t.Name)));
        if (_settings.Projects.Count > 0)
            sb.AppendLine("Projects: " + string.Join(", ", _settings.Projects.Select(p => $"{p.Name} ({p.Folder})")));
        sb.AppendLine("Project folder of this chat: " + (_settings.AiFolder ?? "none (the user can pick one at the bottom of the AI panel)"));
        sb.AppendLine($"Works: {_works.Library.Works.Count}");
        return sb.ToString();
    }

    private HostedTab? FindTab(string title)
        => _tabs.Tabs.FirstOrDefault(t => t.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
           ?? _tabs.Tabs.FirstOrDefault(t => t.Title.Contains(title, StringComparison.OrdinalIgnoreCase));

    private static async Task<string?> FindProgramAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        if ((File.Exists(name) || Directory.Exists(name)) && Path.IsPathRooted(name)) return name;
        var installed = await ProgramLibrary.Installed;
        static string Key(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var wanted = Key(name);
        return installed.FirstOrDefault(p => Key(ProgramNames.FromPath(p)) == wanted)
               ?? installed.FirstOrDefault(p => Key(ProgramNames.FromPath(p)).StartsWith(wanted, StringComparison.Ordinal))
               ?? installed.FirstOrDefault(p => Key(ProgramNames.FromPath(p)).Contains(wanted, StringComparison.Ordinal));
    }

    /// <summary>VS Code style editors understand `-g file:line`; others just open the file.</summary>
    private AiToolResult OpenInEditor(string? relative, int? line)
    {
        if (_settings.AiFolder is not { } folder || !Directory.Exists(folder))
            return AiToolResult.Error("No project folder is chosen for this chat.");
        var path = ProjectTools.Resolve(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), relative ?? "");
        if (!File.Exists(path) && !Directory.Exists(path)) return AiToolResult.Error("No such file in the project.");
        var editor = FindEditor();
        if (editor is null) return AiToolResult.Error("No code editor (VS Code, Cursor, Zed...) is installed.");
        // Development snapshots run unattended: never pop up the user's editor.
        if (Environment.GetEnvironmentVariable("OVERDESK_SNAPSHOT") is { Length: > 0 }) return new AiToolResult("(snapshot run: the editor was not opened)");

        var name = ProgramNames.FromPath(editor);
        var cli = name switch { "Visual Studio Code" or "Code" => "code", "Cursor" => "cursor", "Windsurf" => "windsurf", "Zed" => "zed", _ => null };
        var cliPath = cli is null ? null : CliTools.Find(cli);
        string command;
        if (cliPath is not null)
        {
            var target = line is > 0 && File.Exists(path) ? $"{path}:{line}" : path;
            // Folder first, so the file opens in the project's window rather than a new one.
            command = cli == "zed" ? $"{Quote(cliPath)} {Quote(target)}" : $"{Quote(cliPath)} {Quote(folder)} -g {Quote(target)}";
        }
        else
            command = OperatingSystem.IsMacOS() ? $"open -a {Quote(editor)} {Quote(path)}" : $"{Quote(editor)} {Quote(path)}";
        _runner.Start($"Open in {name}", command, folder);
        OpenProgram(editor);
        return new AiToolResult($"Opened {ProjectTools.Relative(folder, path)} in {name}.");
    }

    // ---------------------------------------------------------------- works

    private static JsonObject BlocksSchema() => new()
    {
        ["type"] = "array",
        ["description"] = "Rows of the work, in order.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(WorkText.Kinds.Select(k => (JsonNode)k).ToArray()),
                    ["description"] = "heading, text (a paragraph), todo (checkbox), toggle (named on/off variable), field (named text value), " +
                                      "number (named number), bullet, quote, divider" },
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "The content, or the name of a toggle/field/number variable." },
                ["value"] = new JsonObject { ["type"] = "string", ["description"] = "field: its value." },
                ["checked"] = new JsonObject { ["type"] = "boolean", ["description"] = "todo: done; toggle: on." },
                ["number"] = new JsonObject { ["type"] = "number", ["description"] = "number: its value." },
                ["level"] = new JsonObject { ["type"] = "integer", ["description"] = "heading: 1 (big) to 3 (small)." },
            },
            ["required"] = new JsonArray("kind"),
        },
    };

    private static JsonObject WithBlocks(JsonObject schema, bool required)
    {
        schema["properties"]!["blocks"] = BlocksSchema();
        if (required) schema["required"]!.AsArray().Add("blocks");
        return schema;
    }

    /// <summary>The "work" argument every work tool takes.</summary>
    private static (string Name, string Type, string Description, bool Required) WorkParam => ("work", "string", "The work's id, or its title (a part of it is enough).", true);

    private IEnumerable<AiTool> WorkTools()
    {
        yield return new AiTool
        {
            Name = "list_works",
            Description = "List the user's works (notes, lists, stories, game variable sheets...) with id, title, template and a summary.",
            Schema = AiTool.Object(("query", "string", "Only works whose title or text contains this.", false)),
            Label = _ => "Looking through your works",
            Run = (a, _) => Ui(() =>
            {
                var query = AiToolbox.Str(a, "query");
                var works = _works.Library.Works.Where(w => query is null || w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                                                            || w.ToMarkdown().Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
                if (works.Count == 0) return new AiToolResult(query is null ? "The user has no works yet." : "No works match.");
                var sb = new StringBuilder();
                foreach (var w in works.Take(100))
                    sb.AppendLine($"- {w.Title} (id {w.Id}; {WorkTemplates.Find(w.Template).Name}{(w.Pinned ? "; pinned" : "")}" +
                                  $"{(w.TodoTotal > 0 ? $"; to-dos {w.TodoDone}/{w.TodoTotal}" : "")}; edited {WorkService.Ago(w.Updated)}) {w.Summary}");
                return new AiToolResult(sb.ToString());
            }),
        };
        yield return new AiTool
        {
            Name = "read_work",
            Description = "Read a work: every row with its short id (in brackets), kind and content. Use the ids to change or delete rows.",
            Schema = AiTool.Object(WorkParam),
            Label = a => $"Reading {WorkName(a)}",
            Run = (a, _) => Ui(() => FindWork(a, out var work) is { } error ? error : new AiToolResult(WorkText.Describe(work!))),
        };
        yield return new AiTool
        {
            Name = "create_work",
            Description = "Create a new work, optionally from a template and with rows, and open it as a tab. " +
                          "Templates: " + string.Join(", ", WorkTemplates.All.Select(t => $"{t.Id} ({t.Name})")) + ".",
            Schema = WithBlocks(AiTool.Object(("title", "string", "Title of the work.", true),
                                              ("template", "string", "Template id (default blank). Its starter rows are replaced when you give blocks.", false),
                                              ("color", "string", "Color: " + string.Join(", ", AccentThemes.All.Select(t => t.Name)) + ".", false),
                                              ("open", "string", "Where to show it: tab (default), panel or none.", false)), required: false),
            Label = a => $"Creating “{AiToolbox.Str(a, "title")}”",
            Run = (a, _) => Ui(() =>
            {
                var work = _works.Library.Create(WorkTemplates.Find(AiToolbox.Str(a, "template") ?? "blank"));
                work.Title = AiToolbox.Str(a, "title") ?? work.Title;
                if (AiToolbox.Str(a, "color") is { } color && AccentThemes.All.Any(t => t.Name.Equals(color, StringComparison.OrdinalIgnoreCase)))
                    work.Color = AccentThemes.Find(color).Name;
                if (a["blocks"] is JsonArray blocks && WorkText.ParseBlocks(blocks) is { Count: > 0 } rows) work.Blocks = rows;
                _works.Library.Touch(work, listChanged: true);
                ShowWork(work, AiToolbox.Str(a, "open"));
                return new AiToolResult($"Created “{work.Title}” (id {work.Id}).\n\n{WorkText.Describe(work)}");
            }),
        };
        yield return new AiTool
        {
            Name = "add_work_rows",
            Description = "Add rows to a work: at the end, or after the row with the given id.",
            Schema = WithBlocks(AiTool.Object(WorkParam,
                                              ("after", "string", "Short id of the row to insert after; empty adds at the end.", false)), required: true),
            Label = a => $"Adding to {WorkName(a)}",
            Run = (a, _) => Ui(() =>
            {
                if (FindWork(a, out var work) is { } error) return error;
                var rows = a["blocks"] is JsonArray blocks ? WorkText.ParseBlocks(blocks) : new List<WorkBlock>();
                if (rows.Count == 0) return AiToolResult.Error("No rows to add.");
                var index = work!.Blocks.Count;
                if (AiToolbox.Str(a, "after") is { } after)
                {
                    if (WorkText.FindBlock(work, after) is not { } anchor) return AiToolResult.Error($"No row with id {after}.");
                    index = work.Blocks.IndexOf(anchor) + 1;
                }
                // A blank template leaves one empty text row: fill it rather than keep it above the new rows.
                if (work.Blocks is [{ Kind: BlockKind.Text, Text: "" }] && index == 1) { work.Blocks.Clear(); index = 0; }
                work.Blocks.InsertRange(index, rows);
                _works.Library.Touch(work, listChanged: true);
                return new AiToolResult($"Added {rows.Count} row(s) to “{work.Title}”.\n\n{WorkText.Describe(work)}");
            }),
        };
        yield return new AiTool
        {
            Name = "update_work_row",
            Description = "Change one row of a work: its text, value, checked state, number, heading level or kind. Only the given fields change.",
            Schema = AiTool.Object(WorkParam, ("row", "string", "Short id of the row (from read_work).", true),
                                   ("text", "string", "New text / variable name.", false), ("value", "string", "New field value.", false),
                                   ("checked", "boolean", "todo done / toggle on.", false), ("number", "number", "New number.", false),
                                   ("level", "integer", "Heading level 1-3.", false), ("kind", "string", "New kind: " + string.Join(", ", WorkText.Kinds) + ".", false)),
            Label = a => $"Updating {WorkName(a)}",
            Run = (a, _) => Ui(() =>
            {
                if (FindWork(a, out var work) is { } error) return error;
                if (WorkText.FindBlock(work!, AiToolbox.Str(a, "row") ?? "") is not { } block) return AiToolResult.Error("No such row. Read the work again for the ids.");
                if (AiToolbox.Str(a, "kind") is { } kind && WorkText.ParseKind(kind) is { } k) block.Kind = k;
                if (a["text"] is not null) block.Text = a["text"]!.ToString();
                if (a["value"] is not null) block.Value = a["value"]!.ToString();
                if (AiToolbox.Bool(a, "checked") is { } done) block.Checked = done;
                if (a["number"] is JsonValue n && double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) block.Number = number;
                if (AiToolbox.Int(a, "level") is { } level) block.Level = Math.Clamp(level, 1, 3);
                _works.Library.Touch(work!, listChanged: true);
                return new AiToolResult("Updated: " + WorkText.DescribeBlock(block));
            }),
        };
        yield return new AiTool
        {
            Name = "delete_work_rows",
            Description = "Delete rows from a work by their short ids.",
            Schema = AiTool.Object(WorkParam, ("rows", "string[]", "Short ids of the rows to delete.", true)),
            Label = a => $"Removing rows from {WorkName(a)}",
            Run = (a, _) => Ui(() =>
            {
                if (FindWork(a, out var work) is { } error) return error;
                var ids = (a["rows"] as JsonArray)?.Select(n => n?.ToString() ?? "").ToList() ?? new List<string>();
                var removed = ids.Select(id => WorkText.FindBlock(work!, id)).OfType<WorkBlock>().Distinct().ToList();
                if (removed.Count == 0) return AiToolResult.Error("None of those rows exist.");
                foreach (var block in removed) work!.Blocks.Remove(block);
                if (work!.Blocks.Count == 0) work.Blocks.Add(new WorkBlock());
                _works.Library.Touch(work, listChanged: true);
                return new AiToolResult($"Deleted {removed.Count} row(s) from “{work.Title}”.");
            }),
        };
        yield return new AiTool
        {
            Name = "update_work",
            Description = "Rename a work, recolor it, pin or unpin it.",
            Schema = AiTool.Object(WorkParam, ("title", "string", "New title.", false),
                                   ("color", "string", "Color: " + string.Join(", ", AccentThemes.All.Select(t => t.Name)) + ".", false),
                                   ("icon", "string", "Icon: " + string.Join(", ", WorkStyle.Icons) + ".", false),
                                   ("pinned", "boolean", "Pinned to the top of the list.", false)),
            Label = a => $"Updating {WorkName(a)}",
            Run = (a, _) => Ui(() =>
            {
                if (FindWork(a, out var work) is { } error) return error;
                if (AiToolbox.Str(a, "title") is { } title) work!.Title = title;
                if (AiToolbox.Str(a, "color") is { } color && AccentThemes.All.Any(t => t.Name.Equals(color, StringComparison.OrdinalIgnoreCase)))
                    work!.Color = AccentThemes.Find(color).Name;
                if (AiToolbox.Str(a, "icon") is { } icon && WorkStyle.Icons.Contains(icon)) work!.Icon = icon;
                if (AiToolbox.Bool(a, "pinned") is { } pinned) work!.Pinned = pinned;
                _works.Library.Touch(work!, listChanged: true);
                return new AiToolResult($"Updated “{work!.Title}”.");
            }),
        };
        yield return new AiTool
        {
            Name = "delete_work",
            Description = "Delete a whole work. Only when the user clearly asked for it. A copy is kept in the works-trash folder.",
            Schema = AiTool.Object(WorkParam),
            Label = a => $"Deleting {WorkName(a)}",
            Approval = a => $"Delete the work {WorkName(a)}?",
            Run = (a, _) => Ui(() =>
            {
                if (FindWork(a, out var work) is { } error) return error;
                _works.Flush();
                var trash = Path.Combine(_store.Directory, "works-trash");
                try
                {
                    Directory.CreateDirectory(trash);
                    var source = Path.Combine(_works.Library.Folder, work!.Id + ".json");
                    if (File.Exists(source)) File.Copy(source, Path.Combine(trash, $"{work.Id}.json"), overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return AiToolResult.Error("Could not keep a backup copy, so the work was not deleted: " + ex.Message);
                }
                _works.Library.Delete(work);
                ShowToast($"AI deleted “{work.Title}” (a copy is in works-trash)", "IconTrash");
                return new AiToolResult($"Deleted “{work.Title}”. A backup is in {trash}.");
            }),
        };
        yield return new AiTool
        {
            Name = "open_work",
            Description = "Show a work to the user: as its own tab, or in the Works tool of the side panel.",
            Schema = AiTool.Object(WorkParam, ("where", "string", "tab (default) or panel.", false)),
            Label = a => $"Opening {WorkName(a)}",
            Run = (a, _) => Ui(() =>
            {
                if (FindWork(a, out var work) is { } error) return error;
                ShowWork(work!, AiToolbox.Str(a, "where") ?? "tab");
                return new AiToolResult($"Opened “{work!.Title}”.");
            }),
        };
    }

    private void ShowWork(Work work, string? where)
    {
        switch (where?.ToLowerInvariant())
        {
            case "none": break;
            // The AI panel lives in the side panel too: putting the work there would hide the chat.
            case "panel" when _activeTool?.Id != AiToolId: _works.OpenInPanel(work); break;
            default: _works.OpenInTab(work); break;
        }
    }

    /// <summary>The work's title for chat lines. Labels are built on the caller's thread, so the lookup hops to the UI thread.</summary>
    private string WorkName(JsonObject args)
    {
        var key = AiToolbox.Str(args, "work") ?? "";
        string? Lookup() => FindWork(args, out var work) is null ? work!.Title : null;
        var title = Dispatcher.UIThread.CheckAccess() ? Lookup() : Dispatcher.UIThread.Invoke(Lookup);
        return $"“{title ?? key}”";
    }

    private AiToolResult? FindWork(JsonObject args, out Work? work)
    {
        var key = AiToolbox.Str(args, "work") ?? "";
        var works = _works.Library.Works;
        work = _works.Library.Find(key)
               ?? works.FirstOrDefault(w => w.Title.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? works.FirstOrDefault(w => w.Title.Contains(key, StringComparison.OrdinalIgnoreCase));
        return work is null ? AiToolResult.Error($"No work matches “{key}”. Use list_works.") : null;
    }
}
