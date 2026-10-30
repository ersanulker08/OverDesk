using System.Text;
using System.Text.Json.Nodes;
using Overdesk.Core.Plugins;
using Overdesk.Tools.Shared;

namespace Overdesk.Tool.Texture;

/// <summary>Entry point named by <c>typeName</c> in overdesk.plugin.json. Also lets the AI make texture maps.</summary>
public sealed class TexturePlugin : IToolPlugin, IAiToolSource
{
    public object CreateView() => new TextureView();

    public IReadOnlyList<PluginAiTool> AiTools { get; } = new[]
    {
        new PluginAiTool(
            "make_texture_maps",
            "Generate PBR texture maps from one image (photo or painted texture, any format OverDesk reads) with OverDesk's Texture Maps tool: " +
            "normal, height, ambient occlusion, roughness, smoothness, metallic, curvature, specular, base color (optionally with baked shading removed) " +
            "and packed maps (ORM for Unreal / Godot, HDRP mask map, Unity metallic-smoothness). Pick an engine preset to get its maps and normal " +
            "convention (Unreal = DirectX, others OpenGL). Files are written as <name>_<Map>.<format> next to the image or in output_folder.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Absolute path of the texture."},
              "preset":{"type":"string","enum":["unity","unity_hdrp","unreal","godot","blender","all"],"description":"Which maps and conventions (default unity)."},
              "maps":{"type":"array","items":{"type":"string","enum":["albedo","normal","height","ao","roughness","smoothness","metallic","curvature","specular","orm","unity_mask","metallic_smoothness"]},"description":"Instead of a preset's list."},
              "normal_strength":{"type":"number","description":"0.1-5, default 1."},
              "directx":{"type":"boolean","description":"DirectX normal map (green flipped). Default from the preset."},
              "seamless":{"type":"boolean","description":"Blend the edges so it tiles without seams."},
              "remove_shading":{"type":"number","description":"0-1: even out baked light in the base color."},
              "metallic":{"type":"number","description":"0-1 metalness for the whole surface (default 0)."},
              "format":{"type":"string","enum":["png","tga","jpg","webp","tiff"],"description":"Default png."},
              "max_size":{"type":"integer","description":"Longest edge in pixels; default the original size."},
              "output_folder":{"type":"string"}
            },"required":["path"]}
            """,
            RunAsync)
        {
            Label = json => $"Making texture maps from {Path.GetFileName(JsonNode.Parse(json)?["path"]?.ToString() ?? "an image")}",
        },
    };

    private static async Task<string> RunAsync(string json, CancellationToken cancellation)
    {
        var args = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var path = args["path"]?.ToString() ?? "";
        if (!File.Exists(path)) throw new FileNotFoundException("No such image.", path);
        var preset = (args["preset"]?.ToString() ?? "unity") switch
        {
            "unity_hdrp" => Preset.UnityHdrp, "unreal" => Preset.Unreal, "godot" => Preset.Godot, "blender" => Preset.Blender, "all" => Preset.All, _ => Preset.Unity,
        };
        var settings = MapSettings.For(preset);
        float? Number(string key) => args[key] is JsonValue v && float.TryParse(v.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : null;
        bool? Flag(string key) => args[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        settings = settings with
        {
            NormalStrength = Number("normal_strength") ?? settings.NormalStrength,
            DirectX = Flag("directx") ?? settings.DirectX,
            MakeSeamless = Flag("seamless") ?? false,
            RemoveShading = Number("remove_shading") ?? 0,
            Metallic = Number("metallic") ?? 0,
        };
        var kinds = (args["maps"] as JsonArray)?.Select(m => m?.ToString() switch
        {
            "albedo" => MapKind.Albedo, "normal" => MapKind.Normal, "height" => MapKind.Height, "ao" => MapKind.AmbientOcclusion,
            "roughness" => MapKind.Roughness, "smoothness" => MapKind.Smoothness, "metallic" => MapKind.Metallic, "curvature" => MapKind.Curvature,
            "specular" => MapKind.Specular, "orm" => MapKind.Orm, "unity_mask" => MapKind.UnityMask, "metallic_smoothness" => MapKind.MetallicSmoothness,
            var other => throw new InvalidOperationException($"Unknown map {other}."),
        }).ToArray() ?? Maps.MapsFor(preset);
        var format = args["format"]?.ToString() is { Length: > 0 } f ? f : "png";
        var maxSize = args["max_size"] is JsonValue m && int.TryParse(m.ToString(), out var size) ? size : (int?)null;
        var folder = args["output_folder"]?.ToString() is { Length: > 0 } o ? o : null;
        var written = await TextureExport.RunAsync(path, settings, kinds, format, maxSize, folder, cancellation);
        var sb = new StringBuilder($"Wrote {written.Count} maps ({(settings.DirectX ? "DirectX" : "OpenGL")} normal):\n");
        foreach (var file in written) sb.AppendLine(file);
        return sb.ToString();
    }
}

/// <summary>Full-size generation and saving, shared by the panel and the AI.</summary>
public static class TextureExport
{
    public static string BaseName(string path) => Path.GetFileNameWithoutExtension(path);

    public static async Task<List<string>> RunAsync(string path, MapSettings settings, IReadOnlyCollection<MapKind> kinds, string format, int? maxSize, string? folder, CancellationToken cancellation)
    {
        return await Task.Run(() =>
        {
            using var loaded = ImageIO.Load(path);
            var bitmap = loaded;
            SkiaSharp.SKBitmap? resized = null;
            if (maxSize is { } max && Math.Max(loaded.Width, loaded.Height) > max)
            {
                var scale = max / (double)Math.Max(loaded.Width, loaded.Height);
                bitmap = resized = ImageIO.Resize(loaded, (int)Math.Round(loaded.Width * scale), (int)Math.Round(loaded.Height * scale));
            }
            try
            {
                var source = Maps.FromBitmap(bitmap);
                var maps = Maps.Generate(source, settings, kinds, cancellation);
                var target = folder ?? Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(target);
                var written = new List<string>();
                foreach (var kind in kinds.Where(maps.ContainsKey))
                {
                    cancellation.ThrowIfCancellationRequested();
                    // Packed maps keep data in alpha, which JPG cannot hold.
                    var ext = format == "jpg" && kind is MapKind.UnityMask or MapKind.MetallicSmoothness or MapKind.Albedo && maps[kind].Where((_, i) => i % 4 == 3).Any(a => a < 255) ? "png" : format;
                    var file = ToolUi.FreePath(target, $"{BaseName(path)}_{Maps.Suffix(kind)}", ext);
                    using var image = ImageIO.FromPixels(maps[kind], source.Width, source.Height);
                    ImageIO.Save(image, file, 95);
                    written.Add(file);
                }
                return written;
            }
            finally { resized?.Dispose(); }
        }, cancellation);
    }
}
