using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Overdesk.Core.Text;
using Overdesk.Tools.Shared;
using SkiaSharp;

namespace Overdesk.Tool.Converter;

public enum FileKind { Image, Data, Text, Document, Audio, Video, Archive, Font, Model, Unknown }

public sealed record ConvertOptions
{
    /// <summary>JPEG / WebP quality, 1-100.</summary>
    public int Quality { get; init; } = 90;

    /// <summary>Images: longest edge in pixels; null keeps the size.</summary>
    public int? MaxSize { get; init; }

    /// <summary>Where results go; null puts them next to the original.</summary>
    public string? OutputFolder { get; init; }
}

/// <summary>
/// What can become what, and doing it. Images go through SkiaSharp (the library OverDesk draws with), data, Markdown,
/// archives, fonts and 3D models are converted in code, and documents, audio and video use tools already on the
/// computer: ffmpeg when it is installed, otherwise the converters macOS ships (sips, textutil, afconvert, avconvert).
/// Every file, whatever it is, can at least be packed into a ZIP or GZ.
/// </summary>
public static partial class Engine
{
    private static readonly Dictionary<string, FileKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["json"] = FileKind.Data, ["yaml"] = FileKind.Data, ["xlsx"] = FileKind.Data, ["yml"] = FileKind.Data, ["csv"] = FileKind.Data, ["tsv"] = FileKind.Data, ["xml"] = FileKind.Data,
        ["toml"] = FileKind.Data, ["ini"] = FileKind.Data, ["cfg"] = FileKind.Data, ["env"] = FileKind.Data, ["properties"] = FileKind.Data,
        ["jsonl"] = FileKind.Data, ["ndjson"] = FileKind.Data, ["plist"] = FileKind.Data,
        ["md"] = FileKind.Text, ["markdown"] = FileKind.Text, ["txt"] = FileKind.Text, ["srt"] = FileKind.Text, ["vtt"] = FileKind.Text,
        ["docx"] = FileKind.Document, ["doc"] = FileKind.Document, ["rtf"] = FileKind.Document, ["odt"] = FileKind.Document,
        ["html"] = FileKind.Document, ["htm"] = FileKind.Document, ["webarchive"] = FileKind.Document,
        ["mp3"] = FileKind.Audio, ["wav"] = FileKind.Audio, ["m4a"] = FileKind.Audio, ["aac"] = FileKind.Audio, ["flac"] = FileKind.Audio,
        ["ogg"] = FileKind.Audio, ["opus"] = FileKind.Audio, ["aiff"] = FileKind.Audio, ["aif"] = FileKind.Audio, ["caf"] = FileKind.Audio,
        ["wma"] = FileKind.Audio, ["amr"] = FileKind.Audio,
        ["mp4"] = FileKind.Video, ["mov"] = FileKind.Video, ["mkv"] = FileKind.Video, ["webm"] = FileKind.Video, ["avi"] = FileKind.Video,
        ["m4v"] = FileKind.Video, ["wmv"] = FileKind.Video, ["flv"] = FileKind.Video, ["mpg"] = FileKind.Video, ["mpeg"] = FileKind.Video,
        ["3gp"] = FileKind.Video, ["ts"] = FileKind.Video, ["mts"] = FileKind.Video,
        ["zip"] = FileKind.Archive, ["tar"] = FileKind.Archive, ["gz"] = FileKind.Archive, ["tgz"] = FileKind.Archive,
        ["ttf"] = FileKind.Font, ["otf"] = FileKind.Font, ["woff"] = FileKind.Font,
        ["obj"] = FileKind.Model, ["stl"] = FileKind.Model, ["ply"] = FileKind.Model,
        ["pdf"] = FileKind.Image, // pages are rendered through macOS's sips
    };

    static Engine()
    {
        foreach (var ext in ImageIO.Readable) Kinds[ext] = FileKind.Image;
    }

    /// <summary>Images any computer reads (SkiaSharp + our TGA reader); the rest need macOS.</summary>
    private static readonly HashSet<string> PortableImages = new() { "png", "jpg", "jpeg", "webp", "gif", "bmp", "ico", "tga", "wbmp" };

    /// <summary>Formats a tree of data can be written as, and the table ones.</summary>
    private static readonly string[] TreeFormats = { "json", "yaml", "toml", "xml", "plist", "ini", "env", "properties" };
    private static readonly string[] TableFormats = { "xlsx", "csv", "tsv", "jsonl", "md", "html", "sql" };

    public static string Ext(string path) => Archives.Format(path) == "tar.gz" ? "tar.gz" : Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static FileKind KindOf(string path) => Ext(path) == "tar.gz" ? FileKind.Archive : Kinds.GetValueOrDefault(Ext(path), FileKind.Unknown);

    private static bool Mac => OperatingSystem.IsMacOS();
    private static bool HasFfmpeg => Cli.Find("ffmpeg") is not null;

    /// <summary>The formats this file can be turned into on this computer (extensions, best first).</summary>
    public static IReadOnlyList<string> TargetsFor(string path)
    {
        var ext = Ext(path) switch { "jpeg" => "jpg", "htm" => "html", "markdown" => "md", "tif" => "tiff", "aif" => "aiff", "yml" => "yaml", "ndjson" => "jsonl", "tgz" => "tar.gz", var e => e };
        IEnumerable<string> targets = KindOf(path) switch
        {
            FileKind.Image when !Mac && !PortableImages.Contains(ext) => Array.Empty<string>(),
            FileKind.Image => new[] { "png", "jpg", "webp", "pdf", "tga", "bmp", "ico" }.Concat(Mac ? new[] { "heic", "tiff" } : Array.Empty<string>()),
            FileKind.Data => ext is "csv" or "tsv" or "xlsx" or "jsonl" ? TableFormats.Concat(TreeFormats) : TreeFormats.Concat(TableFormats),
            FileKind.Text when ext is "srt" or "vtt" => new[] { ext == "srt" ? "vtt" : "srt" },
            FileKind.Text => ext == "md"
                ? new[] { "pdf", "html" }.Concat(Mac ? new[] { "docx", "rtf", "odt" } : Array.Empty<string>())
                : Mac ? new[] { "pdf", "docx", "rtf", "html", "odt" } : new[] { "pdf", "html" },
            FileKind.Document => Mac ? new[] { "pdf", "txt", "docx", "html", "rtf", "odt" } : ext == "html" ? new[] { "pdf", "txt" } : Array.Empty<string>(),
            FileKind.Audio => HasFfmpeg ? new[] { "mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "aac" } : Mac ? new[] { "m4a", "wav", "aiff", "flac", "caf" } : Array.Empty<string>(),
            FileKind.Video => HasFfmpeg
                ? new[] { "mp4", "mov", "webm", "mkv", "avi", "gif", "jpg", "mp3", "m4a", "wav" }
                : Mac ? new[] { "mp4", "mov", "m4v" } : Array.Empty<string>(),
            FileKind.Archive => new[] { "folder", "zip", "tar", "tar.gz" },
            FileKind.Font => ext == "woff" ? new[] { Fonts.WoffFlavor(path) } : new[] { "woff" },
            FileKind.Model => new[] { "glb", "gltf", "obj", "stl", "ply" },
            _ => Array.Empty<string>(),
        };
        // Anything at all can be packed.
        var all = targets.Where(t => t != ext).Distinct().ToList();
        if (KindOf(path) != FileKind.Archive) all.AddRange(new[] { "zip", "gz" }.Where(t => !all.Contains(t)));
        return all;
    }

    /// <summary>What is missing for more formats of this kind, or null.</summary>
    public static string? Hint(FileKind kind) => kind switch
    {
        FileKind.Audio or FileKind.Video when !HasFfmpeg => Mac
            ? "Install ffmpeg for more formats (MP3, WebM, MKV, GIF…): brew install ffmpeg"
            : "Install ffmpeg to convert audio and video: winget install ffmpeg",
        FileKind.Document when !Mac => "Word, RTF and ODT conversion uses macOS's textutil; on Windows only HTML → text works.",
        FileKind.Image when !Mac => "HEIC, AVIF, PSD, RAW, TIFF, SVG and PDF pages are read with macOS's own converters; here they can only be zipped.",
        FileKind.Model => "Geometry only: positions, normals and UVs. Materials and textures are not carried over.",
        FileKind.Unknown => "OverDesk does not know this kind of file, but it can pack it.",
        _ => null,
    };

    /// <summary>A free path for the result: "name.ext", else "name (2).ext"... "folder" is a new folder.</summary>
    public static string OutputPath(string input, string target, ConvertOptions options)
    {
        var folder = options.OutputFolder ?? Path.GetDirectoryName(input)!;
        var name = KindOf(input) == FileKind.Archive ? Archives.BaseName(input) : Path.GetFileNameWithoutExtension(input);
        // Packing keeps the whole name: "photo.png.zip" says what is inside.
        if (target is "zip" or "gz" && KindOf(input) != FileKind.Archive) name = Path.GetFileName(input);
        return ToolUi.FreePath(folder, name, target == "folder" ? "" : target);
    }

    /// <summary>Convert one file; returns the new file's (or folder's) path.</summary>
    public static async Task<string> ConvertAsync(string input, string target, ConvertOptions options, IProgress<double>? progress, CancellationToken cancellation)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("The file does not exist.", input);
        target = target.TrimStart('.').ToLowerInvariant() switch { "jpeg" => "jpg", "htm" => "html", "yml" => "yaml", "tgz" => "tar.gz", var t => t };
        if (!TargetsFor(input).Contains(target))
            throw new InvalidOperationException($"{Path.GetFileName(input)} cannot be converted to {target.ToUpperInvariant()} here. Possible: {string.Join(", ", TargetsFor(input))}.");
        var output = OutputPath(input, target, options);
        try
        {
            if (target is "zip" or "gz" or "tar" or "tar.gz" or "folder")
                await Task.Run(() =>
                {
                    if (target == "folder") Archives.Extract(input, output, cancellation);
                    else Archives.Pack(input, output, target, cancellation);
                }, cancellation);
            else
                switch (KindOf(input))
                {
                    case FileKind.Image: await Task.Run(() => ConvertImage(input, output, target, options), cancellation); break;
                    case FileKind.Data: await Task.Run(() => ConvertData(input, output, target), cancellation); break;
                    case FileKind.Text: await ConvertTextAsync(input, output, target, cancellation); break;
                    case FileKind.Document: await ConvertDocumentAsync(input, output, target, cancellation); break;
                    case FileKind.Audio or FileKind.Video: await ConvertMediaAsync(input, output, target, progress, cancellation); break;
                    case FileKind.Font:
                        var font = await File.ReadAllBytesAsync(input, cancellation);
                        await File.WriteAllBytesAsync(output, target == "woff" ? Fonts.ToWoff(font) : Fonts.FromWoff(font), cancellation);
                        break;
                    case FileKind.Model: await Task.Run(() => Models.Write(Models.Read(input), output, target), cancellation); break;
                }
        }
        catch
        {
            try
            {
                if (File.Exists(output)) File.Delete(output);
                else if (Directory.Exists(output)) Directory.Delete(output, true);
            }
            catch (IOException) { }
            throw;
        }
        if (!File.Exists(output) && !Directory.Exists(output)) throw new InvalidOperationException("The converter finished but wrote no file.");
        progress?.Report(1);
        return output;
    }

    /// <summary>Several images as one PDF, one page each, in the given order.</summary>
    public static async Task<string> CombineToPdfAsync(IReadOnlyList<string> images, ConvertOptions options, CancellationToken cancellation)
    {
        var first = images[0];
        var output = ToolUi.FreePath(options.OutputFolder ?? Path.GetDirectoryName(first)!, Path.GetFileNameWithoutExtension(first) + " and more", "pdf");
        await Task.Run(() =>
        {
            var bitmaps = new List<SKBitmap>();
            try
            {
                foreach (var path in images)
                {
                    cancellation.ThrowIfCancellationRequested();
                    bitmaps.Add(Fit(ImageIO.Load(path), options.MaxSize));
                }
                PdfWriter.WriteImages(bitmaps, output);
            }
            finally { bitmaps.ForEach(b => b.Dispose()); }
        }, cancellation);
        return output;
    }

    // ---------------------------------------------------------------- data

    /// <summary>Text formats convert directly; Excel goes through a table (header row + rows).</summary>
    private static void ConvertData(string input, string output, string target)
    {
        var from = Ext(input);
        if (from == "xlsx")
        {
            File.WriteAllText(output, DataFormats.FromRows(Xlsx.Read(input), target));
            return;
        }
        var text = File.ReadAllText(input);
        if (target == "xlsx") Xlsx.Write(output, DataFormats.Rows(text, from));
        else File.WriteAllText(output, DataFormats.Convert(text, from, target));
    }

    // ---------------------------------------------------------------- images

    private static void ConvertImage(string input, string output, string target, ConvertOptions options)
    {
        using var bitmap = Fit(ImageIO.Load(input), options.MaxSize);
        if (target == "pdf") PdfWriter.WriteImage(bitmap, output);
        else ImageIO.Save(bitmap, output, options.Quality);
    }

    /// <summary>Shrinks to the longest edge (never enlarges); takes ownership of <paramref name="bitmap"/>.</summary>
    private static SKBitmap Fit(SKBitmap bitmap, int? maxSize)
    {
        if (maxSize is not { } size || Math.Max(bitmap.Width, bitmap.Height) <= size) return bitmap;
        var scale = size / (double)Math.Max(bitmap.Width, bitmap.Height);
        using (bitmap) return ImageIO.Resize(bitmap, (int)Math.Round(bitmap.Width * scale), (int)Math.Round(bitmap.Height * scale));
    }

    // ---------------------------------------------------------------- text and documents

    private static async Task ConvertTextAsync(string input, string output, string target, CancellationToken cancellation)
    {
        var text = await File.ReadAllTextAsync(input, cancellation);
        var title = Path.GetFileNameWithoutExtension(input);
        if (target is "srt" or "vtt")
        {
            await File.WriteAllTextAsync(output, Subtitles.Convert(text, target), cancellation);
            return;
        }
        if (target == "pdf")
        {
            await Task.Run(() => PdfWriter.WriteText(text, output, markdown: Ext(input) is "md" or "markdown", title), cancellation);
            return;
        }
        var html = Ext(input) is "md" or "markdown" ? Markdown.ToHtmlPage(text, title) : Markdown.PlainToHtmlPage(text, title);
        if (target == "html")
        {
            await File.WriteAllTextAsync(output, html, cancellation);
            return;
        }
        // Word, RTF, ODT: via HTML and macOS's textutil, which keeps headings, lists and emphasis.
        var temp = Path.Combine(Path.GetTempPath(), $"overdesk-{Guid.NewGuid():N}.html");
        try
        {
            await File.WriteAllTextAsync(temp, html, cancellation);
            await Cli.Run("/usr/bin/textutil", new[] { "-convert", target, temp, "-output", output }, cancellation);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static async Task ConvertDocumentAsync(string input, string output, string target, CancellationToken cancellation)
    {
        if (target == "pdf")
        {
            // The document's text (through textutil on macOS), laid out as a PDF.
            string text;
            if (Mac)
            {
                var temp = Path.Combine(Path.GetTempPath(), $"overdesk-{Guid.NewGuid():N}.txt");
                try
                {
                    await Cli.Run("/usr/bin/textutil", new[] { "-convert", "txt", input, "-output", temp }, cancellation);
                    text = await File.ReadAllTextAsync(temp, cancellation);
                }
                finally { File.Delete(temp); }
            }
            else text = Markdown.HtmlToText(await File.ReadAllTextAsync(input, cancellation));
            await Task.Run(() => PdfWriter.WriteText(text, output, markdown: false, Path.GetFileNameWithoutExtension(input)), cancellation);
            return;
        }
        if (Mac)
        {
            await Cli.Run("/usr/bin/textutil", new[] { "-convert", target, input, "-output", output }, cancellation);
            return;
        }
        // Windows: HTML to plain text only.
        var html = await File.ReadAllTextAsync(input, cancellation);
        await File.WriteAllTextAsync(output, Markdown.HtmlToText(html), cancellation);
    }

    // ---------------------------------------------------------------- audio and video

    private static async Task ConvertMediaAsync(string input, string output, string target, IProgress<double>? progress, CancellationToken cancellation)
    {
        if (Cli.Find("ffmpeg") is { } ffmpeg)
        {
            var args = new List<string> { "-y", "-hide_banner", "-i", input };
            args.AddRange(target switch
            {
                "gif" => new[] { "-vf", "fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0" },
                "mp4" or "mov" or "m4v" => new[] { "-movflags", "+faststart", "-pix_fmt", "yuv420p" },
                "mp3" => new[] { "-vn", "-q:a", "2" },
                "m4a" => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
                "wav" or "flac" or "aiff" or "ogg" => new[] { "-vn" },
                "opus" => new[] { "-vn", "-c:a", "libopus", "-b:a", "128k" },
                "aac" => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
                "webm" => new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-c:a", "libopus" },
                "jpg" => new[] { "-vf", "thumbnail", "-frames:v", "1", "-q:v", "2" }, // a typical frame from the start
                _ => Array.Empty<string>(),
            });
            args.Add(output);
            TimeSpan? duration = null;
            await Cli.Run(ffmpeg, args, cancellation, line =>
            {
                if (duration is null && DurationLine().Match(line) is { Success: true } d) duration = Time(d.Groups[1].Value);
                if (duration is { TotalSeconds: > 0 } total && TimeLine().Match(line) is { Success: true } t)
                    progress?.Report(Math.Clamp(Time(t.Groups[1].Value).TotalSeconds / total.TotalSeconds, 0, 0.99));
            });
            return;
        }
        if (!Mac) throw new InvalidOperationException("Install ffmpeg to convert audio and video.");
        if (KindOf(input) == FileKind.Audio)
        {
            var (fileType, dataFormat) = target switch
            {
                "m4a" => ("m4af", "aac"),
                "wav" => ("WAVE", "LEI16"),
                "aiff" => ("AIFF", "BEI16"),
                "flac" => ("flac", "flac"),
                _ => ("caff", "LEI16"),
            };
            await Cli.Run("/usr/bin/afconvert", new[] { "-f", fileType, "-d", dataFormat, input, output }, cancellation);
        }
        else
            await Cli.Run("/usr/bin/avconvert", new[] { "--source", input, "--output", output, "--preset", "PresetHighestQuality", "--replace" }, cancellation);
    }

    [GeneratedRegex(@"Duration:\s*(\d+:\d+:\d+(?:\.\d+)?)")] private static partial Regex DurationLine();
    [GeneratedRegex(@"time=(\d+:\d+:\d+(?:\.\d+)?)")] private static partial Regex TimeLine();

    private static TimeSpan Time(string text) => TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.Zero;
}

/// <summary>Finds and runs command-line converters the way the user's terminal would (login shell PATH on macOS).</summary>
internal static class Cli
{
    private static readonly Dictionary<string, string?> Found = new();

    public static string? Find(string command)
    {
        lock (Found)
        {
            if (Found.TryGetValue(command, out var known)) return known;
            string? path = null;
            try
            {
                var info = OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("where.exe") { ArgumentList = { command } }
                    : new ProcessStartInfo(Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } sh ? sh : "/bin/zsh") { ArgumentList = { "-l", "-c", $"command -v {command}" } };
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                using var p = Process.Start(info);
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(5000);
                path = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(File.Exists);
            }
            catch (Exception) { /* not installed */ }
            Found[command] = path;
            return path;
        }
    }

    /// <summary>Run to the end; a non-zero exit becomes an exception carrying the last error lines.</summary>
    public static async Task Run(string exe, IEnumerable<string> args, CancellationToken cancellation, Action<string>? onErrorLine = null)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = new Process { StartInfo = info };
        var errors = new List<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errors)
            {
                errors.Add(e.Data);
                if (errors.Count > 30) errors.RemoveAt(0);
            }
            onErrorLine?.Invoke(e.Data);
        };
        process.OutputDataReceived += (_, _) => { };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using var kill = cancellation.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { } });
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0)
        {
            string detail;
            lock (errors) detail = string.Join("\n", errors.TakeLast(4)).Trim();
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed{(detail.Length > 0 ? ": " + detail : ".")}");
        }
    }
}
