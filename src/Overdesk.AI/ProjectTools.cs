using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Overdesk.AI;

/// <summary>
/// The project folder the user picked in the AI panel: a short overview for the system prompt (so answers start from
/// the real project) and file tools to look deeper and, when allowed, change files. Every path is kept inside the
/// folder; .git is never written.
/// </summary>
public static class ProjectTools
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", ".vscode", "dist", "build", "out", ".next", ".nuxt", "target",
        "__pycache__", ".venv", "venv", ".gradle", "Library", "Temp", "Logs", "DerivedData", "Pods", ".dart_tool", "coverage",
    };

    private static readonly string[] Manifests =
    {
        "package.json", "Cargo.toml", "go.mod", "pyproject.toml", "requirements.txt", "pom.xml", "build.gradle", "build.gradle.kts",
        "Gemfile", "composer.json", "pubspec.yaml", "Package.swift", "CMakeLists.txt", "Makefile", "Dockerfile", "docker-compose.yml",
    };

    private const int MaxRead = 2000;
    private const long MaxFileBytes = 2_000_000;

    // ---------------------------------------------------------------- overview

    /// <summary>A compact picture of the project: tree, stack, README and agent notes. Kept under ~8k characters.</summary>
    public static string Overview(string folder)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Project folder: {folder} (name: {Path.GetFileName(folder.TrimEnd('/', '\\'))})");
        try
        {
            var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var files = 0;
            foreach (var file in Walk(folder, int.MaxValue).Take(20000))
            {
                files++;
                var ext = Path.GetExtension(file);
                if (ext.Length > 0) stats[ext] = stats.GetValueOrDefault(ext) + 1;
            }
            sb.AppendLine($"Files (outside build/dependency folders): {files}"
                          + (stats.Count > 0 ? " · mostly " + string.Join(", ", stats.OrderByDescending(s => s.Value).Take(6).Select(s => $"{s.Key} ×{s.Value}")) : ""));

            var stack = Manifests.Where(m => File.Exists(Path.Combine(folder, m))).ToList();
            stack.AddRange(Directory.EnumerateFiles(folder, "*.sln*").Concat(Directory.EnumerateFiles(folder, "*.csproj")).Select(Path.GetFileName)!);
            if (stack.Count > 0) sb.AppendLine("Build files at the top: " + string.Join(", ", stack.Distinct()));

            sb.AppendLine().AppendLine("Layout (2 levels):");
            sb.Append(Tree(folder, 2, 120));

            foreach (var note in new[] { "CLAUDE.md", "AGENTS.md", "README.md", "readme.md", "README" })
            {
                var path = Path.Combine(folder, note);
                if (!File.Exists(path)) continue;
                var lines = File.ReadLines(path).Take(60).ToList();
                sb.AppendLine().AppendLine($"--- {note} (first {lines.Count} lines) ---");
                foreach (var line in lines) sb.AppendLine(line);
                if (sb.Length > 7000) break;
            }
            if (File.Exists(Path.Combine(folder, "package.json")))
            {
                var json = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "package.json")));
                if (json?["scripts"] is JsonObject scripts)
                    sb.AppendLine().AppendLine("package.json scripts: " + string.Join(", ", scripts.Select(s => s.Key)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            sb.AppendLine("(Part of the folder could not be read: " + ex.Message + ")");
        }
        var text = sb.ToString();
        return text.Length > 8000 ? text[..8000] + "\n…" : text;
    }

    private static string Tree(string root, int depth, int limit)
    {
        var sb = new StringBuilder();
        var count = 0;
        void Visit(string dir, int level)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).OrderBy(e => !Directory.Exists(e)).ThenBy(e => e, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') && name != ".github") continue;
                var isDir = Directory.Exists(entry);
                if (count++ >= limit) { sb.AppendLine(new string(' ', level * 2) + "…"); return; }
                sb.AppendLine(new string(' ', level * 2) + name + (isDir ? "/" : ""));
                if (isDir && level + 1 < depth && !Skipped.Contains(name)) Visit(entry, level + 1);
            }
        }
        Visit(root, 0);
        return sb.ToString();
    }

    /// <summary>Files under a folder, skipping dependency/build folders and hidden ones.</summary>
    private static IEnumerable<string> Walk(string dir, int depth)
    {
        var stack = new Stack<(string Dir, int Level)>();
        stack.Push((dir, 0));
        while (stack.Count > 0)
        {
            var (current, level) = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) yield return f;
            if (level >= depth) continue;
            foreach (var d in dirs.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(d);
                if (Skipped.Contains(name) || (name.StartsWith('.') && name != ".github")) continue;
                if (new DirectoryInfo(d).LinkTarget is not null) continue;
                stack.Push((d, level + 1));
            }
        }
    }

    // ---------------------------------------------------------------- tools

    /// <summary>The file tools for a folder. Write tools only when <paramref name="allowEdits"/>.</summary>
    public static IReadOnlyList<AiTool> Create(string folder, bool allowEdits, Action<string>? changed = null)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tools = new List<AiTool>
        {
            new()
            {
                Name = "list_files", IsFileTool = true,
                Description = "List files and folders in the project folder (dependency and build folders are skipped). Paths are relative to the project root.",
                Schema = AiTool.Object(("path", "string", "Folder inside the project, relative to its root. Empty for the root.", false),
                                       ("depth", "integer", "How many levels to go down (1-6, default 2).", false)),
                Label = a => $"Listing {AiToolbox.Str(a, "path") ?? "the project"}",
                Run = (a, _) => Task.Run(() =>
                {
                    var dir = Resolve(root, AiToolbox.Str(a, "path") ?? "");
                    if (!Directory.Exists(dir)) return AiToolResult.Error("No such folder.");
                    var tree = Tree(dir, Math.Clamp(AiToolbox.Int(a, "depth") ?? 2, 1, 6), 500);
                    return new AiToolResult(tree.Length > 0 ? tree : "(empty folder)");
                }),
            },
            new()
            {
                Name = "read_file", IsFileTool = true,
                Description = $"Read a text file of the project with line numbers. Long files: read in parts with offset (first line, 1-based) and limit (max {MaxRead} lines).",
                Schema = AiTool.Object(("path", "string", "File path relative to the project root.", true),
                                       ("offset", "integer", "First line to read (1-based).", false),
                                       ("limit", "integer", $"Number of lines (default {MaxRead}).", false)),
                Label = a => $"Reading {AiToolbox.Str(a, "path")}",
                Run = (a, ct) => ReadAsync(root, a, ct),
            },
            new()
            {
                Name = "search_files", IsFileTool = true,
                Description = "Search the text of the project's files with a regular expression (case-insensitive). Returns file:line: text for up to 150 matches.",
                Schema = AiTool.Object(("pattern", "string", "Regular expression (plain words work too).", true),
                                       ("glob", "string", "Only file names matching this pattern, e.g. *.cs or *.tsx.", false),
                                       ("path", "string", "Only inside this folder (relative).", false)),
                Label = a => $"Searching for “{AiToolbox.Str(a, "pattern")}”",
                Run = (a, ct) => Task.Run(() => Search(root, a, ct), ct),
            },
        };
        if (!allowEdits) return tools;

        tools.Add(new AiTool
        {
            Name = "write_file", IsFileTool = true,
            Description = "Create a file in the project or replace a file's whole content. Prefer edit_file for changes to existing files.",
            Schema = AiTool.Object(("path", "string", "File path relative to the project root.", true),
                                   ("content", "string", "The complete new file content.", true)),
            Label = a => $"Writing {AiToolbox.Str(a, "path")}",
            Run = async (a, ct) =>
            {
                var path = ResolveWritable(root, AiToolbox.Str(a, "path"));
                var content = a["content"]?.GetValue<string>() ?? "";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var existed = File.Exists(path);
                await File.WriteAllTextAsync(path, content, ct);
                changed?.Invoke(path);
                return new AiToolResult($"{(existed ? "Replaced" : "Created")} {Relative(root, path)} ({content.Split('\n').Length} lines).");
            },
        });
        tools.Add(new AiTool
        {
            Name = "edit_file", IsFileTool = true,
            Description = "Change part of a project file: old_text must appear exactly once (include enough surrounding lines), unless replace_all is true.",
            Schema = AiTool.Object(("path", "string", "File path relative to the project root.", true),
                                   ("old_text", "string", "The exact text to replace, copied from read_file without line numbers.", true),
                                   ("new_text", "string", "The replacement text.", true),
                                   ("replace_all", "boolean", "Replace every occurrence.", false)),
            Label = a => $"Editing {AiToolbox.Str(a, "path")}",
            Run = async (a, ct) =>
            {
                var path = ResolveWritable(root, AiToolbox.Str(a, "path"));
                if (!File.Exists(path)) return AiToolResult.Error("No such file. Use write_file to create it.");
                var text = await File.ReadAllTextAsync(path, ct);
                var old = a["old_text"]?.GetValue<string>() ?? "";
                var replacement = a["new_text"]?.GetValue<string>() ?? "";
                if (old.Length == 0) return AiToolResult.Error("old_text is empty.");
                // Files with Windows line endings: match the text the model copied with plain newlines.
                if (!text.Contains(old) && text.Contains("\r\n"))
                {
                    old = old.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    replacement = replacement.Replace("\r\n", "\n").Replace("\n", "\r\n");
                }
                var count = Count(text, old);
                if (count == 0) return AiToolResult.Error("old_text was not found. Read the file again and copy the text exactly.");
                if (count > 1 && AiToolbox.Bool(a, "replace_all") != true)
                    return AiToolResult.Error($"old_text appears {count} times. Add more surrounding lines, or set replace_all.");
                await File.WriteAllTextAsync(path, text.Replace(old, replacement), ct);
                changed?.Invoke(path);
                return new AiToolResult($"Edited {Relative(root, path)} ({count} change{(count == 1 ? "" : "s")}).");
            },
        });
        return tools;
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static async Task<AiToolResult> ReadAsync(string root, JsonObject a, CancellationToken ct)
    {
        var path = Resolve(root, AiToolbox.Str(a, "path") ?? "");
        if (Directory.Exists(path)) return AiToolResult.Error("That is a folder. Use list_files.");
        if (!File.Exists(path)) return AiToolResult.Error("No such file.");
        if (new FileInfo(path).Length > MaxFileBytes * 5) return AiToolResult.Error("The file is too large to read.");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (bytes.Take(8000).Contains((byte)0)) return AiToolResult.Error("This is a binary file.");
        var lines = Encoding.UTF8.GetString(bytes).Split('\n');
        var offset = Math.Max(1, AiToolbox.Int(a, "offset") ?? 1);
        var limit = Math.Clamp(AiToolbox.Int(a, "limit") ?? MaxRead, 1, MaxRead);
        var sb = new StringBuilder();
        for (var i = offset - 1; i < lines.Length && i < offset - 1 + limit; i++)
        {
            var line = lines[i].TrimEnd('\r');
            sb.Append((i + 1).ToString().PadLeft(5)).Append("  ").AppendLine(line.Length > 2000 ? line[..2000] + "…" : line);
        }
        if (offset - 1 + limit < lines.Length) sb.AppendLine($"… ({lines.Length} lines in all; read on with offset {offset + limit})");
        return new AiToolResult(sb.Length > 0 ? sb.ToString() : "(empty file)");
    }

    private static AiToolResult Search(string root, JsonObject a, CancellationToken ct)
    {
        var pattern = AiToolbox.Str(a, "pattern") ?? "";
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
        catch (ArgumentException) { regex = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase); }
        var glob = AiToolbox.Str(a, "glob") is { } g ? new Regex("^" + Regex.Escape(g).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase) : null;
        var start = Resolve(root, AiToolbox.Str(a, "path") ?? "");

        var sb = new StringBuilder();
        var matches = 0;
        foreach (var file in Walk(start, 30))
        {
            ct.ThrowIfCancellationRequested();
            if (glob is not null && !glob.IsMatch(Path.GetFileName(file))) continue;
            try
            {
                if (new FileInfo(file).Length > MaxFileBytes) continue;
                var number = 0;
                foreach (var line in File.ReadLines(file))
                {
                    number++;
                    if (line.Contains('\0')) break;
                    bool hit;
                    try { hit = regex.IsMatch(line); } catch (RegexMatchTimeoutException) { hit = false; }
                    if (!hit) continue;
                    var text = line.Trim();
                    sb.Append(Relative(root, file)).Append(':').Append(number).Append(": ").AppendLine(text.Length > 240 ? text[..240] + "…" : text);
                    if (++matches >= 150) return new AiToolResult(sb + "… (stopped at 150 matches; narrow the search)");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new AiToolResult(matches == 0 ? "No matches." : sb.ToString());
    }

    // ---------------------------------------------------------------- paths

    /// <summary>A path inside the project, or an error when it would leave it.</summary>
    public static string Resolve(string root, string relative)
    {
        relative = relative.Trim();
        var full = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!full.Equals(root, comparison) && !full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("That path is outside the project folder.");
        return full;
    }

    private static string ResolveWritable(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException("A file path is needed.");
        var full = Resolve(root, relative);
        var parts = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Files inside .git are not changed.");
        if (Directory.Exists(full)) throw new InvalidOperationException("That is a folder.");
        // A link could point anywhere: the real target must be inside the project too.
        if (File.Exists(full) && new FileInfo(full).LinkTarget is { } target)
            Resolve(root, Path.GetFullPath(target, Path.GetDirectoryName(full)!));
        return full;
    }

    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
