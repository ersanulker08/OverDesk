using SkiaSharp;

namespace Overdesk.Tool.Texture;

public enum MapKind { Albedo, Height, Normal, AmbientOcclusion, Roughness, Smoothness, Metallic, Curvature, Specular, Orm, UnityMask, MetallicSmoothness }

/// <summary>
/// The knobs. Radii are given for a 1024 px image and scale with the real size, so the small live preview and the
/// full-size export look the same.
/// </summary>
public sealed record MapSettings
{
    /// <summary>The texture repeats: sampling wraps around the edges instead of clamping.</summary>
    public bool Tiling { get; init; } = true;

    /// <summary>Blend the edges first so the texture tiles without a visible seam.</summary>
    public bool MakeSeamless { get; init; }

    public float HeightBlur { get; init; } = 1.5f;
    public float HeightContrast { get; init; } = 1f;
    public bool InvertHeight { get; init; }

    public float NormalStrength { get; init; } = 1f;

    /// <summary>DirectX normal maps (Unreal, 3ds Max) have green flipped compared with OpenGL ones (Unity, Blender, Godot).</summary>
    public bool DirectX { get; init; }

    public float AoStrength { get; init; } = 1f;
    public float AoRadius { get; init; } = 12f;

    /// <summary>0.5 keeps the average; lower is glossier, higher is rougher.</summary>
    public float RoughnessLevel { get; init; } = 0.6f;
    public float RoughnessContrast { get; init; } = 1f;
    public bool InvertRoughness { get; init; }

    public float Metallic { get; init; }

    /// <summary>0..1: how much baked-in light and shadow is evened out of the color map.</summary>
    public float RemoveShading { get; init; }

    public static MapSettings For(Preset preset) => preset switch
    {
        Preset.Unreal => new MapSettings { DirectX = true },
        _ => new MapSettings(),
    };
}

/// <summary>Engine conventions: which maps, packed how, normal map flavor.</summary>
public enum Preset { Unity, UnityHdrp, Unreal, Godot, Blender, All }

/// <summary>The source texture as float channels (0..1) plus its luminance.</summary>
public sealed class TextureSource
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required float[] R { get; init; }
    public required float[] G { get; init; }
    public required float[] B { get; init; }
    public required float[] A { get; init; }
}

/// <summary>
/// Turns one photo or painted texture into PBR maps. Height is derived from brightness (blurred, contrasted, levelled);
/// the normal map is its Sobel slope; ambient occlusion and curvature compare each point with its blurred
/// surroundings at several scales; roughness follows brightness; the color map can have baked shading evened out.
/// Pure C#, parallel over rows, the same on every OS.
/// </summary>
public static class Maps
{
    public static readonly MapKind[] Single = { MapKind.Albedo, MapKind.Normal, MapKind.Height, MapKind.AmbientOcclusion, MapKind.Roughness, MapKind.Smoothness, MapKind.Metallic, MapKind.Curvature, MapKind.Specular };
    public static readonly MapKind[] Packed = { MapKind.Orm, MapKind.UnityMask, MapKind.MetallicSmoothness };

    public static string Label(MapKind kind) => kind switch
    {
        MapKind.AmbientOcclusion => "Ambient occlusion",
        MapKind.Orm => "ORM (AO · Rough · Metal)",
        MapKind.UnityMask => "Unity HDRP mask",
        MapKind.MetallicSmoothness => "Metallic + smoothness",
        var k => k.ToString(),
    };

    /// <summary>File name suffix, as engines' importers expect.</summary>
    public static string Suffix(MapKind kind) => kind switch
    {
        MapKind.Albedo => "BaseColor",
        MapKind.AmbientOcclusion => "AO",
        MapKind.Orm => "ORM",
        MapKind.UnityMask => "MaskMap",
        MapKind.MetallicSmoothness => "MetallicSmoothness",
        var k => k.ToString(),
    };

    /// <summary>The maps each engine wants; the normal flavor comes from <see cref="MapSettings.For"/>.</summary>
    public static MapKind[] MapsFor(Preset preset) => preset switch
    {
        Preset.Unity => new[] { MapKind.Albedo, MapKind.Normal, MapKind.MetallicSmoothness, MapKind.Height, MapKind.AmbientOcclusion },
        Preset.UnityHdrp => new[] { MapKind.Albedo, MapKind.Normal, MapKind.UnityMask, MapKind.Height },
        Preset.Unreal => new[] { MapKind.Albedo, MapKind.Normal, MapKind.Orm, MapKind.Height },
        Preset.Godot => new[] { MapKind.Albedo, MapKind.Normal, MapKind.Orm, MapKind.Height },
        Preset.Blender => new[] { MapKind.Albedo, MapKind.Normal, MapKind.Roughness, MapKind.Height, MapKind.AmbientOcclusion },
        _ => Single.Concat(Packed).ToArray(),
    };

    public static TextureSource FromBitmap(SKBitmap rgba)
    {
        var pixels = rgba.GetPixelSpan();
        var n = rgba.Width * rgba.Height;
        var source = new TextureSource { Width = rgba.Width, Height = rgba.Height, R = new float[n], G = new float[n], B = new float[n], A = new float[n] };
        for (var i = 0; i < n; i++)
        {
            source.R[i] = pixels[i * 4] / 255f;
            source.G[i] = pixels[i * 4 + 1] / 255f;
            source.B[i] = pixels[i * 4 + 2] / 255f;
            source.A[i] = pixels[i * 4 + 3] / 255f;
        }
        return source;
    }

    /// <summary>Every requested map as straight RGBA bytes, same size as the source.</summary>
    public static Dictionary<MapKind, byte[]> Generate(TextureSource source, MapSettings o, IReadOnlyCollection<MapKind> kinds, CancellationToken cancellation = default)
    {
        var w = source.Width;
        var h = source.Height;
        var n = w * h;
        var scale = Math.Min(w, h) / 1024f;
        var wrap = o.Tiling || o.MakeSeamless;
        if (o.MakeSeamless) source = Seamless(source);

        var luminance = new float[n];
        for (var i = 0; i < n; i++) luminance[i] = 0.2126f * source.R[i] + 0.7152f * source.G[i] + 0.0722f * source.B[i];

        bool Needs(params MapKind[] any) => any.Any(kinds.Contains);
        var result = new Dictionary<MapKind, byte[]>();

        // Height: brightness, softened, contrasted around its average, levelled to the full range.
        float[]? height = null;
        if (Needs(MapKind.Height, MapKind.Normal, MapKind.AmbientOcclusion, MapKind.Curvature, MapKind.Orm, MapKind.UnityMask))
        {
            height = Blur(luminance, w, h, o.HeightBlur * scale, wrap);
            var mean = height.Average();
            for (var i = 0; i < n; i++)
            {
                var v = (height[i] - mean) * o.HeightContrast + 0.5f;
                height[i] = o.InvertHeight ? 1 - v : v;
            }
            Normalize(height);
        }
        cancellation.ThrowIfCancellationRequested();

        if (kinds.Contains(MapKind.Albedo))
        {
            float[] r = source.R, g = source.G, b = source.B;
            if (o.RemoveShading > 0)
            {
                // Divide out the low-frequency light: each pixel is pulled towards the image's average brightness.
                var local = Blur(luminance, w, h, 48 * scale, wrap);
                var mean = luminance.Average();
                r = new float[n];
                g = new float[n];
                b = new float[n];
                Parallel.For(0, n, i =>
                {
                    var factor = MathF.Pow(mean / MathF.Max(local[i], 0.02f), o.RemoveShading);
                    r[i] = Math.Clamp(source.R[i] * factor, 0, 1);
                    g[i] = Math.Clamp(source.G[i] * factor, 0, 1);
                    b[i] = Math.Clamp(source.B[i] * factor, 0, 1);
                });
            }
            result[MapKind.Albedo] = Pack(n, r, g, b, source.A);
        }

        if (height is not null && kinds.Contains(MapKind.Height)) result[MapKind.Height] = Gray(height);

        if (height is not null && kinds.Contains(MapKind.Normal))
        {
            var rgba = new byte[n * 4];
            // Slopes per pixel shrink as the image grows; multiply back so strength means the same at any size.
            var k = o.NormalStrength * 6f * Math.Max(w, h) / 1024f;
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    float At(int dx, int dy) => height[Index(x + dx, y + dy, w, h, wrap)];
                    var gx = (At(1, -1) + 2 * At(1, 0) + At(1, 1) - At(-1, -1) - 2 * At(-1, 0) - At(-1, 1)) / 8f;
                    var gy = (At(-1, 1) + 2 * At(0, 1) + At(1, 1) - At(-1, -1) - 2 * At(0, -1) - At(1, -1)) / 8f;
                    var nx = -gx * k;
                    var ny = (o.DirectX ? -gy : gy) * k;
                    var length = MathF.Sqrt(nx * nx + ny * ny + 1);
                    var i = (y * w + x) * 4;
                    rgba[i] = ToByte(nx / length * 0.5f + 0.5f);
                    rgba[i + 1] = ToByte(ny / length * 0.5f + 0.5f);
                    rgba[i + 2] = ToByte(1 / length * 0.5f + 0.5f);
                    rgba[i + 3] = 255;
                }
            });
            result[MapKind.Normal] = rgba;
        }
        cancellation.ThrowIfCancellationRequested();

        // Occlusion: how far below its surroundings each point sits, at three scales (pixel noise smoothed away first).
        float[]? ao = null;
        if (height is not null && Needs(MapKind.AmbientOcclusion, MapKind.Orm, MapKind.UnityMask))
        {
            var surface = Blur(height, w, h, MathF.Max(1, 1.5f * scale), wrap);
            ao = new float[n];
            foreach (var (radius, weight) in new[] { (o.AoRadius, 0.5f), (o.AoRadius * 2.5f, 0.3f), (o.AoRadius * 6f, 0.2f) })
            {
                var around = Blur(surface, w, h, radius * scale, wrap);
                for (var i = 0; i < n; i++) ao[i] += MathF.Max(around[i] - surface[i], 0) * weight;
            }
            for (var i = 0; i < n; i++) ao[i] = Math.Clamp(1 - ao[i] * 2f * o.AoStrength, 0, 1);
            if (kinds.Contains(MapKind.AmbientOcclusion)) result[MapKind.AmbientOcclusion] = Gray(ao);
        }

        if (height is not null && kinds.Contains(MapKind.Curvature))
        {
            // Edges stick out of their surroundings (bright), creases sink in (dark); pixel noise is smoothed first.
            var surface = Blur(height, w, h, MathF.Max(1, 1.5f * scale), wrap);
            var around = Blur(surface, w, h, 5 * scale, wrap);
            var curvature = new float[n];
            for (var i = 0; i < n; i++) curvature[i] = Math.Clamp(0.5f + (surface[i] - around[i]) * 2.5f, 0, 1);
            result[MapKind.Curvature] = Gray(curvature);
        }
        cancellation.ThrowIfCancellationRequested();

        // Roughness: darker, more broken-up areas read as rougher; the level slider shifts it overall.
        float[]? roughness = null;
        if (Needs(MapKind.Roughness, MapKind.Smoothness, MapKind.Orm, MapKind.UnityMask, MapKind.MetallicSmoothness))
        {
            var soft = Blur(luminance, w, h, 1f * scale, wrap);
            var mean = soft.Average();
            roughness = new float[n];
            for (var i = 0; i < n; i++)
            {
                var v = (mean - soft[i]) * 1.5f * o.RoughnessContrast + o.RoughnessLevel;
                roughness[i] = Math.Clamp(o.InvertRoughness ? 1 - v : v, 0, 1);
            }
            if (kinds.Contains(MapKind.Roughness)) result[MapKind.Roughness] = Gray(roughness);
            if (kinds.Contains(MapKind.Smoothness)) result[MapKind.Smoothness] = Gray(roughness.Select(r => 1 - r).ToArray());
        }

        var metal = new float[n];
        if (o.Metallic > 0) Array.Fill(metal, o.Metallic);
        if (kinds.Contains(MapKind.Metallic)) result[MapKind.Metallic] = Gray(metal);
        if (kinds.Contains(MapKind.Specular))
        {
            var mean = luminance.Average();
            result[MapKind.Specular] = Gray(luminance.Select(l => Math.Clamp((l - mean) * 1.6f + 0.5f, 0, 1)).ToArray());
        }

        var ones = new float[n];
        Array.Fill(ones, 1f);
        if (kinds.Contains(MapKind.Orm)) result[MapKind.Orm] = Pack(n, ao!, roughness!, metal, ones);
        if (kinds.Contains(MapKind.UnityMask)) result[MapKind.UnityMask] = Pack(n, metal, ao!, new float[n], roughness!.Select(r => 1 - r).ToArray());
        if (kinds.Contains(MapKind.MetallicSmoothness)) result[MapKind.MetallicSmoothness] = Pack(n, metal, metal, metal, roughness!.Select(r => 1 - r).ToArray());
        return result;
    }

    /// <summary>
    /// Cross-fades the texture with a copy shifted by half its size: the copy's edges come from the middle of the
    /// original, so the borders match up when tiled. The middle keeps the original.
    /// </summary>
    public static TextureSource Seamless(TextureSource s)
    {
        int w = s.Width, h = s.Height, n = w * h;
        var result = new TextureSource { Width = w, Height = h, R = new float[n], G = new float[n], B = new float[n], A = new float[n] };
        var band = Math.Max(1, Math.Min(w, h) * 0.25f);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var edge = Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y));
                var keep = Smooth(Math.Clamp(edge / band, 0, 1));
                var i = y * w + x;
                var j = (y + h / 2) % h * w + (x + w / 2) % w;
                result.R[i] = s.R[i] * keep + s.R[j] * (1 - keep);
                result.G[i] = s.G[i] * keep + s.G[j] * (1 - keep);
                result.B[i] = s.B[i] * keep + s.B[j] * (1 - keep);
                result.A[i] = s.A[i] * keep + s.A[j] * (1 - keep);
            }
        });
        return result;
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    private static int Index(int x, int y, int w, int h, bool wrap)
    {
        if (wrap)
        {
            x = (x % w + w) % w;
            y = (y % h + h) % h;
        }
        else
        {
            x = Math.Clamp(x, 0, w - 1);
            y = Math.Clamp(y, 0, h - 1);
        }
        return y * w + x;
    }

    /// <summary>Gaussian-like blur: three box passes each way. Radius in pixels; under half a pixel nothing happens.</summary>
    public static float[] Blur(float[] input, int w, int h, float radius, bool wrap)
    {
        var r = (int)MathF.Round(radius);
        var current = (float[])input.Clone();
        if (r < 1) return current;
        var temp = new float[current.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxRows(current, temp, w, h, r, wrap);
            BoxColumns(temp, current, w, h, r, wrap);
        }
        return current;
    }

    private static void BoxRows(float[] from, float[] to, int w, int h, int r, bool wrap)
    {
        var size = 2 * r + 1;
        Parallel.For(0, h, y =>
        {
            var row = y * w;
            float Sample(int x) => from[row + (wrap ? (x % w + w) % w : Math.Clamp(x, 0, w - 1))];
            var sum = 0f;
            for (var x = -r; x <= r; x++) sum += Sample(x);
            for (var x = 0; x < w; x++)
            {
                to[row + x] = sum / size;
                sum += Sample(x + r + 1) - Sample(x - r);
            }
        });
    }

    private static void BoxColumns(float[] from, float[] to, int w, int h, int r, bool wrap)
    {
        var size = 2 * r + 1;
        Parallel.For(0, w, x =>
        {
            float Sample(int y) => from[(wrap ? (y % h + h) % h : Math.Clamp(y, 0, h - 1)) * w + x];
            var sum = 0f;
            for (var y = -r; y <= r; y++) sum += Sample(y);
            for (var y = 0; y < h; y++)
            {
                to[y * w + x] = sum / size;
                sum += Sample(y + r + 1) - Sample(y - r);
            }
        });
    }

    /// <summary>Stretch to 0..1, ignoring the darkest and brightest 0.5% so a few stray pixels do not flatten it.</summary>
    private static void Normalize(float[] values)
    {
        var sample = values.Length > 200_000 ? values.Where((_, i) => i % (values.Length / 100_000) == 0).ToArray() : (float[])values.Clone();
        Array.Sort(sample);
        var low = sample[(int)(sample.Length * 0.005)];
        var high = sample[(int)(sample.Length * 0.995)];
        var range = high - low;
        if (range < 1e-5f)
        {
            Array.Fill(values, 0.5f);
            return;
        }
        for (var i = 0; i < values.Length; i++) values[i] = Math.Clamp((values[i] - low) / range, 0, 1);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);

    private static byte[] Gray(float[] v)
    {
        var rgba = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
        {
            var b = ToByte(v[i]);
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    private static byte[] Pack(int n, float[] r, float[] g, float[] b, float[] a)
    {
        var rgba = new byte[n * 4];
        for (var i = 0; i < n; i++)
        {
            rgba[i * 4] = ToByte(r[i]);
            rgba[i * 4 + 1] = ToByte(g[i]);
            rgba[i * 4 + 2] = ToByte(b[i]);
            rgba[i * 4 + 3] = ToByte(a[i]);
        }
        return rgba;
    }
}
