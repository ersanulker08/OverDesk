using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Overdesk.Tools.Shared;

/// <summary>
/// Reading and writing images for the bundled tools. SkiaSharp (OverDesk's drawing library) does PNG, JPG, WebP, GIF,
/// BMP and ICO; TGA, BMP and ICO are also written here, and on macOS everything else (HEIC, AVIF, PSD, camera RAW,
/// TIFF, PDF pages, SVG) goes through the converters macOS ships (sips, qlmanage). Linked into each tool's assembly.
/// </summary>
internal static class ImageIO
{
    /// <summary>Extensions that can be read (some only on macOS).</summary>
    public static readonly string[] Readable =
    {
        "png", "jpg", "jpeg", "webp", "gif", "bmp", "ico", "tga", "wbmp",
        "heic", "heif", "avif", "tif", "tiff", "psd", "svg", "jp2", "exr", "hdr",
        "dng", "cr2", "cr3", "nef", "arw", "raf", "orf", "rw2",
    };

    /// <summary>Extensions <see cref="Save"/> can write on this computer.</summary>
    public static IReadOnlyList<string> Writable => OperatingSystem.IsMacOS()
        ? new[] { "png", "jpg", "webp", "tga", "bmp", "ico", "tiff", "heic" }
        : new[] { "png", "jpg", "webp", "tga", "bmp", "ico" };

    public static bool CanRead(string path) => Readable.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    /// <summary>The image as straight-alpha RGBA, already rotated upright (phone photos).</summary>
    public static SKBitmap Load(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext == "tga") return ReadTga(File.ReadAllBytes(path));
        if (ext != "svg")
        {
            using var codec = SKCodec.Create(path);
            if (codec is not null)
            {
                using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("The image could not be read.");
                using var upright = Orient(decoded, codec.EncodedOrigin);
                return ToRgba(upright);
            }
        }
        if (!OperatingSystem.IsMacOS())
            throw new InvalidOperationException($"{ext.ToUpperInvariant()} images can only be read on macOS.");
        var temp = Path.Combine(Path.GetTempPath(), $"overdesk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            string png;
            if (ext == "svg")
            {
                // Quick Look renders SVG (WebKit) into a PNG thumbnail.
                Run("/usr/bin/qlmanage", "-t", "-s", "2048", "-o", temp, path);
                png = Directory.EnumerateFiles(temp, "*.png").FirstOrDefault() ?? throw new InvalidOperationException("The SVG could not be rendered.");
            }
            else
            {
                png = Path.Combine(temp, "image.png");
                Run("/usr/bin/sips", "-s", "format", "png", path, "--out", png);
            }
            using var bitmap = SKBitmap.Decode(png) ?? throw new InvalidOperationException("The image could not be read.");
            return ToRgba(bitmap);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    /// <summary>Write by the path's extension. JPG puts transparency on white.</summary>
    public static void Save(SKBitmap bitmap, string path, int quality = 90)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        switch (ext)
        {
            case "tga": File.WriteAllBytes(path, WriteTga(bitmap)); return;
            case "bmp": File.WriteAllBytes(path, WriteBmp(bitmap)); return;
            case "ico": File.WriteAllBytes(path, WriteIco(bitmap)); return;
            case "tif" or "tiff" or "heic":
                if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException($"{ext.ToUpperInvariant()} can only be written on macOS.");
                var temp = Path.Combine(Path.GetTempPath(), $"overdesk-{Guid.NewGuid():N}.png");
                try
                {
                    File.WriteAllBytes(temp, Encode(bitmap, SKEncodedImageFormat.Png, 100));
                    var args = new List<string> { "-s", "format", ext == "heic" ? "heic" : "tiff" };
                    if (ext == "heic") args.AddRange(new[] { "-s", "formatOptions", quality.ToString() });
                    args.AddRange(new[] { temp, "--out", path });
                    Run("/usr/bin/sips", args.ToArray());
                }
                finally { File.Delete(temp); }
                return;
        }
        var format = ext switch { "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg, "webp" => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Png };
        File.WriteAllBytes(path, Encode(bitmap, format, quality));
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(format == SKEncodedImageFormat.Jpeg ? SKColors.White : SKColors.Transparent);
        surface.Canvas.DrawBitmap(bitmap, 0, 0);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, Math.Clamp(quality, 1, 100)) ?? throw new InvalidOperationException($"Could not write {format}.");
        return data.ToArray();
    }

    /// <summary>A resized copy (high-quality filtering); the original is left alone.</summary>
    public static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var info = new SKImageInfo(Math.Max(1, width), Math.Max(1, height), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(source);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var premul = SKBitmap.FromImage(snapshot);
        return ToRgba(premul);
    }

    /// <summary>Straight (unpremultiplied) RGBA8888: what the per-pixel tools work on.</summary>
    public static SKBitmap ToRgba(SKBitmap source)
    {
        var result = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var from = source.PeekPixels();
        using var to = result.PeekPixels();
        if (!from.ReadPixels(to)) throw new InvalidOperationException("The image's pixel format is not supported.");
        return result;
    }

    /// <summary>RGBA bytes (straight alpha) of a bitmap made by <see cref="ToRgba"/>.</summary>
    public static byte[] Pixels(SKBitmap rgba) => rgba.GetPixelSpan().ToArray();

    public static SKBitmap FromPixels(byte[] rgba, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), width * height * 4);
        return bitmap;
    }

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

    // ---------------------------------------------------------------- TGA

    /// <summary>Truecolor and grayscale TGA, raw or RLE, 8/16/24/32 bit, either origin.</summary>
    public static SKBitmap ReadTga(byte[] data)
    {
        if (data.Length < 18) throw new InvalidOperationException("Not a TGA file.");
        int idLength = data[0], colorMapType = data[1], type = data[2];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)), height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
        int bits = data[16], descriptor = data[17];
        if (colorMapType != 0 || type is not (2 or 3 or 10 or 11) || width == 0 || height == 0)
            throw new InvalidOperationException("This TGA variant (color-mapped) is not supported.");
        var bytesPer = bits / 8;
        if (bytesPer is < 1 or > 4) throw new InvalidOperationException("Unsupported TGA bit depth.");
        var pos = 18 + idLength;
        var pixels = new byte[width * height * 4];
        var count = width * height;
        var raw = new byte[count * bytesPer];
        if (type is 2 or 3) Array.Copy(data, pos, raw, 0, Math.Min(raw.Length, data.Length - pos));
        else
        {
            var o = 0;
            while (o < raw.Length && pos < data.Length)
            {
                var header = data[pos++];
                var n = (header & 0x7F) + 1;
                if ((header & 0x80) != 0)
                {
                    for (var i = 0; i < n && o < raw.Length; i++, o += bytesPer) Array.Copy(data, pos, raw, o, bytesPer);
                    pos += bytesPer;
                }
                else
                {
                    var len = Math.Min(n * bytesPer, raw.Length - o);
                    Array.Copy(data, pos, raw, o, len);
                    pos += n * bytesPer;
                    o += len;
                }
            }
        }
        var topDown = (descriptor & 0x20) != 0;
        for (var i = 0; i < count; i++)
        {
            var x = i % width;
            var y = topDown ? i / width : height - 1 - i / width;
            var d = (y * width + x) * 4;
            var s = i * bytesPer;
            if (bytesPer <= 2 && type is 3 or 11)
            {
                pixels[d] = pixels[d + 1] = pixels[d + 2] = raw[s];
                pixels[d + 3] = bytesPer == 2 ? raw[s + 1] : (byte)255;
            }
            else if (bytesPer == 2)
            {
                var v = raw[s] | raw[s + 1] << 8;
                pixels[d] = (byte)((v >> 10 & 31) * 255 / 31);
                pixels[d + 1] = (byte)((v >> 5 & 31) * 255 / 31);
                pixels[d + 2] = (byte)((v & 31) * 255 / 31);
                pixels[d + 3] = (descriptor & 0x0F) > 0 && (v & 0x8000) == 0 ? (byte)0 : (byte)255;
            }
            else
            {
                pixels[d] = raw[s + 2];
                pixels[d + 1] = raw[s + 1];
                pixels[d + 2] = raw[s];
                pixels[d + 3] = bytesPer == 4 ? raw[s + 3] : (byte)255;
            }
        }
        return FromPixels(pixels, width, height);
    }

    /// <summary>Uncompressed 32-bit TGA, top-left origin (what game engines import without fuss).</summary>
    public static byte[] WriteTga(SKBitmap bitmap)
    {
        using var rgba = ToRgba(bitmap);
        var pixels = Pixels(rgba);
        var result = new byte[18 + pixels.Length];
        result[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), (ushort)bitmap.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), (ushort)bitmap.Height);
        result[16] = 32;
        result[17] = 0x28; // 8 alpha bits, top-left origin
        for (var i = 0; i < pixels.Length; i += 4)
        {
            result[18 + i] = pixels[i + 2];
            result[18 + i + 1] = pixels[i + 1];
            result[18 + i + 2] = pixels[i];
            result[18 + i + 3] = pixels[i + 3];
        }
        return result;
    }

    // ---------------------------------------------------------------- BMP and ICO

    /// <summary>32-bit BMP with an alpha mask (BITMAPV4HEADER), stored top-down.</summary>
    public static byte[] WriteBmp(SKBitmap bitmap)
    {
        using var rgba = ToRgba(bitmap);
        var pixels = Pixels(rgba);
        const int headerSize = 14 + 108;
        var result = new byte[headerSize + pixels.Length];
        var span = result.AsSpan();
        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], result.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], 108);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], bitmap.Width);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], -bitmap.Height);
        BinaryPrimitives.WriteInt16LittleEndian(span[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[28..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[30..], 3); // BI_BITFIELDS
        BinaryPrimitives.WriteInt32LittleEndian(span[34..], pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[38..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(span[42..], 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(span[54..], 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[58..], 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(span[62..], 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(span[66..], 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[70..], 0x57696E20); // "Win " color space
        for (var i = 0; i < pixels.Length; i += 4)
        {
            result[headerSize + i] = pixels[i + 2];
            result[headerSize + i + 1] = pixels[i + 1];
            result[headerSize + i + 2] = pixels[i];
            result[headerSize + i + 3] = pixels[i + 3];
        }
        return result;
    }

    /// <summary>A Windows icon holding PNG images at the standard sizes up to the source's size (square-padded).</summary>
    public static byte[] WriteIco(SKBitmap bitmap, IEnumerable<int>? sizes = null)
    {
        var side = Math.Max(bitmap.Width, bitmap.Height);
        var chosen = (sizes ?? new[] { 16, 24, 32, 48, 64, 128, 256 }).Where(s => s <= Math.Max(side, 16)).Distinct().OrderBy(s => s).ToList();
        if (chosen.Count == 0) chosen.Add(Math.Min(256, side));
        var images = chosen.Select(s =>
        {
            using var square = Square(bitmap, s);
            return Encode(square, SKEncodedImageFormat.Png, 100);
        }).ToList();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        for (var i = 0; i < images.Count; i++)
        {
            writer.Write((byte)(chosen[i] >= 256 ? 0 : chosen[i]));
            writer.Write((byte)(chosen[i] >= 256 ? 0 : chosen[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) writer.Write(image);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>The image centered on a transparent square of the given side, scaled to fit.</summary>
    public static SKBitmap Square(SKBitmap source, int side, float padding = 0)
    {
        var info = new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        var inner = side * (1 - 2 * padding);
        var scale = inner / Math.Max(source.Width, source.Height);
        var w = source.Width * scale;
        var h = source.Height * scale;
        using var image = SKImage.FromBitmap(source);
        surface.Canvas.DrawImage(image, SKRect.Create((side - w) / 2, (side - h) / 2, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var premul = SKBitmap.FromImage(snapshot);
        return ToRgba(premul);
    }

    private static void Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{Path.GetFileName(exe)} could not be started.");
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {error.Result.Trim()}");
    }
}
