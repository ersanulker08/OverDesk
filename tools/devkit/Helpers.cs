using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Overdesk.Tool.DevKit;

/// <summary>Cron expressions in plain words, with the next times they fire.</summary>
internal static class Cron
{
    private static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
    private static readonly string[] Days = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };
    private static readonly string[] DayNames = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    public static string Explain(string expression)
    {
        var text = expression.Trim().ToLowerInvariant() switch
        {
            "@yearly" or "@annually" => "0 0 1 1 *",
            "@monthly" => "0 0 1 * *",
            "@weekly" => "0 0 * * 0",
            "@daily" or "@midnight" => "0 0 * * *",
            "@hourly" => "0 * * * *",
            var other => other,
        };
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) throw new FormatException("A cron expression has 5 fields: minute hour day-of-month month day-of-week.");
        var minutes = Field(parts[0], 0, 59, null);
        var hours = Field(parts[1], 0, 23, null);
        var monthDays = Field(parts[2], 1, 31, null);
        var months = Field(parts[3], 1, 12, Months);
        var weekDays = Field(parts[4], 0, 7, Days).Select(d => d % 7).ToHashSet();
        bool dayRestricted = parts[2] != "*", weekRestricted = parts[4] != "*";

        var sb = new StringBuilder();
        sb.Append($"minute        {Describe(parts[0], minutes)}\n");
        sb.Append($"hour          {Describe(parts[1], hours)}\n");
        sb.Append($"day of month  {Describe(parts[2], monthDays)}\n");
        sb.Append($"month         {(parts[3] == "*" ? "every month" : string.Join(", ", months.Order().Select(m => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m))))}\n");
        sb.Append($"day of week   {(parts[4] == "*" ? "every day" : string.Join(", ", weekDays.Order().Select(d => DayNames[d])))}\n");
        if (dayRestricted && weekRestricted) sb.Append("(runs when EITHER the day of month OR the day of week matches)\n");

        sb.Append("\nNEXT RUNS (local time)\n");
        var t = DateTime.Now;
        t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0).AddMinutes(1);
        var found = 0;
        for (var guard = 0; guard < 600_000 && found < 6; guard++, t = t.AddMinutes(1))
        {
            if (!months.Contains(t.Month)) { t = new DateTime(t.Year, t.Month, 1).AddMonths(1).AddMinutes(-1); continue; }
            var dayOk = dayRestricted && weekRestricted
                ? monthDays.Contains(t.Day) || weekDays.Contains((int)t.DayOfWeek)
                : monthDays.Contains(t.Day) && weekDays.Contains((int)t.DayOfWeek);
            if (!dayOk) { t = t.Date.AddDays(1).AddMinutes(-1); continue; }
            if (!hours.Contains(t.Hour)) { t = t.Date.AddHours(t.Hour + 1).AddMinutes(-1); continue; }
            if (!minutes.Contains(t.Minute)) continue;
            sb.Append($"{t:ddd yyyy-MM-dd HH:mm}\n");
            found++;
        }
        if (found == 0) sb.Append("never (no date matches)\n");
        return sb.ToString();
    }

    private static string Describe(string field, HashSet<int> values)
    {
        if (field == "*") return "every one";
        if (Regex.Match(field, @"^\*/(\d+)$") is { Success: true } m) return $"every {m.Groups[1].Value} ({string.Join(", ", values.Order().Take(12))}{(values.Count > 12 ? "…" : "")})";
        return string.Join(", ", values.Order());
    }

    private static HashSet<int> Field(string field, int min, int max, string[]? names)
    {
        var result = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var range = part;
            if (part.Split('/') is { Length: 2 } stepped)
            {
                range = stepped[0];
                step = int.Parse(stepped[1], CultureInfo.InvariantCulture);
                if (step <= 0) throw new FormatException("A step must be at least 1.");
            }
            int from, to;
            if (range == "*") (from, to) = (min, max);
            else if (range.Split('-') is { Length: 2 } bounds) (from, to) = (Value(bounds[0], names, min), Value(bounds[1], names, min));
            else
            {
                from = Value(range, names, min);
                to = part.Contains('/') ? max : from;
            }
            if (from < min || to > max || from > to) throw new FormatException($"\"{part}\" is outside {min}-{max}.");
            for (var v = from; v <= to; v += step) result.Add(v);
        }
        return result;
    }

    private static int Value(string text, string[]? names, int min)
    {
        if (names is not null && Array.IndexOf(names, text) is var i and >= 0) return i + (min == 1 ? 1 : 0);
        return int.TryParse(text, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"\"{text}\" is not a number.");
    }
}

/// <summary>Colors between HEX, RGB, HSL, HSV and code for engines, with WCAG contrast.</summary>
internal static class Colors
{
    private static readonly Dictionary<string, string> Named = new()
    {
        ["black"] = "000000", ["white"] = "ffffff", ["red"] = "ff0000", ["green"] = "008000", ["lime"] = "00ff00", ["blue"] = "0000ff",
        ["yellow"] = "ffff00", ["cyan"] = "00ffff", ["aqua"] = "00ffff", ["magenta"] = "ff00ff", ["fuchsia"] = "ff00ff", ["gray"] = "808080",
        ["grey"] = "808080", ["silver"] = "c0c0c0", ["maroon"] = "800000", ["olive"] = "808000", ["navy"] = "000080", ["purple"] = "800080",
        ["teal"] = "008080", ["orange"] = "ffa500", ["pink"] = "ffc0cb", ["gold"] = "ffd700", ["coral"] = "ff7f50", ["crimson"] = "dc143c",
        ["indigo"] = "4b0082", ["violet"] = "ee82ee", ["tomato"] = "ff6347", ["salmon"] = "fa8072", ["skyblue"] = "87ceeb", ["rebeccapurple"] = "663399",
        ["transparent"] = "00000000",
    };

    public static string Explain(string input)
    {
        var (r, g, b, a) = Parse(input.Trim().ToLowerInvariant());
        var (h, s, l) = Hsl(r, g, b);
        var (hv, sv, v) = Hsv(r, g, b);
        var inv = CultureInfo.InvariantCulture;
        string F(double x) => x.ToString("0.###", inv);
        var hex = $"#{r:x2}{g:x2}{b:x2}" + (a < 1 ? $"{(int)Math.Round(a * 255):x2}" : "");
        var white = Contrast(r, g, b, 255, 255, 255);
        var black = Contrast(r, g, b, 0, 0, 0);
        string Grade(double c) => c >= 7 ? "AAA" : c >= 4.5 ? "AA" : c >= 3 ? "AA large text only" : "fails";
        return $"HEX      {hex}\n" +
               $"RGB      rgb({r} {g} {b}{(a < 1 ? $" / {F(a)}" : "")})\n" +
               $"HSL      hsl({h:0} {s * 100:0}% {l * 100:0}%{(a < 1 ? $" / {F(a)}" : "")})\n" +
               $"HSV      {hv:0}°, {sv * 100:0}%, {v * 100:0}%\n" +
               $"0-1      {F(r / 255.0)}, {F(g / 255.0)}, {F(b / 255.0)}, {F(a)}\n" +
               $"Linear   {F(Linear(r))}, {F(Linear(g))}, {F(Linear(b))}  (for shaders working in linear space)\n\n" +
               $"Unity    new Color({F(r / 255.0)}f, {F(g / 255.0)}f, {F(b / 255.0)}f, {F(a)}f)\n" +
               $"Unreal   FLinearColor({F(Linear(r))}f, {F(Linear(g))}f, {F(Linear(b))}f, {F(a)}f)\n" +
               $"SwiftUI  Color(red: {F(r / 255.0)}, green: {F(g / 255.0)}, blue: {F(b / 255.0)}, opacity: {F(a)})\n" +
               $"Android  0x{(int)Math.Round(a * 255):X2}{r:X2}{g:X2}{b:X2}\n\n" +
               $"Contrast on white  {white:0.00}:1  {Grade(white)}\n" +
               $"Contrast on black  {black:0.00}:1  {Grade(black)}\n" +
               $"Readable text on it: {(white >= black ? "white" : "black")}";
    }

    private static (int R, int G, int B, double A) Parse(string t)
    {
        if (Named.TryGetValue(t, out var named)) t = "#" + named;
        if (Regex.Match(t, "^#?([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$") is { Success: true } hex)
        {
            var h = hex.Groups[1].Value;
            if (h.Length <= 4) h = string.Concat(h.Select(c => $"{c}{c}"));
            var a = h.Length == 8 ? Convert.ToInt32(h[6..8], 16) / 255.0 : 1;
            return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16), a);
        }
        var nums = Regex.Matches(t, @"-?[\d.]+%?").Select(m => m.Value).ToList();
        double N(string x, double scale) => x.EndsWith('%') ? double.Parse(x[..^1], CultureInfo.InvariantCulture) / 100 * scale : double.Parse(x, CultureInfo.InvariantCulture);
        double Alpha() => nums.Count > 3 ? Math.Clamp(N(nums[3], 1), 0, 1) : 1;
        if (t.StartsWith("rgb") && nums.Count >= 3)
            return ((int)Math.Clamp(Math.Round(N(nums[0], 255)), 0, 255), (int)Math.Clamp(Math.Round(N(nums[1], 255)), 0, 255), (int)Math.Clamp(Math.Round(N(nums[2], 255)), 0, 255), Alpha());
        if (t.StartsWith("hsl") && nums.Count >= 3)
        {
            var (r, g, b) = FromHsl(N(nums[0], 360), N(nums[1], 1) > 1 ? N(nums[1], 1) / 100 : N(nums[1], 1), N(nums[2], 1) > 1 ? N(nums[2], 1) / 100 : N(nums[2], 1));
            return (r, g, b, Alpha());
        }
        if (nums.Count >= 3 && !t.Contains('#'))
        {
            // Bare numbers: 0-1 floats or 0-255.
            var values = nums.Take(4).Select(x => double.Parse(x.TrimEnd('%'), CultureInfo.InvariantCulture)).ToList();
            var unit = values.Take(3).All(x => x <= 1);
            int C(double x) => (int)Math.Clamp(Math.Round(unit ? x * 255 : x), 0, 255);
            return (C(values[0]), C(values[1]), C(values[2]), values.Count > 3 ? Math.Clamp(values[3] > 1 ? values[3] / 255 : values[3], 0, 1) : 1);
        }
        throw new FormatException("Not a color. Try #ff3366, rgb(255 51 102), hsl(345 100% 60%) or 1 0.2 0.4.");
    }

    private static (double H, double S, double L) Hsl(int r, int g, int b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        return (Hue(rf, gf, bf, max, d), s, l);
    }

    private static (double H, double S, double V) Hsv(int r, int g, int b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        var d = max - min;
        return (d == 0 ? 0 : Hue(rf, gf, bf, max, d), max == 0 ? 0 : d / max, max);
    }

    private static double Hue(double r, double g, double b, double max, double d)
    {
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return h * 60;
    }

    private static (int, int, int) FromHsl(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360 / 360;
        double F(double t)
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            t = (t + 1) % 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        return ((int)Math.Round(F(h + 1 / 3.0) * 255), (int)Math.Round(F(h) * 255), (int)Math.Round(F(h - 1 / 3.0) * 255));
    }

    private static double Linear(int c)
    {
        var x = c / 255.0;
        return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
    }

    private static double Contrast(int r1, int g1, int b1, int r2, int g2, int b2)
    {
        double L(int r, int g, int b) => 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
        var (a, b) = (L(r1, g1, b1), L(r2, g2, b2));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}

/// <summary>Type definitions inferred from a JSON sample. Arrays of objects are merged; keys missing somewhere become optional.</summary>
internal static class Types
{
    private sealed record Shape(string Name, List<(string Key, string Type, bool Optional)> Fields);

    public static string TypeScript(JsonNode root) => Emit(root, "Root",
        (kind, inner) => kind switch { "string" => "string", "int" or "float" => "number", "bool" => "boolean", "any" => "unknown", "array" => inner + "[]", _ => inner },
        shape => $"export interface {shape.Name} {{\n" + string.Concat(shape.Fields.Select(f => $"  {(Regex.IsMatch(f.Key, "^[A-Za-z_$][\\w$]*$") ? f.Key : JsonSerializer.Serialize(f.Key))}{(f.Optional ? "?" : "")}: {f.Type};\n")) + "}\n");

    public static string CSharp(JsonNode root) => "using System.Text.Json.Serialization;\n\n" + Emit(root, "Root",
        (kind, inner) => kind switch { "string" => "string", "int" => "long", "float" => "double", "bool" => "bool", "any" => "object", "array" => $"List<{inner}>", _ => inner },
        shape => $"public sealed record {shape.Name}\n{{\n" + string.Concat(shape.Fields.Select(f =>
            $"    [JsonPropertyName({JsonSerializer.Serialize(f.Key)})]\n    public {f.Type}{(f.Optional || f.Type is "object" ? "?" : "")} {Pascal(f.Key)} {{ get; init; }}{(f.Optional || f.Type is "long" or "double" or "bool" or "object" ? "" : f.Type == "string" ? " = \"\";" : " = new();")}\n")) + "}\n");

    public static string Go(JsonNode root) => Emit(root, "Root",
        (kind, inner) => kind switch { "string" => "string", "int" => "int64", "float" => "float64", "bool" => "bool", "any" => "any", "array" => "[]" + inner, _ => inner },
        shape => $"type {shape.Name} struct {{\n" + string.Concat(shape.Fields.Select(f =>
            $"\t{Pascal(f.Key)} {(f.Optional && !f.Type.StartsWith("[]") ? "*" : "")}{f.Type} `json:\"{f.Key}{(f.Optional ? ",omitempty" : "")}\"`\n")) + "}\n");

    private static string Emit(JsonNode root, string rootName, Func<string, string, string> type, Func<Shape, string> write)
    {
        var shapes = new List<Shape>();
        var used = new HashSet<string>();

        string Unique(string name)
        {
            var n = name;
            for (var i = 2; !used.Add(n); i++) n = name + i;
            return n;
        }

        string TypeOf(List<JsonNode?> samples, string name)
        {
            var present = samples.Where(s => s is not null).ToList();
            if (present.Count == 0) return type("any", "");
            if (present.All(s => s is JsonObject)) return type("object", Build(present.Cast<JsonObject>().ToList(), name));
            if (present.All(s => s is JsonArray))
                return type("array", TypeOf(present.SelectMany(s => s!.AsArray()).ToList(), Singular(name)));
            var kinds = present.Select(s => s is JsonValue v ? v.GetValueKind() switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.True or JsonValueKind.False => "bool",
                JsonValueKind.Number => v.TryGetValue<long>(out _) ? "int" : "float",
                _ => "any",
            } : "any").Distinct().ToList();
            if (kinds.Count == 2 && kinds.Contains("int") && kinds.Contains("float")) return type("float", "");
            return type(kinds.Count == 1 ? kinds[0] : "any", "");
        }

        string Build(List<JsonObject> objects, string name)
        {
            var shape = new Shape(Unique(name), new());
            shapes.Add(shape);
            var keys = objects.SelectMany(o => o.Select(p => p.Key)).Distinct().ToList();
            foreach (var key in keys)
            {
                var values = objects.Select(o => o.TryGetPropertyValue(key, out var v) ? v : null).ToList();
                var optional = objects.Any(o => !o.ContainsKey(key) || o[key] is null);
                shape.Fields.Add((key, TypeOf(values, Pascal(key)), optional));
            }
            return shape.Name;
        }

        var top = TypeOf(new List<JsonNode?> { root }, rootName);
        var sb = new StringBuilder();
        foreach (var shape in shapes) sb.Append(write(shape)).Append('\n');
        if (root is JsonArray) sb.Append($"// The JSON is a list: {top}\n");
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Pascal(string key)
    {
        var words = Regex.Split(Regex.Replace(key, @"(?<=[a-z0-9])(?=[A-Z])", " "), @"[^A-Za-z0-9]+").Where(w => w.Length > 0).ToList();
        var name = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return name.Length == 0 ? "Field" : char.IsDigit(name[0]) ? "_" + name : name;
    }

    private static string Singular(string name)
        => name.EndsWith("ies") ? name[..^3] + "y" : name.EndsWith("ses") || name.EndsWith("xes") ? name[..^2] : name.EndsWith('s') && !name.EndsWith("ss") ? name[..^1] : name + "Item";
}
