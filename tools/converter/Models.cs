using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Overdesk.Tool.Converter;

/// <summary>A triangle mesh: one list of vertices (position + optional normal and UV) and three indices per triangle.</summary>
public sealed class Mesh
{
    public List<Vector3> Positions { get; } = new();
    public List<Vector3>? Normals { get; set; }
    public List<Vector2>? Uvs { get; set; }
    public List<int> Indices { get; } = new();

    public int TriangleCount => Indices.Count / 3;
}

/// <summary>
/// 3D models: OBJ, STL (text or binary) and PLY (text or binary) in; OBJ, STL, PLY, glTF and GLB out. Geometry only
/// (positions, normals, UVs); materials and textures are not carried over. Faces with more than three corners are
/// split into triangles.
/// </summary>
public static class Models
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Mesh Read(string path) => Engine.Ext(path) switch
    {
        "obj" => ReadObj(File.ReadAllLines(path)),
        "stl" => ReadStl(File.ReadAllBytes(path)),
        "ply" => ReadPly(File.ReadAllBytes(path)),
        var e => throw new InvalidOperationException($"Cannot read {e} models."),
    };

    public static void Write(Mesh mesh, string path, string target)
    {
        if (mesh.TriangleCount == 0) throw new InvalidOperationException("The model has no faces.");
        switch (target)
        {
            case "obj": File.WriteAllText(path, WriteObj(mesh, Path.GetFileNameWithoutExtension(path))); break;
            case "stl": File.WriteAllBytes(path, WriteStl(mesh)); break;
            case "ply": File.WriteAllText(path, WritePly(mesh)); break;
            case "gltf": File.WriteAllText(path, WriteGltf(mesh, binary: false).Json); break;
            case "glb": File.WriteAllBytes(path, WriteGlb(mesh)); break;
            default: throw new InvalidOperationException($"Cannot write {target} models.");
        }
    }

    // ---------------------------------------------------------------- OBJ

    public static Mesh ReadObj(IEnumerable<string> lines)
    {
        var v = new List<Vector3>();
        var vt = new List<Vector2>();
        var vn = new List<Vector3>();
        var mesh = new Mesh();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var corners = new Dictionary<(int, int, int), int>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "v": v.Add(new Vector3(F(parts, 1), F(parts, 2), F(parts, 3))); break;
                case "vt": vt.Add(new Vector2(F(parts, 1), F(parts, 2))); break;
                case "vn": vn.Add(new Vector3(F(parts, 1), F(parts, 2), F(parts, 3))); break;
                case "f":
                    var face = new List<int>();
                    for (var i = 1; i < parts.Length; i++)
                    {
                        var ids = parts[i].Split('/');
                        int Index(int slot, int count) => slot < ids.Length && int.TryParse(ids[slot], Inv, out var n) ? (n < 0 ? count + n : n - 1) : -1;
                        var key = (Index(0, v.Count), Index(1, vt.Count), Index(2, vn.Count));
                        if (key.Item1 < 0 || key.Item1 >= v.Count) throw new InvalidOperationException($"A face points at vertex {ids[0]}, which does not exist.");
                        if (!corners.TryGetValue(key, out var index))
                        {
                            index = mesh.Positions.Count;
                            corners[key] = index;
                            mesh.Positions.Add(v[key.Item1]);
                            uvs.Add(key.Item2 >= 0 && key.Item2 < vt.Count ? vt[key.Item2] : Vector2.Zero);
                            normals.Add(key.Item3 >= 0 && key.Item3 < vn.Count ? vn[key.Item3] : Vector3.Zero);
                        }
                        face.Add(index);
                    }
                    for (var i = 1; i + 1 < face.Count; i++) mesh.Indices.AddRange(new[] { face[0], face[i], face[i + 1] });
                    break;
            }
        }
        if (vt.Count > 0) mesh.Uvs = uvs;
        if (vn.Count > 0) mesh.Normals = normals;
        return mesh;
    }

    public static string WriteObj(Mesh mesh, string name)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Written by OverDesk Converter");
        sb.AppendLine($"o {name}");
        foreach (var p in mesh.Positions) sb.AppendLine($"v {N(p.X)} {N(p.Y)} {N(p.Z)}");
        if (mesh.Uvs is { } uvs) foreach (var t in uvs) sb.AppendLine($"vt {N(t.X)} {N(t.Y)}");
        if (mesh.Normals is { } normals) foreach (var n in normals) sb.AppendLine($"vn {N(n.X)} {N(n.Y)} {N(n.Z)}");
        for (var i = 0; i < mesh.Indices.Count; i += 3)
        {
            sb.Append('f');
            for (var k = 0; k < 3; k++)
            {
                var id = mesh.Indices[i + k] + 1;
                sb.Append(' ').Append(id);
                if (mesh.Uvs is not null || mesh.Normals is not null) sb.Append('/').Append(mesh.Uvs is not null ? id.ToString(Inv) : "");
                if (mesh.Normals is not null) sb.Append('/').Append(id);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- STL

    public static Mesh ReadStl(byte[] data)
    {
        var mesh = new Mesh();
        var seen = new Dictionary<Vector3, int>();
        void Corner(Vector3 p)
        {
            if (!seen.TryGetValue(p, out var index))
            {
                index = mesh.Positions.Count;
                seen[p] = index;
                mesh.Positions.Add(p);
            }
            mesh.Indices.Add(index);
        }
        var binary = data.Length >= 84 && 84 + 50L * BitConverter.ToUInt32(data, 80) == data.Length;
        if (binary)
        {
            var count = BitConverter.ToUInt32(data, 80);
            for (var i = 0; i < count; i++)
            {
                var at = 84 + i * 50 + 12;
                for (var k = 0; k < 3; k++, at += 12)
                    Corner(new Vector3(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8)));
            }
            return mesh;
        }
        foreach (var raw in Encoding.ASCII.GetString(data).Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && parts[0] == "vertex") Corner(new Vector3(F(parts, 1), F(parts, 2), F(parts, 3)));
        }
        if (mesh.Indices.Count % 3 != 0) throw new InvalidOperationException("The STL file is damaged.");
        return mesh;
    }

    public static byte[] WriteStl(Mesh mesh)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("Binary STL written by OverDesk Converter").CopyTo(header, 0);
        writer.Write(header);
        writer.Write((uint)mesh.TriangleCount);
        for (var i = 0; i < mesh.Indices.Count; i += 3)
        {
            var (a, b, c) = (mesh.Positions[mesh.Indices[i]], mesh.Positions[mesh.Indices[i + 1]], mesh.Positions[mesh.Indices[i + 2]]);
            var normal = Vector3.Cross(b - a, c - a);
            normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.Zero;
            foreach (var p in new[] { normal, a, b, c })
            {
                writer.Write(p.X);
                writer.Write(p.Y);
                writer.Write(p.Z);
            }
            writer.Write((ushort)0);
        }
        writer.Flush();
        return stream.ToArray();
    }

    // ---------------------------------------------------------------- PLY

    public static Mesh ReadPly(byte[] data)
    {
        // Header: text lines up to "end_header".
        var end = IndexOf(data, "end_header");
        if (end < 0 || !Encoding.ASCII.GetString(data, 0, 3).Equals("ply")) throw new InvalidOperationException("Not a PLY file.");
        var bodyStart = end + "end_header".Length;
        while (bodyStart < data.Length && data[bodyStart] != '\n') bodyStart++;
        bodyStart++;
        var header = Encoding.ASCII.GetString(data, 0, end).Split('\n').Select(l => l.Trim()).ToList();
        var format = header.FirstOrDefault(l => l.StartsWith("format "))?.Split(' ')[1] ?? "ascii";
        if (format == "binary_big_endian") throw new InvalidOperationException("Big-endian binary PLY is not supported.");

        var elements = new List<(string Name, int Count, List<(string Name, string Type, string? ListCount)> Props)>();
        foreach (var line in header)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "element") elements.Add((parts[1], int.Parse(parts[2], Inv), new()));
            else if (parts.Length >= 3 && parts[0] == "property" && elements.Count > 0)
            {
                if (parts[1] == "list" && parts.Length == 5) elements[^1].Props.Add((parts[4], parts[3], parts[2]));
                else elements[^1].Props.Add((parts[2], parts[1], null));
            }
        }

        var mesh = new Mesh();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tokens = format == "ascii" ? Encoding.ASCII.GetString(data, bodyStart, data.Length - bodyStart).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : null;
        var t = 0;
        var at = bodyStart;
        double Next(string type)
        {
            if (tokens is not null) return double.Parse(tokens[t++], Inv);
            double value = type switch
            {
                "char" or "int8" => (sbyte)data[at],
                "uchar" or "uint8" => data[at],
                "short" or "int16" => BitConverter.ToInt16(data, at),
                "ushort" or "uint16" => BitConverter.ToUInt16(data, at),
                "int" or "int32" => BitConverter.ToInt32(data, at),
                "uint" or "uint32" => BitConverter.ToUInt32(data, at),
                "float" or "float32" => BitConverter.ToSingle(data, at),
                "double" or "float64" => BitConverter.ToDouble(data, at),
                _ => throw new InvalidOperationException($"Unknown PLY type {type}."),
            };
            at += type switch { "char" or "int8" or "uchar" or "uint8" => 1, "short" or "int16" or "ushort" or "uint16" => 2, "double" or "float64" => 8, _ => 4 };
            return value;
        }
        var hasNormals = false;
        var hasUvs = false;
        foreach (var (name, count, props) in elements)
        {
            for (var i = 0; i < count; i++)
            {
                var values = new Dictionary<string, double>();
                List<int>? list = null;
                foreach (var (prop, type, listCount) in props)
                {
                    if (listCount is not null)
                    {
                        var n = (int)Next(listCount);
                        var items = new List<int>(n);
                        for (var k = 0; k < n; k++) items.Add((int)Next(type));
                        if (prop is "vertex_indices" or "vertex_index") list = items;
                    }
                    else values[prop] = Next(type);
                }
                if (name == "vertex")
                {
                    mesh.Positions.Add(new Vector3((float)values.GetValueOrDefault("x"), (float)values.GetValueOrDefault("y"), (float)values.GetValueOrDefault("z")));
                    hasNormals |= values.ContainsKey("nx");
                    normals.Add(new Vector3((float)values.GetValueOrDefault("nx"), (float)values.GetValueOrDefault("ny"), (float)values.GetValueOrDefault("nz")));
                    var u = values.TryGetValue("u", out var uu) ? uu : values.GetValueOrDefault("s");
                    var v = values.TryGetValue("v", out var vv) ? vv : values.GetValueOrDefault("t");
                    hasUvs |= values.ContainsKey("u") || values.ContainsKey("s");
                    uvs.Add(new Vector2((float)u, (float)v));
                }
                else if (name == "face" && list is { Count: >= 3 })
                    for (var k = 1; k + 1 < list.Count; k++) mesh.Indices.AddRange(new[] { list[0], list[k], list[k + 1] });
            }
        }
        if (hasNormals) mesh.Normals = normals;
        if (hasUvs) mesh.Uvs = uvs;
        return mesh;
    }

    public static string WritePly(Mesh mesh)
    {
        var sb = new StringBuilder();
        sb.Append("ply\nformat ascii 1.0\ncomment Written by OverDesk Converter\n");
        sb.Append($"element vertex {mesh.Positions.Count}\nproperty float x\nproperty float y\nproperty float z\n");
        if (mesh.Normals is not null) sb.Append("property float nx\nproperty float ny\nproperty float nz\n");
        if (mesh.Uvs is not null) sb.Append("property float s\nproperty float t\n");
        sb.Append($"element face {mesh.TriangleCount}\nproperty list uchar int vertex_indices\nend_header\n");
        for (var i = 0; i < mesh.Positions.Count; i++)
        {
            var p = mesh.Positions[i];
            sb.Append($"{N(p.X)} {N(p.Y)} {N(p.Z)}");
            if (mesh.Normals is { } normals) sb.Append($" {N(normals[i].X)} {N(normals[i].Y)} {N(normals[i].Z)}");
            if (mesh.Uvs is { } uvs) sb.Append($" {N(uvs[i].X)} {N(uvs[i].Y)}");
            sb.Append('\n');
        }
        for (var i = 0; i < mesh.Indices.Count; i += 3) sb.Append($"3 {mesh.Indices[i]} {mesh.Indices[i + 1]} {mesh.Indices[i + 2]}\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- glTF

    /// <summary>The glTF JSON and its binary buffer (embedded as a data URI unless <paramref name="binary"/>).</summary>
    public static (string Json, byte[] Buffer) WriteGltf(Mesh mesh, bool binary)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        var views = new JsonArray();
        var accessors = new JsonArray();
        var attributes = new JsonObject();

        int View(int target, Action write)
        {
            var start = (int)buffer.Position;
            write();
            writer.Flush();
            while (buffer.Position % 4 != 0) writer.Write((byte)0);
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = start, ["byteLength"] = (int)(buffer.Position - start), ["target"] = target });
            return views.Count - 1;
        }

        var min = mesh.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = mesh.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        var positions = View(34962, () => mesh.Positions.ForEach(p => { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }));
        accessors.Add(new JsonObject
        {
            ["bufferView"] = positions, ["componentType"] = 5126, ["count"] = mesh.Positions.Count, ["type"] = "VEC3",
            ["min"] = new JsonArray(min.X, min.Y, min.Z), ["max"] = new JsonArray(max.X, max.Y, max.Z),
        });
        attributes["POSITION"] = accessors.Count - 1;
        if (mesh.Normals is { } normals)
        {
            var view = View(34962, () => normals.ForEach(n =>
            {
                var unit = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
                writer.Write(unit.X); writer.Write(unit.Y); writer.Write(unit.Z);
            }));
            accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = normals.Count, ["type"] = "VEC3" });
            attributes["NORMAL"] = accessors.Count - 1;
        }
        if (mesh.Uvs is { } uvs)
        {
            // OBJ / PLY put v = 0 at the bottom; glTF at the top.
            var view = View(34962, () => uvs.ForEach(t => { writer.Write(t.X); writer.Write(1 - t.Y); }));
            accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = uvs.Count, ["type"] = "VEC2" });
            attributes["TEXCOORD_0"] = accessors.Count - 1;
        }
        var indices = View(34963, () => mesh.Indices.ForEach(i => writer.Write((uint)i)));
        accessors.Add(new JsonObject { ["bufferView"] = indices, ["componentType"] = 5125, ["count"] = mesh.Indices.Count, ["type"] = "SCALAR" });
        writer.Flush();
        var bytes = buffer.ToArray();

        var bufferInfo = new JsonObject { ["byteLength"] = bytes.Length };
        if (!binary) bufferInfo["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes);
        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "OverDesk Converter" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = attributes, ["indices"] = accessors.Count - 1, ["mode"] = 4 }),
            }),
            ["buffers"] = new JsonArray(bufferInfo),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
        };
        return (root.ToJsonString(), bytes);
    }

    public static byte[] WriteGlb(Mesh mesh)
    {
        var (json, bin) = WriteGltf(mesh, binary: true);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var jsonPadded = (jsonBytes.Length + 3) & ~3;
        var binPadded = (bin.Length + 3) & ~3;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u); // "glTF"
        writer.Write(2u);
        writer.Write((uint)(12 + 8 + jsonPadded + 8 + binPadded));
        writer.Write((uint)jsonPadded);
        writer.Write(0x4E4F534Au); // JSON
        writer.Write(jsonBytes);
        for (var i = jsonBytes.Length; i < jsonPadded; i++) writer.Write((byte)' ');
        writer.Write((uint)binPadded);
        writer.Write(0x004E4942u); // BIN
        writer.Write(bin);
        for (var i = bin.Length; i < binPadded; i++) writer.Write((byte)0);
        writer.Flush();
        return stream.ToArray();
    }

    private static float F(string[] parts, int i) => i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, Inv, out var f) ? f : 0;

    private static string N(float f) => f.ToString("0.######", Inv);

    private static int IndexOf(byte[] data, string text)
    {
        var needle = Encoding.ASCII.GetBytes(text);
        return data.AsSpan(0, Math.Min(data.Length, 64 * 1024)).IndexOf(needle);
    }
}
