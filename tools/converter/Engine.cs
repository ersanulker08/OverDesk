using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Overdesk.Tool.Converter;

public enum FileKind { Image, Data, Text, Document, Audio, Video, Unknown }

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
/// What can become what, and doing it. Images go through SkiaSharp (the library OverDesk draws with), data and
/// Markdown are converted in code, and documents, audio and video use tools already on the computer: ffmpeg when it
/// is installed, otherwise the converters macOS ships (sips, textutil, afconvert, avconvert).
/// </summary>
public static partial class Engine
{
    private static readonly Dictionary<string, FileKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = FileKind.Image, ["jpg"] = FileKind.Image, ["jpeg"] = FileKind.Image, ["webp"] = FileKind.Image, ["gif"] = FileKind.Image,
        ["bmp"] = FileKind.Image, ["ico"] = FileKind.Image, ["heic"] = FileKind.Image, ["heif"] = FileKind.Image, ["tif"] = FileKind.Image,
        ["tiff"] = FileKind.Image,
        ["json"] = FileKind.Data, ["csv"] = FileKind.Data, ["tsv"] = FileKind.Data, ["xml"] = FileKind.Data,
        ["md"] = FileKind.Text, ["markdown"] = FileKind.Text, ["txt"] = FileKind.Text,
        ["docx"] = FileKind.Document, ["doc"] = FileKind.Document, ["rtf"] = FileKind.Document, ["odt"] = FileKind.Document,
        ["html"] = FileKind.Document, ["htm"] = FileKind.Document, ["webarchive"] = FileKind.Document,
        ["mp3"] = FileKind.Audio, ["wav"] = FileKind.Audio, ["m4a"] = FileKind.Audio, ["aac"] = FileKind.Audio, ["flac"] = FileKind.Audio,
        ["ogg"] = FileKind.Audio, ["opus"] = FileKind.Audio, ["aiff"] = FileKind.Audio, ["aif"] = FileKind.Audio, ["caf"] = FileKind.Audio,
        ["mp4"] = FileKind.Video, ["mov"] = FileKind.Video, ["mkv"] = FileKind.Video, ["webm"] = FileKind.Video, ["avi"] = FileKind.Video,
        ["m4v"] = FileKind.Video,
    };

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static FileKind KindOf(string path) => Kinds.GetValueOrDefault(Ext(path), FileKind.Unknown);

    private static bool Mac => OperatingSystem.IsMacOS();
    private static bool HasFfmpeg => Cli.Find("ffmpeg") is not null;

    /// <summary>The formats this file can be turned into on this computer (extensions, best first).</summary>
    public static IReadOnlyList<string> TargetsFor(string path)
    {
        var ext = Ext(path) switch { "jpeg" => "jpg", "htm" => "html", "markdown" => "md", "tif" => "tiff", "aif" => "aiff", var e => e };
        IEnumerable<string> targets = KindOf(path) switch
        {
            FileKind.Image => new[] { "png", "jpg", "webp" }.Concat(Mac ? new[] { "heic", "tiff", "pdf" } : Array.Empty<string>()),
            FileKind.Data => ext switch
            {
                "json" => new[] { "csv", "xml", "tsv" },
                "csv" => new[] { "json", "tsv", "xml" },
                "tsv" => new[] { "csv", "json" },
                _ => new[] { "json" },
            },
            FileKind.Text => ext == "md"
                ? new[] { "html" }.Concat(Mac ? new[] { "docx", "rtf", "odt" } : Array.Empty<string>())
                : Mac ? new[] { "docx", "rtf", "html", "odt" } : new[] { "html" },
            FileKind.Document => Mac ? new[] { "txt", "docx", "html", "rtf", "odt" } : ext == "html" ? new[] { "txt" } : Array.Empty<string>(),
            FileKind.Audio => HasFfmpeg ? new[] { "mp3", "m4a", "wav", "flac", "ogg", "aiff" } : Mac ? new[] { "m4a", "wav", "aiff", "flac", "caf" } : Array.Empty<string>(),
            FileKind.Video => HasFfmpeg ? new[] { "mp4", "mov", "webm", "gif", "mp3", "m4a" } : Mac ? new[] { "mp4", "mov", "m4v" } : Array.Empty<string>(),
            _ => Array.Empty<string>(),
        };
        return targets.Where(t => t != ext).Distinct().ToList();
    }

    /// <summary>What is missing for more formats of this kind, or null.</summary>
    public static string? Hint(FileKind kind) => kind switch
    {
        FileKind.Audio or FileKind.Video when !HasFfmpeg => Mac
            ? "Install ffmpeg for more formats (MP3, WebM, GIF…): brew install ffmpeg"
            : "Install ffmpeg to convert audio and video: winget install ffmpeg",
        FileKind.Document when !Mac => "Word, RTF and ODT conversion uses macOS's textutil; on Windows only HTML → text works.",
        _ => null,
    };

    /// <summary>A free path for the result: "name.ext", else "name (2).ext"...</summary>
    public static string OutputPath(string input, string target, ConvertOptions options)
    {
        var folder = options.OutputFolder ?? Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        var path = Path.Combine(folder, $"{name}.{target}");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({i}).{target}");
        return path;
    }

    /// <summary>Convert one file; returns the new file's path.</summary>
    public static async Task<string> ConvertAsync(string input, string target, ConvertOptions options, IProgress<double>? progress, CancellationToken cancellation)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("The file does not exist.", input);
        target = target.TrimStart('.').ToLowerInvariant() switch { "jpeg" => "jpg", "htm" => "html", var t => t };
        if (!TargetsFor(input).Contains(target))
            throw new InvalidOperationException($"{Path.GetFileName(input)} cannot be converted to {target.ToUpperInvariant()} here. Possible: {string.Join(", ", TargetsFor(input))}.");
        var output = OutputPath(input, target, options);
        try
        {
            switch (KindOf(input))
            {
                case FileKind.Image: await Task.Run(() => ConvertImage(input, output, target, options), cancellation); break;
                case FileKind.Data: await Task.Run(() => File.WriteAllText(output, DataFormats.Convert(File.ReadAllText(input), Ext(input), target)), cancellation); break;
                case FileKind.Text: await ConvertTextAsync(input, output, target, cancellation); break;
                case FileKind.Document: await ConvertDocumentAsync(input, output, target, cancellation); break;
                case FileKind.Audio or FileKind.Video: await ConvertMediaAsync(input, output, target, progress, cancellation); break;
            }
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
            throw;
        }
        if (!File.Exists(output)) throw new InvalidOperationException("The converter finished but wrote no file.");
        progress?.Report(1);
        return output;
    }

    // ---------------------------------------------------------------- images

    private static void ConvertImage(string input, string output, string target, ConvertOptions options)
    {
        // HEIC / TIFF / PDF output, and inputs Skia cannot read (HEIC), go through macOS's sips.
        using var codec = SKCodec.Create(input);
        if (target is "heic" or "tiff" or "pdf" || codec is null)
        {
            if (!Mac) throw new InvalidOperationException("This image format cannot be read on this computer.");
            var args = new List<string> { "-s", "format", target == "tiff" ? "tiff" : target };
            if (target == "heic") args.AddRange(new[] { "-s", "formatOptions", options.Quality.ToString(CultureInfo.InvariantCulture) });
            if (options.MaxSize is { } max) args.AddRange(new[] { "-Z", max.ToString(CultureInfo.InvariantCulture) });
            args.AddRange(new[] { input, "--out", output });
            Cli.Run("/usr/bin/sips", args, CancellationToken.None).GetAwaiter().GetResult();
            return;
        }

        using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("The image could not be read.");
        using var upright = Orient(decoded, codec.EncodedOrigin);
        var bitmap = upright;
        SKBitmap? resized = null;
        if (options.MaxSize is { } size && Math.Max(bitmap.Width, bitmap.Height) > size)
        {
            var scale = size / (double)Math.Max(bitmap.Width, bitmap.Height);
            resized = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale))),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
            bitmap = resized ?? bitmap;
        }

        var format = target switch { "jpg" => SKEncodedImageFormat.Jpeg, "webp" => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Png };
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        // JPEG has no transparency: put transparent parts on white rather than black.
        surface.Canvas.Clear(format == SKEncodedImageFormat.Jpeg ? SKColors.White : SKColors.Transparent);
        surface.Canvas.DrawBitmap(bitmap, 0, 0);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, Math.Clamp(options.Quality, 1, 100)) ?? throw new InvalidOperationException($"Could not write {target.ToUpperInvariant()}.");
        using var file = File.Create(output);
        data.SaveTo(file);
        resized?.Dispose();
    }

    /// <summary>Phone photos store their rotation separately; bake it in, since PNG and WebP keep no such tag here.</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var (degrees, swap) = origin switch
        {
            SKEncodedOrigin.BottomRight => (180, false),
            SKEncodedOrigin.RightTop => (90, true),
            SKEncodedOrigin.LeftBottom => (270, true),
            _ => (0, false),
        };
        var result = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    // ---------------------------------------------------------------- text and documents

    private static async Task ConvertTextAsync(string input, string output, string target, CancellationToken cancellation)
    {
        var text = await File.ReadAllTextAsync(input, cancellation);
        var title = Path.GetFileNameWithoutExtension(input);
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
