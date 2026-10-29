using System.Text;
using Overdesk.AI;
using Overdesk.App.Services;

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
                await _stats.RefreshPortsAsync();
                var ports = _stats.Ports.Where(p => p.Port is >= 1024 and < 49152).OrderBy(p => p.Port).ToList();
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
