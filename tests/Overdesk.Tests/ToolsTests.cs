using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Overdesk.Tool.Converter;
using Overdesk.Tool.Icons;
using Overdesk.Tool.Sprites;
using Overdesk.Tool.Texture;
using SkiaSharp;

namespace Overdesk.Tests;

/// <summary>Converter's newer formats, DevKit utilities, Texture Maps, App Icons and Sprite Sheet.</summary>
public class ToolsTests
{
    // ---------------------------------------------------------------- helpers

    private static string Png(TempFolder dir, string name, int w, int h, Func<int, int, SKColor> color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) bitmap.SetPixel(x, y, color(x, y));
        var path = Path.Combine(dir.Path, name);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static Task<string> Devkit(string utility, string input, string argument = "")
        => new Overdesk.Tool.DevKit.DevKitPlugin().AiTools.Single(t => t.Name == "devkit")
            .Run(new JsonObject { ["utility"] = utility, ["input"] = input, ["argument"] = argument }.ToJsonString(), CancellationToken.None);

    // ---------------------------------------------------------------- data formats

    [Fact]
    public void Toml_reads_tables_arrays_and_strings()
    {
        const string toml = """
            # settings
            title = "OverDesk"
            version = 2
            ratio = 0.5
            enabled = true
            hex = 0xff
            released = 1979-05-27T07:32:00Z
            tags = ["a", "b",
                    "c"]  # multi-line array

            [server]
            host = 'localhost'
            ports = [ 8000, 8001 ]
            limits = { cpu = 2, memory = "1 GB" }
            database.name = "main"

            [[plugins]]
            id = "converter"

            [[plugins]]
            id = "devkit"
            """;
        var json = JsonNode.Parse(DataFormats.Convert(toml, "toml", "json"))!;
        Assert.Equal("OverDesk", json["title"]!.GetValue<string>());
        Assert.Equal(2, json["version"]!.GetValue<long>());
        Assert.Equal(0.5, json["ratio"]!.GetValue<double>());
        Assert.True(json["enabled"]!.GetValue<bool>());
        Assert.Equal(255, json["hex"]!.GetValue<long>());
        Assert.Equal("1979-05-27T07:32:00Z", json["released"]!.GetValue<string>());
        Assert.Equal(3, json["tags"]!.AsArray().Count);
        Assert.Equal("localhost", json["server"]!["host"]!.GetValue<string>());
        Assert.Equal(8001, json["server"]!["ports"]![1]!.GetValue<long>());
        Assert.Equal("1 GB", json["server"]!["limits"]!["memory"]!.GetValue<string>());
        Assert.Equal("main", json["server"]!["database"]!["name"]!.GetValue<string>());
        Assert.Equal("devkit", json["plugins"]![1]!["id"]!.GetValue<string>());

        // And back: TOML → JSON → TOML → JSON is stable.
        var again = JsonNode.Parse(DataFormats.Convert(DataFormats.Convert(json.ToJsonString(), "json", "toml"), "toml", "json"))!;
        Assert.True(JsonNode.DeepEquals(json, again), again.ToJsonString());
    }

    [Fact]
    public void Ini_env_properties_plist_and_jsonl_round_trip()
    {
        const string json = """{"name":"app","debug":true,"database":{"host":"db.local","port":5432}}""";
        var ini = DataFormats.Convert(json, "json", "ini");
        Assert.Contains("[database]", ini);
        Assert.Equal(5432, JsonNode.Parse(DataFormats.Convert(ini, "ini", "json"))!["database"]!["port"]!.GetValue<long>());

        var env = DataFormats.Convert(json, "json", "env");
        Assert.Contains("DATABASE_HOST=db.local", env);
        Assert.Equal("db.local", JsonNode.Parse(DataFormats.Convert("export DATABASE_HOST=\"db.local\" \n# c\nX=1 # note", "env", "json"))!["DATABASE_HOST"]!.GetValue<string>());

        Assert.Contains("database.port=5432", DataFormats.Convert(json, "json", "properties"));

        var plist = DataFormats.Convert(json, "json", "plist");
        Assert.Contains("<key>port</key>", plist);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(DataFormats.Convert(plist, "plist", "json"))));

        var jsonl = DataFormats.Convert("""[{"a":1},{"a":2}]""", "json", "jsonl");
        Assert.Equal(2, jsonl.Trim().Split('\n').Length);
        Assert.Equal(2, JsonNode.Parse(DataFormats.Convert(jsonl, "jsonl", "json"))!.AsArray().Count);
    }

    [Fact]
    public void Tables_become_markdown_html_and_sql()
    {
        const string csv = "name,age\nAda,36\n\"O'Neil | Jr\",\n";
        var md = DataFormats.Convert(csv, "csv", "md");
        Assert.Contains("| name | age |", md);
        Assert.Contains("O'Neil \\| Jr", md);
        Assert.Contains("<td>Ada</td>", DataFormats.Convert(csv, "csv", "html"));
        var sql = DataFormats.Convert(csv, "csv", "sql");
        Assert.Contains("\"age\" INTEGER", sql);
        Assert.Contains("VALUES ('O''Neil | Jr', NULL);", sql);
    }

    [Fact]
    public void Every_data_format_offers_the_others()
    {
        using var dir = new TempFolder();
        var targets = Engine.TargetsFor(dir.Write("a.toml", "a = 1"));
        foreach (var t in new[] { "json", "yaml", "xml", "plist", "csv", "xlsx", "sql", "zip" }) Assert.Contains(t, targets);
        Assert.DoesNotContain("toml", targets);
    }

    // ---------------------------------------------------------------- archives

    [Fact]
    public async Task Files_pack_and_archives_repack_and_unpack()
    {
        using var dir = new TempFolder();
        var file = dir.Write("notes.txt", "hello archive");
        var options = new ConvertOptions();
        var zip = await Engine.ConvertAsync(file, "zip", options, null, CancellationToken.None);
        Assert.EndsWith("notes.txt.zip", zip);

        var tgz = await Engine.ConvertAsync(zip, "tar.gz", options, null, CancellationToken.None);
        Assert.EndsWith("notes.txt.tar.gz", tgz);
        Assert.Equal(FileKind.Archive, Engine.KindOf(tgz));

        var folder = await Engine.ConvertAsync(tgz, "folder", options, null, CancellationToken.None);
        Assert.True(Directory.Exists(folder));
        Assert.Equal("hello archive", File.ReadAllText(Path.Combine(folder, "notes.txt")));

        var gz = await Engine.ConvertAsync(file, "gz", options, null, CancellationToken.None);
        var back = await Engine.ConvertAsync(gz, "folder", options, null, CancellationToken.None);
        Assert.Equal("hello archive", File.ReadAllText(Path.Combine(back, "notes.txt")));

        // Unknown files can always be packed.
        Assert.Equal(new[] { "zip", "gz" }, Engine.TargetsFor(dir.Write("data.xyz", "?")));
    }

    // ---------------------------------------------------------------- fonts

    [Fact]
    public void Woff_round_trips_the_font_tables()
    {
        // A tiny sfnt with two tables (contents need not be a real font for the container).
        var tables = new (string Tag, byte[] Data)[] { ("cmap", Enumerable.Repeat((byte)7, 300).ToArray()), ("head", Enumerable.Range(0, 54).Select(i => (byte)i).ToArray()) };
        var sfnt = new List<byte>();
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); sfnt.AddRange(b); }
        void U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); sfnt.AddRange(b); }
        U32(0x00010000); U16(2); U16(32); U16(1); U16(0);
        var offset = 12 + 16 * 2;
        foreach (var (tag, data) in tables)
        {
            sfnt.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
            U32(0x12345678); U32((uint)offset); U32((uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }
        foreach (var (_, data) in tables)
        {
            sfnt.AddRange(data);
            while (sfnt.Count % 4 != 0) sfnt.Add(0);
        }
        var woff = Fonts.ToWoff(sfnt.ToArray());
        Assert.Equal("wOFF", System.Text.Encoding.ASCII.GetString(woff, 0, 4));
        Assert.True(woff.Length < sfnt.Count, "the repeated table compresses");
        Assert.Equal(sfnt.ToArray(), Fonts.FromWoff(woff));
    }

    // ---------------------------------------------------------------- 3D models

    [Fact]
    public async Task Obj_becomes_glb_gltf_stl_and_ply()
    {
        using var dir = new TempFolder();
        var obj = dir.Write("quad.obj", "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1 4/4/1\n");
        var mesh = Models.Read(obj);
        Assert.Equal(2, mesh.TriangleCount);
        Assert.NotNull(mesh.Uvs);

        var options = new ConvertOptions();
        var glb = await Engine.ConvertAsync(obj, "glb", options, null, CancellationToken.None);
        var bytes = File.ReadAllBytes(glb);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal((uint)bytes.Length, BitConverter.ToUInt32(bytes, 8));

        var gltf = JsonNode.Parse(File.ReadAllText(await Engine.ConvertAsync(obj, "gltf", options, null, CancellationToken.None)))!;
        Assert.Equal("2.0", gltf["asset"]!["version"]!.GetValue<string>());
        Assert.NotNull(gltf["meshes"]![0]!["primitives"]![0]!["attributes"]!["TEXCOORD_0"]);
        Assert.StartsWith("data:application/octet-stream;base64,", gltf["buffers"]![0]!["uri"]!.GetValue<string>());

        var stl = await Engine.ConvertAsync(obj, "stl", options, null, CancellationToken.None);
        Assert.Equal(84 + 2 * 50, new FileInfo(stl).Length);
        var fromStl = Models.Read(stl);
        Assert.Equal(2, fromStl.TriangleCount);
        Assert.Equal(4, fromStl.Positions.Count); // shared corners are merged

        var ply = await Engine.ConvertAsync(obj, "ply", options, null, CancellationToken.None);
        var fromPly = Models.Read(ply);
        Assert.Equal(2, fromPly.TriangleCount);
        Assert.NotNull(fromPly.Normals);
    }

    // ---------------------------------------------------------------- images

    [Fact]
    public async Task Images_go_to_tga_bmp_and_ico_and_back()
    {
        using var dir = new TempFolder();
        var png = Png(dir, "pic.png", 40, 30, (x, y) => new SKColor((byte)(x * 6), (byte)(y * 8), 90, (byte)(x < 20 ? 255 : 128)));
        var options = new ConvertOptions();
        var tga = await Engine.ConvertAsync(png, "tga", options, null, CancellationToken.None);
        var fromTga = await Engine.ConvertAsync(tga, "png", options with { OutputFolder = Directory.CreateDirectory(Path.Combine(dir.Path, "out")).FullName }, null, CancellationToken.None);
        using (var a = SKBitmap.Decode(png))
        using (var b = SKBitmap.Decode(fromTga))
        {
            Assert.Equal(a.Width, b.Width);
            Assert.Equal(a.GetPixel(5, 5), b.GetPixel(5, 5));
            Assert.Equal(a.GetPixel(30, 20), b.GetPixel(30, 20));
        }
        using (var bmp = SKBitmap.Decode(await Engine.ConvertAsync(png, "bmp", options, null, CancellationToken.None)))
            Assert.Equal(40, bmp.Width);
        using (var ico = SKBitmap.Decode(await Engine.ConvertAsync(png, "ico", options, null, CancellationToken.None)))
            Assert.NotNull(ico);
    }

    [Fact]
    public async Task Several_images_combine_into_one_pdf()
    {
        using var dir = new TempFolder();
        var a = Png(dir, "a.png", 20, 20, (_, _) => SKColors.Red);
        var b = Png(dir, "b.png", 30, 10, (_, _) => SKColors.Blue);
        var pdf = await Engine.CombineToPdfAsync(new[] { a, b }, new ConvertOptions(), CancellationToken.None);
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        Assert.StartsWith("%PDF", text);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page\b").Count);
    }

    // ---------------------------------------------------------------- texture maps

    [Fact]
    public void Flat_texture_gives_a_flat_normal_map()
    {
        var n = 32 * 32;
        var gray = Enumerable.Repeat(0.5f, n).ToArray();
        var source = new TextureSource { Width = 32, Height = 32, R = gray, G = gray, B = gray, A = Enumerable.Repeat(1f, n).ToArray() };
        var maps = Maps.Generate(source, new MapSettings(), new[] { MapKind.Normal, MapKind.AmbientOcclusion });
        var normal = maps[MapKind.Normal];
        Assert.InRange(normal[0], 127, 128);
        Assert.InRange(normal[1], 127, 128);
        Assert.Equal(255, normal[2]);
        Assert.Equal(255, maps[MapKind.AmbientOcclusion][0]);
    }

    [Fact]
    public void Normals_lean_away_from_the_rise_and_directx_flips_green()
    {
        // A steep rise downwards (y) and to the right (x) through the middle of the image.
        int w = 64, h = 64;
        var values = new float[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) values[y * w + x] = Math.Clamp((x + y - 48) / 32f, 0, 1);
        var source = new TextureSource { Width = w, Height = h, R = values, G = values, B = values, A = Enumerable.Repeat(1f, w * h).ToArray() };
        var center = (32 * w + 32) * 4;
        var gl = Maps.Generate(source, new MapSettings { Tiling = false, NormalStrength = 5 }, new[] { MapKind.Normal })[MapKind.Normal];
        var dx = Maps.Generate(source, new MapSettings { Tiling = false, NormalStrength = 5, DirectX = true }, new[] { MapKind.Normal })[MapKind.Normal];
        Assert.True(gl[center] < 124, $"red {gl[center]}: a surface rising to the right faces left");
        Assert.True(gl[center + 1] > 132, $"green {gl[center + 1]}: OpenGL green follows the image's downward rise");
        Assert.True(dx[center + 1] < 124, $"green {dx[center + 1]}: DirectX flips green");
        Assert.Equal(gl[center], dx[center]);
    }

    [Fact]
    public async Task Texture_export_writes_the_presets_maps()
    {
        using var dir = new TempFolder();
        var png = Png(dir, "bricks.png", 96, 64, (x, y) => (x / 12 + y / 8) % 2 == 0 ? new SKColor(170, 80, 60) : new SKColor(60, 50, 45));
        var written = await TextureExport.RunAsync(png, MapSettings.For(Preset.Unreal), Maps.MapsFor(Preset.Unreal), "tga", null, null, CancellationToken.None);
        Assert.Equal(new[] { "bricks_BaseColor.tga", "bricks_Normal.tga", "bricks_ORM.tga", "bricks_Height.tga" }, written.Select(Path.GetFileName));

        var tool = new TexturePlugin().AiTools.Single();
        var result = await tool.Run(new JsonObject { ["path"] = png, ["preset"] = "unity", ["seamless"] = true }.ToJsonString(), CancellationToken.None);
        Assert.Contains("bricks_MetallicSmoothness.png", result);
        Assert.Contains("OpenGL", result);
    }

    // ---------------------------------------------------------------- app icons

    [Fact]
    public void Icon_sets_hold_every_platforms_files()
    {
        using var dir = new TempFolder();
        using var art = new SKBitmap(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        art.Erase(SKColors.Orange);
        var written = IconSets.Generate(art, dir.Path, Enum.GetValues<IconTarget>(), new IconOptions { Shape = IconShape.RoundedSquare, Padding = 0.1f });
        string F(string relative) => Path.Combine(dir.Path, relative);
        foreach (var file in new[] { "macOS/AppIcon.icns", "Windows/app.ico", "iOS/AppIcon.appiconset/Contents.json", "Android/res/mipmap-xxxhdpi/ic_launcher_round.png", "Web/site.webmanifest", "Web/favicon.ico" })
            Assert.Contains(F(file), written.Select(Path.GetFullPath));

        var icns = File.ReadAllBytes(F("macOS/AppIcon.icns"));
        Assert.Equal("icns", System.Text.Encoding.ASCII.GetString(icns, 0, 4));
        Assert.Equal((uint)icns.Length, BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(4)));
        using (var round = SKBitmap.Decode(F("Android/res/mipmap-xxxhdpi/ic_launcher_round.png")))
        {
            Assert.Equal(192, round.Width);
            Assert.Equal(0, round.GetPixel(2, 2).Alpha); // corner outside the circle
        }
        using (var ios = SKBitmap.Decode(F("iOS/AppIcon.appiconset/AppIcon-1024.png")))
            Assert.Equal(255, ios.GetPixel(0, 0).Alpha); // iOS gets a solid background
    }

    // ---------------------------------------------------------------- sprites

    [Fact]
    public void Sprites_pack_without_overlap_and_slice_back()
    {
        using var dir = new TempFolder();
        var paths = new[]
        {
            Png(dir, "hero.png", 40, 50, (x, y) => x is >= 5 and < 35 && y >= 4 ? SKColors.Red : SKColors.Transparent),
            Png(dir, "coin.png", 16, 16, (_, _) => SKColors.Gold),
            Png(dir, "tree.png", 64, 32, (_, _) => SKColors.Green),
        };
        var result = Sprites.Pack(paths, Path.Combine(dir.Path, "atlas.png"), new PackOptions());
        Assert.Equal(3, result.Count);
        var frames = JsonNode.Parse(File.ReadAllText(result.Json))!["frames"]!.AsObject();
        var hero = frames["hero.png"]!;
        Assert.True(hero["trimmed"]!.GetValue<bool>());
        Assert.Equal(30, hero["frame"]!["w"]!.GetValue<int>());
        Assert.Equal(5, hero["spriteSourceSize"]!["x"]!.GetValue<int>());
        var rects = frames.Select(f => SKRectI.Create(f.Value!["frame"]!["x"]!.GetValue<int>(), f.Value["frame"]!["y"]!.GetValue<int>(), f.Value["frame"]!["w"]!.GetValue<int>(), f.Value["frame"]!["h"]!.GetValue<int>())).ToList();
        for (var i = 0; i < rects.Count; i++)
            for (var j = i + 1; j < rects.Count; j++) Assert.False(rects[i].IntersectsWith(rects[j]), $"{rects[i]} overlaps {rects[j]}");

        using var atlas = SKBitmap.Decode(result.Image);
        using var rgba = atlas.Copy(SKColorType.Rgba8888);
        Assert.Equal(3, Sprites.Detect(rgba).Count);
        Assert.Equal(4, Sprites.Grid(64, 64, 2, 2).Count);
    }

    // ---------------------------------------------------------------- devkit

    [Fact]
    public async Task DevKit_utilities_compute_exactly()
    {
        Assert.Contains("hex      0xff", await Devkit("Number bases", "255"));
        Assert.Contains("1111 1111", await Devkit("Number bases", "0b11111111"));
        Assert.Contains("rgb(255 51 102)", await Devkit("Color convert", "#ff3366"));
        Assert.Contains("#ff0000", await Devkit("Color convert", "hsl(0 100% 50%)"));
        Assert.Contains("snake_case     parse_http_response", await Devkit("Case convert", "parseHTTPResponse"));
        Assert.Equal("cafe-au-lait-istanbul", await Devkit("Slugify", "Café au lait — İstanbul!"));
        Assert.Equal("3610a686", await Devkit("CRC32", "hello"));
        Assert.Contains("rwxr-xr-x", await Devkit("Unix permissions", "755"));
        Assert.Contains("1 removed, 1 added", await Devkit("Diff", "a\nb\nc", "a\nB\nc"));
        Assert.Contains("2 matches", await Devkit("Regex test", "ada@x.com bo@y.com", @"(\w+)@(\w+)\.com"));
        Assert.Contains("every 15", await Devkit("Cron explain", "*/15 9-17 * * 1-5"));
        Assert.Contains("\"sub\": \"42\"", await Devkit("JWT decode", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiI0MiIsImV4cCI6MX0.sig"));
        Assert.Contains("✗ Expired", await Devkit("JWT decode", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiI0MiIsImV4cCI6MX0.sig"));
        Assert.Contains("✓ Matches: SHA-256", await Devkit("Hash check", "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", "hello"));
        Assert.Contains("hex     ", await Devkit("HMAC-SHA256", "message", "key"));
        Assert.Equal("{\"a\":1}", await Devkit("JSON minify", "{ \"a\": 1, // c\n }"));

        var ts = await Devkit("JSON → TypeScript", """{"id":1,"user":{"name":"a"},"tags":["x"],"items":[{"n":1},{"n":2.5,"extra":true}]}""");
        Assert.Contains("export interface Root", ts);
        Assert.Contains("user: User;", ts);
        Assert.Contains("items: Item[];", ts);
        Assert.Contains("extra?: boolean;", ts);
        Assert.Contains("n: number;", ts);
        Assert.Contains("public sealed record Root", await Devkit("JSON → C#", """{"id":1}"""));
        Assert.Contains("`json:\"id\"`", await Devkit("JSON → Go", """{"id":1}"""));

        var password = (await Devkit("Password", "16 simple")).Split('\n')[0];
        Assert.Equal(16, password.Length);
        Assert.Matches("^[A-Za-z0-9]+$", password);
        Assert.Equal(26, (await Devkit("ULID", "")).Length);
    }
}
