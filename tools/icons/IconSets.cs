using System.Text;
using System.Text.Json.Nodes;
using Overdesk.Tools.Shared;
using SkiaSharp;

namespace Overdesk.Tool.Icons;

public enum IconTarget { MacOS, Windows, iOS, Android, Web }

public enum IconShape { AsIs, RoundedSquare, Circle }

public sealed record IconOptions
{
    /// <summary>Empty space around the artwork, 0..0.4 of the side.</summary>
    public float Padding { get; init; }

    /// <summary>Fills the shape behind the artwork; null keeps transparency (iOS always gets one: it does not allow transparency).</summary>
    public SKColor? Background { get; init; }

    public IconShape Shape { get; init; } = IconShape.AsIs;
}

/// <summary>
/// One picture → every app icon file a platform asks for: macOS .icns (PNG entries 16-1024), Windows .ico (16-256),
/// iOS AppIcon.appiconset (one 1024 px image + Contents.json, as current Xcode wants), Android launcher icons for every
/// density (square + round, plus the 512 px Play Store image) and web favicons with a manifest and the HTML to paste.
/// </summary>
public static class IconSets
{
    public static string Label(IconTarget target) => target switch
    {
        IconTarget.MacOS => "macOS (.icns)",
        IconTarget.Windows => "Windows (.ico)",
        IconTarget.iOS => "iOS (AppIcon set)",
        IconTarget.Android => "Android (mipmaps)",
        _ => "Web (favicons)",
    };

    /// <summary>Writes into <paramref name="folder"/> (created); returns the files written.</summary>
    public static List<string> Generate(SKBitmap source, string folder, IReadOnlyCollection<IconTarget> targets, IconOptions options)
    {
        var written = new List<string>();
        Directory.CreateDirectory(folder);

        void Png(string path, int size, IconOptions? o = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var image = Render(source, size, o ?? options);
            File.WriteAllBytes(path, ImageIO.Encode(image, SKEncodedImageFormat.Png, 100));
            written.Add(path);
        }

        if (targets.Contains(IconTarget.MacOS))
        {
            // Big Sur and later: the shape sits inside a 1024 grid with ~10% margin; full-bleed art is left alone.
            var mac = options.Shape == IconShape.RoundedSquare ? options with { Padding = Math.Max(options.Padding, 0.098f) } : options;
            var path = Path.Combine(folder, "macOS", "AppIcon.icns");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Icns(source, mac));
            written.Add(path);
        }
        if (targets.Contains(IconTarget.Windows))
        {
            var path = Path.Combine(folder, "Windows", "app.ico");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var big = Render(source, 256, options);
            File.WriteAllBytes(path, ImageIO.WriteIco(big));
            written.Add(path);
        }
        if (targets.Contains(IconTarget.iOS))
        {
            // iOS rounds the corners itself and refuses transparency: full square, solid background.
            var ios = options with { Shape = IconShape.AsIs, Background = options.Background ?? SKColors.White };
            var set = Path.Combine(folder, "iOS", "AppIcon.appiconset");
            Png(Path.Combine(set, "AppIcon-1024.png"), 1024, ios);
            var contents = new JsonObject
            {
                ["images"] = new JsonArray(new JsonObject { ["filename"] = "AppIcon-1024.png", ["idiom"] = "universal", ["platform"] = "ios", ["size"] = "1024x1024" }),
                ["info"] = new JsonObject { ["author"] = "OverDesk", ["version"] = 1 },
            };
            File.WriteAllText(Path.Combine(set, "Contents.json"), contents.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            written.Add(Path.Combine(set, "Contents.json"));
        }
        if (targets.Contains(IconTarget.Android))
        {
            var round = options with { Shape = IconShape.Circle };
            foreach (var (density, size) in new[] { ("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192) })
            {
                Png(Path.Combine(folder, "Android", "res", $"mipmap-{density}", "ic_launcher.png"), size);
                Png(Path.Combine(folder, "Android", "res", $"mipmap-{density}", "ic_launcher_round.png"), size, round);
            }
            Png(Path.Combine(folder, "Android", "playstore-icon.png"), 512, options with { Shape = IconShape.AsIs, Background = options.Background ?? SKColors.White });
        }
        if (targets.Contains(IconTarget.Web))
        {
            var web = Path.Combine(folder, "Web");
            Directory.CreateDirectory(web);
            using (var big = Render(source, 48, options))
                File.WriteAllBytes(Path.Combine(web, "favicon.ico"), ImageIO.WriteIco(big, new[] { 16, 32, 48 }));
            written.Add(Path.Combine(web, "favicon.ico"));
            Png(Path.Combine(web, "favicon-16x16.png"), 16);
            Png(Path.Combine(web, "favicon-32x32.png"), 32);
            Png(Path.Combine(web, "apple-touch-icon.png"), 180, options with { Shape = IconShape.AsIs, Background = options.Background ?? SKColors.White });
            Png(Path.Combine(web, "android-chrome-192x192.png"), 192);
            Png(Path.Combine(web, "android-chrome-512x512.png"), 512);
            var manifest = new JsonObject
            {
                ["name"] = "", ["short_name"] = "",
                ["icons"] = new JsonArray(
                    new JsonObject { ["src"] = "/android-chrome-192x192.png", ["sizes"] = "192x192", ["type"] = "image/png" },
                    new JsonObject { ["src"] = "/android-chrome-512x512.png", ["sizes"] = "512x512", ["type"] = "image/png" }),
                ["theme_color"] = Hex(options.Background ?? SKColors.White), ["background_color"] = Hex(options.Background ?? SKColors.White), ["display"] = "standalone",
            };
            File.WriteAllText(Path.Combine(web, "site.webmanifest"), manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            written.Add(Path.Combine(web, "site.webmanifest"));
            File.WriteAllText(Path.Combine(web, "head.html"),
                """
                <link rel="icon" href="/favicon.ico" sizes="48x48">
                <link rel="icon" type="image/png" sizes="32x32" href="/favicon-32x32.png">
                <link rel="icon" type="image/png" sizes="16x16" href="/favicon-16x16.png">
                <link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png">
                <link rel="manifest" href="/site.webmanifest">

                """);
            written.Add(Path.Combine(web, "head.html"));
        }
        return written;
    }

    /// <summary>The artwork on a square of <paramref name="size"/>: shape-clipped, background-filled, padded.</summary>
    public static SKBitmap Render(SKBitmap source, int size, IconOptions options)
    {
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var inset = options.Shape == IconShape.AsIs ? 0 : size * Math.Clamp(options.Padding, 0, 0.4f) * (options.Shape == IconShape.RoundedSquare ? 1 : 0.5f);
        var shapeRect = new SKRect(inset, inset, size - inset, size - inset);
        using var clip = new SKPath();
        switch (options.Shape)
        {
            case IconShape.RoundedSquare: clip.AddRoundRect(shapeRect, shapeRect.Width * 0.225f, shapeRect.Height * 0.225f); break;
            case IconShape.Circle: clip.AddOval(shapeRect); break;
            default: clip.AddRect(shapeRect); break;
        }
        canvas.Save();
        canvas.ClipPath(clip, antialias: true);
        if (options.Background is { } background) canvas.Clear(background);
        // The art fits inside the shape, with the padding (for AsIs the padding applies to the art directly).
        var artPad = options.Shape == IconShape.AsIs ? size * Math.Clamp(options.Padding, 0, 0.4f) : shapeRect.Width * 0.08f;
        var box = new SKRect(shapeRect.Left + artPad, shapeRect.Top + artPad, shapeRect.Right - artPad, shapeRect.Bottom - artPad);
        if (options.Shape == IconShape.AsIs && options.Padding == 0) box = shapeRect;
        var scale = Math.Min(box.Width / source.Width, box.Height / source.Height);
        var w = source.Width * scale;
        var h = source.Height * scale;
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, SKRect.Create(box.MidX - w / 2, box.MidY - h / 2, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        canvas.Restore();
        using var snapshot = surface.Snapshot();
        using var premul = SKBitmap.FromImage(snapshot);
        return ImageIO.ToRgba(premul);
    }

    /// <summary>An Apple icon file with PNG entries from 16 to 1024 px (16, 32 and their @2x, up to 512@2x).</summary>
    public static byte[] Icns(SKBitmap source, IconOptions options)
    {
        var entries = new (string Type, int Size)[]
        {
            ("icp4", 16), ("icp5", 32), ("ic11", 32), ("icp6", 64), ("ic12", 64), ("ic07", 128), ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024),
        };
        var cache = new Dictionary<int, byte[]>();
        using var stream = new MemoryStream();
        void BigEndian(uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }
        stream.Write(Encoding.ASCII.GetBytes("icns"));
        BigEndian(0); // total length, filled in below
        foreach (var (type, size) in entries)
        {
            if (!cache.TryGetValue(size, out var png))
            {
                using var image = Render(source, size, options);
                cache[size] = png = ImageIO.Encode(image, SKEncodedImageFormat.Png, 100);
            }
            stream.Write(Encoding.ASCII.GetBytes(type));
            BigEndian((uint)(png.Length + 8));
            stream.Write(png);
        }
        var bytes = stream.ToArray();
        var total = (uint)bytes.Length;
        bytes[4] = (byte)(total >> 24);
        bytes[5] = (byte)(total >> 16);
        bytes[6] = (byte)(total >> 8);
        bytes[7] = (byte)total;
        return bytes;
    }

    private static string Hex(SKColor c) => $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";
}
