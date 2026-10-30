using System.Text.Json;
using System.Text.Json.Nodes;
using Overdesk.Tools.Shared;
using SkiaSharp;

namespace Overdesk.Tool.Sprites;

public sealed record PackOptions
{
    /// <summary>Cut away fully transparent borders (the JSON remembers the offset).</summary>
    public bool Trim { get; init; } = true;

    /// <summary>Empty pixels between sprites, so filtering never bleeds a neighbor in.</summary>
    public int Padding { get; init; } = 2;

    /// <summary>Round the atlas up to powers of two (older GPUs, some engines' compression).</summary>
    public bool PowerOfTwo { get; init; }

    public int MaxSize { get; init; } = 8192;
}

public sealed record PackResult(string Image, string Json, int Width, int Height, int Count);

/// <summary>
/// Sprite sheets both ways. Packing places sprites with a MaxRects packer (best short-side fit) and writes the atlas
/// PNG plus a TexturePacker-style JSON (hash format), which Phaser, PixiJS, Cocos, Godot plugins and many engines read.
/// Slicing cuts a sheet into frames, by a grid or by finding each separate shape on a transparent background.
/// </summary>
public static class Sprites
{
    public static PackResult Pack(IReadOnlyList<string> paths, string output, PackOptions options)
    {
        if (paths.Count == 0) throw new InvalidOperationException("No images to pack.");
        var sprites = new List<(string Name, SKBitmap Bitmap, SKRectI Trim, int SourceW, int SourceH)>();
        var names = new HashSet<string>();
        try
        {
            foreach (var path in paths)
            {
                var bitmap = ImageIO.Load(path);
                var trim = options.Trim ? Opaque(bitmap) : new SKRectI(0, 0, bitmap.Width, bitmap.Height);
                if (trim.Width == 0) trim = new SKRectI(0, 0, 1, 1);
                var name = Path.GetFileName(path);
                for (var i = 2; !names.Add(name); i++) name = $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}";
                sprites.Add((name, bitmap, trim, bitmap.Width, bitmap.Height));
            }

            // Biggest first packs tightest; then grow the bin until everything fits.
            var order = sprites.Select((s, i) => i).OrderByDescending(i => Math.Max(sprites[i].Trim.Width, sprites[i].Trim.Height)).ThenByDescending(i => sprites[i].Trim.Width * sprites[i].Trim.Height).ToList();
            var area = sprites.Sum(s => (long)(s.Trim.Width + options.Padding) * (s.Trim.Height + options.Padding));
            var widest = sprites.Max(s => s.Trim.Width + options.Padding);
            var tallest = sprites.Max(s => s.Trim.Height + options.Padding);
            var side = Math.Max(Math.Max(widest, tallest), (int)Math.Ceiling(Math.Sqrt(area)));
            if (options.PowerOfTwo) side = NextPow2(side);
            SKRectI[]? places = null;
            int binW = side, binH = side;
            while (places is null)
            {
                if (binW > options.MaxSize || binH > options.MaxSize)
                    throw new InvalidOperationException($"The sprites do not fit in {options.MaxSize} × {options.MaxSize}.");
                places = TryPack(sprites.Select(s => new SKSizeI(s.Trim.Width + options.Padding, s.Trim.Height + options.Padding)).ToList(), order, binW, binH);
                if (places is not null) break;
                // Grow the shorter side.
                if (binW <= binH) binW = options.PowerOfTwo ? binW * 2 : (int)(binW * 1.15) + 1;
                else binH = options.PowerOfTwo ? binH * 2 : (int)(binH * 1.15) + 1;
            }

            // Shrink to what is used.
            var usedW = places.Max(p => p.Right) - options.Padding;
            var usedH = places.Max(p => p.Bottom) - options.Padding;
            if (options.PowerOfTwo) (usedW, usedH) = (NextPow2(usedW), NextPow2(usedH));

            var info = new SKImageInfo(Math.Max(1, usedW), Math.Max(1, usedH), SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            surface.Canvas.Clear(SKColors.Transparent);
            var frames = new JsonObject();
            for (var i = 0; i < sprites.Count; i++)
            {
                var (name, bitmap, trim, w, h) = sprites[i];
                var at = places[i];
                using var image = SKImage.FromBitmap(bitmap);
                surface.Canvas.DrawImage(image, trim, SKRect.Create(at.Left, at.Top, trim.Width, trim.Height), new SKSamplingOptions(SKFilterMode.Nearest));
                frames[name] = new JsonObject
                {
                    ["frame"] = Rect(at.Left, at.Top, trim.Width, trim.Height),
                    ["rotated"] = false,
                    ["trimmed"] = trim.Width != w || trim.Height != h,
                    ["spriteSourceSize"] = Rect(trim.Left, trim.Top, trim.Width, trim.Height),
                    ["sourceSize"] = new JsonObject { ["w"] = w, ["h"] = h },
                    ["pivot"] = new JsonObject { ["x"] = 0.5, ["y"] = 0.5 },
                };
            }
            using (var snapshot = surface.Snapshot())
            using (var data = snapshot.Encode(SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(output, data.ToArray());
            var json = Path.ChangeExtension(output, ".json");
            var root = new JsonObject
            {
                ["frames"] = frames,
                ["meta"] = new JsonObject
                {
                    ["app"] = "OverDesk Sprite Sheet",
                    ["version"] = "1.0",
                    ["image"] = Path.GetFileName(output),
                    ["format"] = "RGBA8888",
                    ["size"] = new JsonObject { ["w"] = info.Width, ["h"] = info.Height },
                    ["scale"] = "1",
                },
            };
            File.WriteAllText(json, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return new PackResult(output, json, info.Width, info.Height, sprites.Count);
        }
        finally
        {
            foreach (var s in sprites) s.Bitmap.Dispose();
        }
    }

    /// <summary>MaxRects, best short side fit. Null when it does not fit.</summary>
    private static SKRectI[]? TryPack(List<SKSizeI> sizes, List<int> order, int width, int height)
    {
        var free = new List<SKRectI> { new(0, 0, width, height) };
        var result = new SKRectI[sizes.Count];
        foreach (var index in order)
        {
            var size = sizes[index];
            var best = -1;
            var bestShort = int.MaxValue;
            var bestLong = int.MaxValue;
            for (var f = 0; f < free.Count; f++)
            {
                var r = free[f];
                if (r.Width < size.Width || r.Height < size.Height) continue;
                var leftW = r.Width - size.Width;
                var leftH = r.Height - size.Height;
                var shortSide = Math.Min(leftW, leftH);
                var longSide = Math.Max(leftW, leftH);
                if (shortSide < bestShort || (shortSide == bestShort && longSide < bestLong))
                {
                    best = f;
                    bestShort = shortSide;
                    bestLong = longSide;
                }
            }
            if (best < 0) return null;
            var placed = SKRectI.Create(free[best].Left, free[best].Top, size.Width, size.Height);
            result[index] = placed;

            // Split every free rectangle the new one overlaps, then drop free rectangles inside others.
            var next = new List<SKRectI>();
            foreach (var r in free)
            {
                if (!r.IntersectsWith(placed)) { next.Add(r); continue; }
                if (placed.Left > r.Left) next.Add(new SKRectI(r.Left, r.Top, placed.Left, r.Bottom));
                if (placed.Right < r.Right) next.Add(new SKRectI(placed.Right, r.Top, r.Right, r.Bottom));
                if (placed.Top > r.Top) next.Add(new SKRectI(r.Left, r.Top, r.Right, placed.Top));
                if (placed.Bottom < r.Bottom) next.Add(new SKRectI(r.Left, placed.Bottom, r.Right, r.Bottom));
            }
            free = next.Where((r, i) => !next.Where((o, j) => j != i && Contains(o, r) && (o != r || j < i)).Any()).ToList();
        }
        return result;
    }

    private static bool Contains(SKRectI outer, SKRectI inner)
        => inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static JsonObject Rect(int x, int y, int w, int h) => new() { ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h };

    private static int NextPow2(int v)
    {
        var p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    /// <summary>The smallest rectangle holding every pixel that is not fully transparent.</summary>
    public static SKRectI Opaque(SKBitmap rgba)
    {
        var pixels = rgba.GetPixelSpan();
        int w = rgba.Width, h = rgba.Height, left = w, top = h, right = -1, bottom = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (pixels[(y * w + x) * 4 + 3] != 0)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
        return right < 0 ? SKRectI.Empty : new SKRectI(left, top, right + 1, bottom + 1);
    }

    // ---------------------------------------------------------------- slicing

    /// <summary>Cells of a grid: <paramref name="columns"/> × <paramref name="rows"/>, with an outer margin and spacing between cells.</summary>
    public static List<SKRectI> Grid(int width, int height, int columns, int rows, int margin = 0, int spacing = 0)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        var cellW = (width - 2 * margin - (columns - 1) * spacing) / columns;
        var cellH = (height - 2 * margin - (rows - 1) * spacing) / rows;
        if (cellW < 1 || cellH < 1) throw new InvalidOperationException("The grid is finer than the image.");
        var cells = new List<SKRectI>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                cells.Add(SKRectI.Create(margin + c * (cellW + spacing), margin + r * (cellH + spacing), cellW, cellH));
        return cells;
    }

    /// <summary>
    /// Each separate shape on a transparent background (8-connected opaque pixels), as its bounding box; boxes that
    /// touch or lie within <paramref name="merge"/> pixels of each other are joined (a sword and its sparkle).
    /// Reading order: top to bottom, then left to right.
    /// </summary>
    public static List<SKRectI> Detect(SKBitmap rgba, int merge = 2, int minSize = 2)
    {
        var pixels = rgba.GetPixelSpan();
        int w = rgba.Width, h = rgba.Height;
        var seen = new bool[w * h];
        var boxes = new List<SKRectI>();
        var stack = new Stack<int>();
        for (var start = 0; start < w * h; start++)
        {
            if (seen[start] || pixels[start * 4 + 3] < 8) continue;
            int left = w, top = h, right = 0, bottom = 0;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                int x = p % w, y = p / w;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        var q = ny * w + nx;
                        if (seen[q] || pixels[q * 4 + 3] < 8) continue;
                        seen[q] = true;
                        stack.Push(q);
                    }
            }
            boxes.Add(new SKRectI(left, top, right + 1, bottom + 1));
        }

        // Merge nearby boxes until nothing changes.
        bool merged;
        do
        {
            merged = false;
            for (var i = 0; i < boxes.Count && !merged; i++)
                for (var j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i];
                    var grown = new SKRectI(a.Left - merge, a.Top - merge, a.Right + merge, a.Bottom + merge);
                    if (!grown.IntersectsWith(boxes[j])) continue;
                    var b = boxes[j];
                    boxes[i] = new SKRectI(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
                    boxes.RemoveAt(j);
                    merged = true;
                    break;
                }
        } while (merged);

        boxes = boxes.Where(b => b.Width >= minSize && b.Height >= minSize).ToList();
        // Rows: boxes whose vertical middles are close share a row.
        var rowHeight = boxes.Count == 0 ? 1 : Math.Max(1, (int)boxes.Average(b => b.Height) / 2);
        return boxes.OrderBy(b => (b.Top + b.Height / 2) / rowHeight).ThenBy(b => b.Left).ToList();
    }

    /// <summary>Cut the cells out into "<name>_000.png"…; empty (fully transparent) cells are skipped.</summary>
    public static List<string> Slice(SKBitmap sheet, IReadOnlyList<SKRectI> cells, string folder, string name, bool skipEmpty = true)
    {
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        var pixels = sheet.GetPixelSpan().ToArray();
        var index = 0;
        foreach (var cell in cells)
        {
            var c = SKRectI.Intersect(cell, new SKRectI(0, 0, sheet.Width, sheet.Height));
            if (c.IsEmpty) continue;
            if (skipEmpty)
            {
                var any = false;
                for (var y = c.Top; y < c.Bottom && !any; y++)
                    for (var x = c.Left; x < c.Right; x++)
                        if (pixels[(y * sheet.Width + x) * 4 + 3] != 0) { any = true; break; }
                if (!any) continue;
            }
            var frame = new byte[c.Width * c.Height * 4];
            for (var y = 0; y < c.Height; y++) Array.Copy(pixels, ((c.Top + y) * sheet.Width + c.Left) * 4, frame, y * c.Width * 4, c.Width * 4);
            using var bitmap = ImageIO.FromPixels(frame, c.Width, c.Height);
            var path = Path.Combine(folder, $"{name}_{index++:000}.png");
            File.WriteAllBytes(path, ImageIO.Encode(bitmap, SKEncodedImageFormat.Png, 100));
            written.Add(path);
        }
        return written;
    }
}
