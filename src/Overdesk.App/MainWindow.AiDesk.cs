using System.Text;
using System.Text.Json.Nodes;
using Overdesk.AI;
using Overdesk.App.Services;
using Overdesk.Core.Platform;
using Overdesk.Core.Tabs;

namespace Overdesk.App;

// AI tools for the rest of the desk: the tasks OverDesk runs (dev servers, builds) and their logs, local dev ports,
// the media player, system load and the dock's scratchpad.
public partial class MainWindow
{
    private IEnumerable<AiTool> DeskTools()
    {
        yield return new AiTool
        {
            Name = "read_tasks",
            Description = "The commands OverDesk runs for the user (project dev servers, builds, AI commands) with their state. " +
                          "Give a name to also get the end of that task's log, e.g. to see why a dev server crashed.",
            Schema = AiTool.Object(("name", "string", "Task name (a part is enough) whose log you want.", false),
                                   ("lines", "integer", "How many log lines from the end (default 120).", false)),
            Label = a => AiToolbox.Str(a, "name") is { } n ? $"Reading the log of {n}" : "Looking at running tasks",
            Run = (a, _) => Ui(() =>
            {
                if (_runner.Tasks.Count == 0) return new AiToolResult("No tasks have run in this session.");
                var sb = new StringBuilder();
                foreach (var t in _runner.Tasks.Take(30))
                    sb.AppendLine($"- {t.Name} [{(t.IsRunning ? "running" : $"exited {t.ExitCode}")}] in {t.Folder}, started {t.Started:HH:mm:ss}: {t.Command}");
                if (AiToolbox.Str(a, "name") is { } name)
                {
                    var task = _runner.Tasks.FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                                                                 || t.Command.Contains(name, StringComparison.OrdinalIgnoreCase));
                    if (task is null) return AiToolResult.Error($"No task matches “{name}”.\n{sb}");
                    var lines = task.Output.Split('\n');
                    var count = Math.Clamp(AiToolbox.Int(a, "lines") ?? 120, 10, 1000);
                    sb.AppendLine().AppendLine($"Last {Math.Min(count, lines.Length)} lines of {task.Name}:")
                      .AppendLine(string.Join('\n', lines.Skip(Math.Max(0, lines.Length - count))));
                }
                return new AiToolResult(sb.ToString());
            }),
        };
        yield return new AiTool
        {
            Name = "dev_servers",
            Description = "Local servers listening on this computer (ports 1024-49151) with the program behind each, e.g. to find the URL of a running app.",
            Schema = AiTool.Object(),
            Label = _ => "Checking local servers",
            Run = async (_, _) =>
            {
                // The refresh tells the dock, which must happen on the UI thread.
                var ports = await Ui(async () =>
                {
                    await _stats.RefreshPortsAsync();
                    return _stats.Ports.Where(p => p.Port is >= 1024 and < 49152).OrderBy(p => p.Port).ToList();
                });
                return new AiToolResult(ports.Count == 0
                    ? "No local servers are listening."
                    : string.Join('\n', ports.Select(p => $"- http://localhost:{p.Port}  ({p.ProcessName}, pid {p.ProcessId})")));
            },
        };
        yield return new AiTool
        {
            Name = "media",
            Description = "The music or video playing on the computer (Apple Music / Spotify on macOS, any player on Windows): " +
                          "see what is playing, play or pause, skip to the next or previous track.",
            Schema = AiTool.Object(("action", "string", "status (default), play_pause, next or previous.", false)),
            Label = a => (AiToolbox.Str(a, "action") ?? "status") switch
            {
                "play_pause" => "Play / pause",
                "next" => "Next track",
                "previous" => "Previous track",
                _ => "Checking what is playing",
            },
            Run = async (a, _) =>
            {
                var action = AiToolbox.Str(a, "action") ?? "status";
                await Ui(async () =>
                {
                    switch (action)
                    {
                        case "play_pause": await _nowPlaying.PlayPauseAsync(); break;
                        case "next": await _nowPlaying.NextAsync(); break;
                        case "previous": await _nowPlaying.PreviousAsync(); break;
                    }
                    return true;
                });
                if (action is "next" or "previous") await Task.Delay(900);
                return await Ui(() =>
                {
                    if (_nowPlaying.Current is not { } now) return new AiToolResult("Nothing is playing (or no supported player is open).");
                    return new AiToolResult($"{(now.IsPlaying ? "Playing" : "Paused")}: {now.Title} by {now.Artist}" +
                                            $"{(now.Album.Length > 0 ? $" ({now.Album})" : "")} in {now.SourceName}, " +
                                            $"{_nowPlaying.Position:m\\:ss} of {now.Duration:m\\:ss}.");
                });
            },
        };
        yield return new AiTool
        {
            Name = "system_status",
            Description = "How busy this computer is: CPU, GPU, memory, network, disk, swap, battery, uptime and process count.",
            Schema = AiTool.Object(),
            Label = _ => "Checking the computer",
            Run = (_, _) => Ui(() =>
            {
                var sb = new StringBuilder();
                foreach (var metric in SystemStatsService.Catalog)
                    if (_stats.Read(metric.Id) is { } reading) sb.AppendLine($"- {metric.Label}: {reading.Value}");
                return new AiToolResult(sb.Length > 0 ? sb.ToString() : "No readings yet.");
            }),
        };
        yield return new AiTool
        {
            Name = "scratchpad",
            Description = "The quick-notes scratchpad in OverDesk's left dock: read it, add a line at the end, or replace it all. " +
                          "For anything longer or structured, use works instead.",
            Schema = AiTool.Object(("action", "string", "read (default), append or replace.", false),
                                   ("text", "string", "Text to add or the new content.", false)),
            Label = a => (AiToolbox.Str(a, "action") ?? "read") switch
            {
                "append" => "Adding to the scratchpad",
                "replace" => "Rewriting the scratchpad",
                _ => "Reading the scratchpad",
            },
            Run = (a, _) => Ui(() =>
            {
                var text = a["text"]?.ToString() ?? "";
                switch (AiToolbox.Str(a, "action") ?? "read")
                {
                    case "append":
                        Dock.SetNotes(_settings.Notes.Length == 0 ? text : _settings.Notes.TrimEnd('\n') + "\n" + text);
                        break;
                    case "replace":
                        Dock.SetNotes(text);
                        break;
                }
                return new AiToolResult(_settings.Notes.Length == 0 ? "(the scratchpad is empty)" : _settings.Notes);
            }),
        };
    }

    /// <summary>The visible text of a web tab (its title, address and up to 40,000 characters of text).</summary>
    private async Task<AiToolResult> ReadWebPageAsync(WebTab tab)
    {
        // A tab opened a moment ago (open_website, then read) may still be creating its view or loading.
        IWebView? view = null;
        for (var i = 0; i < 40; i++)
        {
            if (_webHosts.TryGetValue(tab.Id, out var host) && host.View is { } v)
            {
                view = v;
                if (!v.IsLoading) break;
            }
            await Task.Delay(250);
        }
        if (view is null) return AiToolResult.Error($"“{tab.Title}” has not loaded yet: switch to it once, then try again.");
        const string script = "(() => JSON.stringify({ title: document.title, url: location.href, " +
                              "text: (document.body ? document.body.innerText : '').slice(0, 40000) }))()";
        var json = await view.ExecuteScriptAsync(script);
        if (json is null) return AiToolResult.Error("The page returned nothing.");
        var page = JsonNode.Parse(json);
        var text = page?["text"]?.GetValue<string>() ?? "";
        return new AiToolResult($"{page?["title"]} ({page?["url"]})\n\n{(text.Length > 0 ? text : "(no text on the page)")}");
    }

    private IEnumerable<AiTool> WebTools()
    {
        yield return new AiTool
        {
            Name = "read_web_page",
            Description = "Read the text of a web tab open in OverDesk (the one on stage, or the one whose title you give), " +
                          "e.g. to summarize an article, answer questions about a page or pick data out of it.",
            Schema = AiTool.Object(("title", "string", "Title or address of the web tab (a part is enough); empty for the one on stage.", false)),
            Label = a => AiToolbox.Str(a, "title") is { } t ? $"Reading the page {t}" : "Reading the web page",
            Run = (a, _) => Ui(async () =>
            {
                var title = AiToolbox.Str(a, "title");
                var tab = title is null ? _activeWeb : _webTabs.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)
                                                                                 || w.Url.Contains(title, StringComparison.OrdinalIgnoreCase));
                if (tab is null)
                    return AiToolResult.Error(title is null ? "No web page is on stage. Open one with open_website, or name a web tab." : $"No web tab matches “{title}”.");
                return await ReadWebPageAsync(tab);
            }),
        };
    }

    private IEnumerable<AiTool> LayoutTools()
    {
        yield return new AiTool
        {
            Name = "workspace",
            Description = "OverDesk's workspaces: named sets of tabs (e.g. \"Coding\", \"Music\"). List them, switch to one, or create a new " +
                          "empty one and switch to it (then open programs into it). Programs of other workspaces keep running hidden.",
            Schema = AiTool.Object(("action", "string", "list (default), switch or create.", false), ("name", "string", "Workspace name.", false)),
            Label = a => (AiToolbox.Str(a, "action") ?? "list") switch
            {
                "switch" => $"Switching to the {AiToolbox.Str(a, "name")} workspace",
                "create" => $"Creating the {AiToolbox.Str(a, "name")} workspace",
                _ => "Looking at workspaces",
            },
            Run = (a, _) => Ui(() =>
            {
                var name = AiToolbox.Str(a, "name")?.Trim();
                var existing = name is null ? null
                    : _settings.Workspaces.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                      ?? _settings.Workspaces.FirstOrDefault(w => w.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                switch (AiToolbox.Str(a, "action") ?? "list")
                {
                    case "switch" when existing is not null:
                        SwitchWorkspace(existing);
                        return new AiToolResult($"Switched to {existing.Name}.");
                    case "switch":
                        return AiToolResult.Error($"No workspace is called “{name}”.");
                    case "create" when name is { Length: > 0 }:
                        var ws = existing?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true ? existing : NewWorkspace(name);
                        SwitchWorkspace(ws);
                        return new AiToolResult($"Now in the {ws.Name} workspace{(ws == existing ? " (it already existed)" : " (new and empty)")}.");
                    case "create":
                        return AiToolResult.Error("A name is needed.");
                    default:
                        return new AiToolResult(string.Join('\n', _settings.Workspaces.Select(w =>
                            $"- {w.Name}{(w == CurrentWorkspace ? " (current)" : "")}: {w.Tabs.Count} tab(s)")));
                }
            }),
        };
        yield return new AiTool
        {
            Name = "split_view",
            Description = "Show two program tabs side by side on the stage (left and right), or end the split view.",
            Schema = AiTool.Object(("left", "string", "Title of the program tab on the left.", false),
                                   ("right", "string", "Title of the program tab on the right.", false),
                                   ("end", "boolean", "true to end the split view.", false)),
            Label = a => AiToolbox.Bool(a, "end") == true ? "Ending the split view" : $"Splitting {AiToolbox.Str(a, "left")} | {AiToolbox.Str(a, "right")}",
            Run = (a, _) => Ui(() =>
            {
                if (AiToolbox.Bool(a, "end") == true)
                {
                    EndSplit();
                    return new AiToolResult("Split view ended.");
                }
                if (FindTab(AiToolbox.Str(a, "left") ?? "") is not { IsInternal: false } left || FindTab(AiToolbox.Str(a, "right") ?? "") is not { IsInternal: false } right)
                    return AiToolResult.Error("Both need to be open program tabs (not works or settings). Open them with open_program first.");
                if (left == right) return AiToolResult.Error("Pick two different tabs.");
                _activeWeb = null;
                _tabs.Activate(left);
                SplitWith(right);
                return new AiToolResult($"{left.Title} | {right.Title} side by side.");
            }),
        };
    }

    /// <summary>One line about what the user is looking at, added to every message so "this" makes sense.</summary>
    private string StageSummary()
    {
        if (_activeWeb is { } web) return $"On stage right now: the web page “{web.Title}” ({web.Url}).";
        if (_tabs.Active is { } tab)
            return tab.IsWork ? $"On stage right now: the work “{tab.Title}” (id {tab.WorkId})."
                : tab.IsSettings ? "On stage right now: OverDesk's settings."
                : $"On stage right now: the program “{tab.Title}”{(tab.Status == Overdesk.Core.Tabs.TabStatus.Running ? "" : $" ({tab.Status.ToString().ToLowerInvariant()})")}.";
        return "On stage right now: OverDesk's home page.";
    }
}
