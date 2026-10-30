using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace Overdesk.Tool.DevKit;

/// <summary>
/// One utility: turns the input text (and, for some, a second value such as a key or a pattern) into output text, or
/// throws a message for the user.
/// </summary>
internal sealed record Utility(string Name, string Group, string Hint, Func<string, string, string> Run)
{
    /// <summary>Label of the second field (HMAC key, regex pattern, the other text of a diff); null when there is none.</summary>
    public string? Second { get; init; }

    /// <summary>Produces something useful with an empty input (generators).</summary>
    public bool RunsEmpty { get; init; }
}

internal static partial class Utilities
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly string[] Groups = { "Format", "Encode", "Hash", "Generate", "Text", "Time", "Web", "Numbers", "Code" };

    public static readonly IReadOnlyList<Utility> All =
    [
        // ------------------------------------------------ format
        new("JSON format", "Format", "Paste JSON to pretty-print it (comments and trailing commas are fine)",
            (s, _) => JsonSerializer.Serialize(ParseJson(s), Pretty)),
        new("JSON minify", "Format", "Paste JSON to compact it",
            (s, _) => JsonSerializer.Serialize(ParseJson(s))),
        new("JSON validate", "Format", "Paste JSON: says whether it is valid, and where it breaks",
            (s, _) =>
            {
                try
                {
                    var root = ParseJson(s);
                    return $"Valid JSON: {root.ValueKind.ToString().ToLowerInvariant()}" + root.ValueKind switch
                    {
                        JsonValueKind.Object => $" with {root.EnumerateObject().Count()} keys",
                        JsonValueKind.Array => $" with {root.GetArrayLength()} items",
                        _ => "",
                    };
                }
                catch (JsonException ex) { return $"Invalid: {ex.Message}"; }
            }),
        new("XML format", "Format", "Paste XML to indent it",
            (s, _) => System.Xml.Linq.XDocument.Parse(s).ToString()),

        // ------------------------------------------------ encode
        new("Base64 encode", "Encode", "Text to encode", (s, _) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))),
        new("Base64 decode", "Encode", "Base64 to decode (URL-safe Base64 works too)", (s, _) => Encoding.UTF8.GetString(FromBase64(s))),
        new("Base64URL encode", "Encode", "Text to encode with the URL-safe alphabet, no padding",
            (s, _) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_')),
        new("URL encode", "Encode", "Text to percent-encode", (s, _) => Uri.EscapeDataString(s)),
        new("URL decode", "Encode", "Percent-encoded text to decode", (s, _) => Uri.UnescapeDataString(s.Replace('+', ' '))),
        new("HTML encode", "Encode", "Text to escape for HTML (&lt; &amp; …)", (s, _) => WebUtility.HtmlEncode(s)),
        new("HTML decode", "Encode", "HTML entities to turn back into text", (s, _) => WebUtility.HtmlDecode(s)),
        new("Hex encode", "Encode", "Text to hex bytes (UTF-8)", (s, _) => Convert.ToHexStringLower(Encoding.UTF8.GetBytes(s))),
        new("Hex decode", "Encode", "Hex bytes (spaces, 0x and colons are ignored) to text",
            (s, _) => Encoding.UTF8.GetString(Convert.FromHexString(Regex.Replace(s, @"0x|[\s:,-]", "")))),
        new("String escape", "Encode", "Text to a JSON / C# / JavaScript string literal",
            (s, _) => JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })),
        new("String unescape", "Encode", "A quoted literal with \\n, \\t, \\u00e9 … to plain text",
            (s, _) =>
            {
                var t = s.Trim();
                if (!(t.Length >= 2 && t[0] == '"' && t[^1] == '"')) t = "\"" + t.Replace("\"", "\\\"") + "\"";
                return JsonSerializer.Deserialize<string>(t) ?? "";
            }),

        // ------------------------------------------------ hash
        new("SHA-256", "Hash", "Text to hash", (s, _) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))),
        new("SHA-512", "Hash", "Text to hash", (s, _) => Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(s)))),
        new("SHA-1", "Hash", "Text to hash", (s, _) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)))),
        new("MD5", "Hash", "Text to hash", (s, _) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)))),
        new("CRC32", "Hash", "Text to checksum", (s, _) => Crc32(Encoding.UTF8.GetBytes(s)).ToString("x8", Inv)),
        new("HMAC-SHA256", "Hash", "The message to sign",
            (s, key) => $"hex     {Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(s)))}\n" +
                        $"base64  {Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(s)))}")
            { Second = "Secret key" },
        new("Hash check", "Hash", "A hash; put the text in the second field to see whether its MD5 / SHA-1 / SHA-256 / SHA-512 matches",
            (hash, text) =>
            {
                var h = hash.Trim().ToLowerInvariant();
                var bytes = Encoding.UTF8.GetBytes(text);
                var candidates = new Dictionary<string, string>
                {
                    ["MD5"] = Convert.ToHexStringLower(MD5.HashData(bytes)),
                    ["SHA-1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)),
                    ["SHA-256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    ["SHA-512"] = Convert.ToHexStringLower(SHA512.HashData(bytes)),
                };
                var match = candidates.FirstOrDefault(c => c.Value == h);
                return match.Key is { } name ? $"✓ Matches: {name} of the text." : "✗ No MD5 / SHA-1 / SHA-256 / SHA-512 of the text matches this hash.";
            })
            { Second = "Text" },

        // ------------------------------------------------ generate
        new("UUID", "Generate", "How many (empty = 1)", (s, _) => string.Join('\n', Enumerable.Range(0, Count(s, 1)).Select(_ => Guid.NewGuid().ToString()))) { RunsEmpty = true },
        new("UUID v7 (sortable)", "Generate", "How many (empty = 1); v7 UUIDs sort by creation time",
            (s, _) => string.Join('\n', Enumerable.Range(0, Count(s, 1)).Select(_ => Guid.CreateVersion7().ToString()))) { RunsEmpty = true },
        new("ULID", "Generate", "How many (empty = 1)", (s, _) => string.Join('\n', Enumerable.Range(0, Count(s, 1)).Select(_ => Ulid()))) { RunsEmpty = true },
        new("Password", "Generate", "Length (empty = 24); add \"simple\" for letters and digits only",
            (s, _) => Password(s)) { RunsEmpty = true },
        new("Random hex / secret", "Generate", "Bytes of randomness (empty = 32): hex, Base64 and Base64URL",
            (s, _) =>
            {
                var bytes = RandomNumberGenerator.GetBytes(Count(s, 32, 1024));
                return $"hex        {Convert.ToHexStringLower(bytes)}\nbase64     {Convert.ToBase64String(bytes)}\nbase64url  {Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
            }) { RunsEmpty = true },
        new("Lorem ipsum", "Generate", "Paragraphs (empty = 3)", (s, _) => Lorem(Count(s, 3, 50))) { RunsEmpty = true },

        // ------------------------------------------------ text
        new("Case convert", "Text", "Any words: camelCase, snake_case, kebab-case… in every style at once", (s, _) => Cases(s)),
        new("Slugify", "Text", "A title to a URL slug", (s, _) => Slug(s)),
        new("Sort lines", "Text", "Lines to sort (natural order, case-insensitive)",
            (s, _) => string.Join('\n', Lines(s).Order(StringComparer.Create(Inv, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering)))),
        new("Unique lines", "Text", "Lines; duplicates are removed, order kept", (s, _) => string.Join('\n', Lines(s).Distinct())),
        new("Reverse lines", "Text", "Lines to put in reverse order", (s, _) => string.Join('\n', Lines(s).Reverse())),
        new("Text stats", "Text", "Any text: characters, words, lines, bytes", (s, _) => Stats(s)),
        new("Diff", "Text", "The original text", (a, b) => Diff(a, b)) { Second = "Changed text" },
        new("Regex test", "Text", "The text to search", (s, pattern) => RegexTest(s, pattern)) { Second = "Pattern (e.g. (\\w+)@(\\w+)\\.com)" },

        // ------------------------------------------------ time
        new("Unix time", "Time", "A Unix timestamp (seconds or milliseconds) or a date; empty = now", (s, _) => UnixTime(s)) { RunsEmpty = true },
        new("Cron explain", "Time", "A cron expression (min hour day month weekday), e.g. */15 9-17 * * 1-5", (s, _) => Cron.Explain(s)),

        // ------------------------------------------------ web
        new("JWT decode", "Web", "A JSON Web Token: header, payload and expiry (the signature is not checked)", (s, _) => Jwt(s)),
        new("URL parse", "Web", "A URL: its parts and query parameters", (s, _) => ParseUrl(s)),
        new("HTTP status", "Web", "A status code (e.g. 418) or words to look for", (s, _) => HttpStatus(s)),
        new("MIME type", "Web", "A file extension or name (e.g. .webp) or a MIME type", (s, _) => Mime(s)),

        // ------------------------------------------------ numbers
        new("Number bases", "Numbers", "A number: 255, 0xff, 0b1010, 0o17", (s, _) => Bases(s)),
        new("Color convert", "Numbers", "A color: #ff3366, rgb(255 51 102), hsl(345 100% 60%), or a CSS name", (s, _) => Colors.Explain(s)),
        new("Unix permissions", "Numbers", "chmod mode: 755 or rwxr-xr-x", (s, _) => Chmod(s)),
        new("Byte size", "Numbers", "A size: 1536, 1.5 MB, 2 GiB", (s, _) => Bytes(s)),

        // ------------------------------------------------ code
        new("JSON → TypeScript", "Code", "Sample JSON: interfaces for it", (s, _) => Types.TypeScript(ParseNode(s))),
        new("JSON → C#", "Code", "Sample JSON: C# records for it", (s, _) => Types.CSharp(ParseNode(s))),
        new("JSON → Go", "Code", "Sample JSON: Go structs with json tags", (s, _) => Types.Go(ParseNode(s))),
    ];

    public static Utility? Find(string name) => All.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- helpers

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static JsonElement ParseJson(string s) => JsonDocument.Parse(s, Lenient).RootElement;

    private static JsonNode ParseNode(string s) => JsonNode.Parse(s, documentOptions: Lenient) ?? throw new FormatException("Empty JSON.");

    private static byte[] FromBase64(string s)
    {
        var t = Regex.Replace(s, @"\s", "").Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static int Count(string s, int fallback, int max = 1000)
        => int.TryParse(Regex.Match(s, @"\d+").Value, out var n) ? Math.Clamp(n, 1, max) : fallback;

    private static IEnumerable<string> Lines(string s) => s.Replace("\r\n", "\n").Split('\n');

    private static string Ulid()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var bytes = new byte[16];
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--, time >>= 8) bytes[i] = (byte)time;
        RandomNumberGenerator.Fill(bytes.AsSpan(6));
        var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[26];
        for (var i = 25; i >= 0; i--, value >>= 5) chars[i] = alphabet[(int)(value & 31)];
        return new string(chars);
    }

    private static string Password(string s)
    {
        var simple = s.Contains("simple", StringComparison.OrdinalIgnoreCase);
        var length = Count(s, 24, 256);
        const string letters = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var alphabet = simple ? letters : letters + "!@#$%^&*-_=+?";
        var lines = new List<string>();
        for (var n = 0; n < 5; n++)
            lines.Add(new string(Enumerable.Range(0, length).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray()));
        var bits = length * Math.Log2(alphabet.Length);
        return string.Join('\n', lines) + $"\n\n≈ {bits:0} bits of entropy each";
    }

    private static string Lorem(int paragraphs)
    {
        var words = ("lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua ut enim ad minim " +
                     "veniam quis nostrud exercitation ullamco laboris nisi aliquip ex ea commodo consequat duis aute irure in reprehenderit voluptate velit esse " +
                     "cillum fugiat nulla pariatur excepteur sint occaecat cupidatat non proident sunt culpa qui officia deserunt mollit anim id est laborum").Split(' ');
        var random = new Random(paragraphs);
        var result = new List<string>();
        for (var p = 0; p < paragraphs; p++)
        {
            var sentences = new List<string>();
            for (var s = 0; s < 4 + random.Next(3); s++)
            {
                var sentence = string.Join(' ', Enumerable.Range(0, 8 + random.Next(9)).Select(_ => words[random.Next(words.Length)]));
                sentences.Add(char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".");
            }
            result.Add(p == 0 ? "Lorem ipsum dolor sit amet, " + string.Join(' ', sentences)[0..1].ToLowerInvariant() + string.Join(' ', sentences)[1..] : string.Join(' ', sentences));
        }
        return string.Join("\n\n", result);
    }

    private static List<string> Words(string s)
    {
        // Split on separators and on case changes: "parseHTTPResponse_v2" → parse, HTTP, Response, v2.
        var spaced = Regex.Replace(s, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return Regex.Split(spaced, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
    }

    private static string Cases(string s)
    {
        var w = Words(s);
        if (w.Count == 0) return "";
        string Cap(string x) => char.ToUpperInvariant(x[0]) + x[1..].ToLowerInvariant();
        var lower = w.Select(x => x.ToLowerInvariant()).ToList();
        return string.Join('\n',
            $"camelCase      {lower[0]}{string.Concat(w.Skip(1).Select(Cap))}",
            $"PascalCase     {string.Concat(w.Select(Cap))}",
            $"snake_case     {string.Join('_', lower)}",
            $"kebab-case     {string.Join('-', lower)}",
            $"CONSTANT_CASE  {string.Join('_', w.Select(x => x.ToUpperInvariant()))}",
            $"dot.case       {string.Join('.', lower)}",
            $"Title Case     {string.Join(' ', w.Select(Cap))}",
            $"Sentence case  {Cap(w[0])} {string.Join(' ', lower.Skip(1))}".TrimEnd(),
            $"lower case     {string.Join(' ', lower)}",
            $"UPPER CASE     {string.Join(' ', w.Select(x => x.ToUpperInvariant()))}");
    }

    private static string Slug(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        var ascii = sb.ToString().Replace('ı', 'i').Replace('ß', 's').Replace('ø', 'o').Replace('æ', 'a').Replace('đ', 'd').Replace('ł', 'l');
        return Regex.Replace(Regex.Replace(ascii.ToLowerInvariant(), @"[^a-z0-9]+", "-"), "^-+|-+$", "");
    }

    private static string Stats(string s)
    {
        var words = Regex.Matches(s, @"[\p{L}\p{N}'’-]+").Count;
        var lines = s.Length == 0 ? 0 : Lines(s).Count();
        var graphemes = new StringInfo(s).LengthInTextElements;
        var minutes = words / 230.0;
        return $"characters   {graphemes:N0}\n" +
               $"no spaces    {s.Count(c => !char.IsWhiteSpace(c)):N0}\n" +
               $"words        {words:N0}\n" +
               $"lines        {lines:N0}\n" +
               $"paragraphs   {Regex.Split(s.Trim(), @"\n\s*\n").Count(p => p.Trim().Length > 0):N0}\n" +
               $"UTF-8 bytes  {Encoding.UTF8.GetByteCount(s):N0}\n" +
               $"reading      {(minutes < 1 ? "under a minute" : $"{minutes:0} min")}";
    }

    /// <summary>A line diff (longest common subsequence): "-" removed, "+" added, "  " unchanged.</summary>
    private static string Diff(string a, string b)
    {
        var x = Lines(a).ToArray();
        var y = Lines(b).ToArray();
        if ((long)x.Length * y.Length > 25_000_000) throw new InvalidOperationException("The texts are too long to compare here.");
        var lcs = new int[x.Length + 1, y.Length + 1];
        for (var i = x.Length - 1; i >= 0; i--)
            for (var j = y.Length - 1; j >= 0; j--)
                lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var sb = new StringBuilder();
        int p = 0, q = 0, added = 0, removed = 0;
        while (p < x.Length || q < y.Length)
        {
            if (p < x.Length && q < y.Length && x[p] == y[q]) { sb.Append("  ").Append(x[p]).Append('\n'); p++; q++; }
            else if (q < y.Length && (p == x.Length || lcs[p, q + 1] >= lcs[p + 1, q])) { sb.Append("+ ").Append(y[q]).Append('\n'); q++; added++; }
            else { sb.Append("- ").Append(x[p]).Append('\n'); p++; removed++; }
        }
        return added + removed == 0 ? "The texts are the same." : $"{removed} removed, {added} added\n\n{sb}";
    }

    private static string RegexTest(string text, string pattern)
    {
        if (pattern.Length == 0) return "Type a pattern in the second field.";
        var regex = new Regex(pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        var matches = regex.Matches(text);
        if (matches.Count == 0) return "No matches.";
        var sb = new StringBuilder($"{matches.Count} match{(matches.Count == 1 ? "" : "es")}\n");
        foreach (Match m in matches.Take(200))
        {
            sb.Append($"\n[{m.Index}] {m.Value}");
            for (var g = 1; g < m.Groups.Count; g++)
            {
                var name = regex.GroupNameFromNumber(g);
                sb.Append($"\n    {(name == g.ToString(Inv) ? "$" + g : name)} = {(m.Groups[g].Success ? m.Groups[g].Value : "(no match)")}");
            }
        }
        return sb.ToString();
    }

    private static string UnixTime(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTimeOffset.UtcNow;
            return $"seconds       {now.ToUnixTimeSeconds()}\nmilliseconds  {now.ToUnixTimeMilliseconds()}\nUTC           {now:yyyy-MM-dd HH:mm:ss}\nISO 8601      {now:yyyy-MM-ddTHH:mm:ssZ}";
        }
        if (long.TryParse(s, NumberStyles.Integer, Inv, out var n))
        {
            var time = Math.Abs(n) >= 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
            var ago = DateTimeOffset.UtcNow - time;
            return $"UTC       {time:yyyy-MM-dd HH:mm:ss}\nLocal     {time.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\nISO 8601  {time:yyyy-MM-ddTHH:mm:ssZ}\nRelative  {Relative(ago)}";
        }
        if (DateTimeOffset.TryParse(s, Inv, DateTimeStyles.AssumeUniversal, out var date))
            return $"seconds       {date.ToUnixTimeSeconds()}\nmilliseconds  {date.ToUnixTimeMilliseconds()}\nISO 8601      {date.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";
        throw new FormatException("Not a timestamp or a date.");
    }

    private static string Relative(TimeSpan ago)
    {
        var future = ago < TimeSpan.Zero;
        var span = future ? -ago : ago;
        var text = span.TotalDays >= 365 ? $"{span.TotalDays / 365:0.#} years" : span.TotalDays >= 1 ? $"{span.TotalDays:0} days"
            : span.TotalHours >= 1 ? $"{span.TotalHours:0} hours" : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} minutes" : $"{span.TotalSeconds:0} seconds";
        return future ? $"in {text}" : $"{text} ago";
    }

    private static string Jwt(string s)
    {
        var parts = s.Trim().Replace("Bearer ", "").Split('.');
        if (parts.Length < 2) throw new FormatException("A JWT has three parts separated by dots.");
        string Part(string p) => JsonSerializer.Serialize(JsonDocument.Parse(FromBase64(p)).RootElement, Pretty);
        var payload = JsonNode.Parse(FromBase64(parts[1]));
        var sb = new StringBuilder("HEADER\n").Append(Part(parts[0])).Append("\n\nPAYLOAD\n").Append(Part(parts[1]));
        foreach (var claim in new[] { "iat", "nbf", "exp" })
            if (payload?[claim] is JsonValue v && v.TryGetValue<long>(out var t))
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(t);
                sb.Append($"\n{claim}  {time:yyyy-MM-dd HH:mm:ss} UTC ({Relative(DateTimeOffset.UtcNow - time)})");
            }
        if (payload?["exp"] is JsonValue e && e.TryGetValue<long>(out var exp))
            sb.Append(DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow ? "\n\n✗ Expired" : "\n\n✓ Not expired");
        return sb.ToString();
    }

    private static string ParseUrl(string s)
    {
        var uri = new Uri(s.Trim().Contains("://") ? s.Trim() : "https://" + s.Trim());
        var sb = new StringBuilder();
        sb.Append($"scheme    {uri.Scheme}\nhost      {uri.Host}\nport      {uri.Port}{(uri.IsDefaultPort ? " (default)" : "")}\npath      {Uri.UnescapeDataString(uri.AbsolutePath)}\n");
        if (uri.UserInfo.Length > 0) sb.Append($"user      {uri.UserInfo}\n");
        if (uri.Fragment.Length > 0) sb.Append($"fragment  {Uri.UnescapeDataString(uri.Fragment[1..])}\n");
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (query.Count > 0)
        {
            sb.Append("\nQUERY\n");
            foreach (var key in query.AllKeys) sb.Append($"{key} = {query[key]}\n");
        }
        return sb.ToString();
    }

    private static string Bases(string s)
    {
        var t = s.Trim().Replace("_", "").Replace(" ", "").ToLowerInvariant();
        var negative = t.StartsWith('-');
        if (negative) t = t[1..];
        System.Numerics.BigInteger value = t switch
        {
            _ when t.StartsWith("0x") => System.Numerics.BigInteger.Parse("0" + t[2..], NumberStyles.HexNumber, Inv),
            _ when t.StartsWith("0b") => t[2..].Aggregate(System.Numerics.BigInteger.Zero, (v, c) => c is '0' or '1' ? v * 2 + (c - '0') : throw new FormatException("Not binary.")),
            _ when t.StartsWith("0o") => t[2..].Aggregate(System.Numerics.BigInteger.Zero, (v, c) => c is >= '0' and <= '7' ? v * 8 + (c - '0') : throw new FormatException("Not octal.")),
            _ => System.Numerics.BigInteger.Parse(t, Inv),
        };
        if (negative) value = -value;
        string To(int radix)
        {
            if (value.IsZero) return "0";
            var v = System.Numerics.BigInteger.Abs(value);
            var sb = new StringBuilder();
            while (v > 0) { sb.Insert(0, "0123456789abcdef"[(int)(v % radix)]); v /= radix; }
            return (value.Sign < 0 ? "-" : "") + sb;
        }
        var bin = To(2);
        var grouped = Regex.Replace(bin.TrimStart('-').PadLeft((bin.TrimStart('-').Length + 3) / 4 * 4, '0'), ".{4}(?!$)", "$0 ");
        return $"decimal  {value:N0}\nhex      0x{To(16)}\noctal    0o{To(8)}\nbinary   {(value.Sign < 0 ? "-" : "")}{grouped}\nbits     {(value.IsZero ? 1 : (int)Math.Ceiling(System.Numerics.BigInteger.Log(System.Numerics.BigInteger.Abs(value) + 1, 2)))}";
    }

    private static string Chmod(string s)
    {
        var t = s.Trim();
        int mode;
        if (Regex.IsMatch(t, "^[0-7]{3,4}$")) mode = Convert.ToInt32(t, 8);
        else if (Regex.IsMatch(t, "^[-dl]?[rwx-]{9}$"))
        {
            var bits = t[^9..];
            mode = 0;
            for (var i = 0; i < 9; i++) if (bits[i] != '-') mode |= 1 << (8 - i);
        }
        else throw new FormatException("Use an octal mode (755) or rwx letters (rwxr-xr-x).");
        string Who(int shift) => $"{((mode >> shift & 4) != 0 ? "read " : "")}{((mode >> shift & 2) != 0 ? "write " : "")}{((mode >> shift & 1) != 0 ? "execute" : "")}".Trim() is { Length: > 0 } w ? w : "nothing";
        var letters = string.Concat(Enumerable.Range(0, 9).Select(i => (mode >> (8 - i) & 1) != 0 ? "rwx"[i % 3] : '-'));
        return $"octal    {Convert.ToString(mode & 0xFFF, 8).PadLeft(3, '0')}\nletters  {letters}\n\nowner    {Who(6)}\ngroup    {Who(3)}\nothers   {Who(0)}\n\nchmod {Convert.ToString(mode & 0xFFF, 8).PadLeft(3, '0')} file";
    }

    private static string Bytes(string s)
    {
        var m = Regex.Match(s.Trim(), @"^([\d.,]+)\s*([kmgtp]?)(i?)b?$", RegexOptions.IgnoreCase);
        if (!m.Success) throw new FormatException("Write a size like 1536, 1.5 MB or 2 GiB.");
        var number = double.Parse(m.Groups[1].Value.Replace(",", ""), Inv);
        var power = "kmgtp".IndexOf(m.Groups[2].Value.ToLowerInvariant(), StringComparison.Ordinal) + 1;
        var binary = m.Groups[3].Value.Length > 0;
        var bytes = number * Math.Pow(binary ? 1024 : 1000, m.Groups[2].Value.Length == 0 ? 0 : power);
        var sb = new StringBuilder($"bytes  {bytes:N0}\n\n");
        string[] si = { "kB", "MB", "GB", "TB", "PB" }, iec = { "KiB", "MiB", "GiB", "TiB", "PiB" };
        for (var i = 0; i < 5; i++) sb.Append($"{bytes / Math.Pow(1000, i + 1),16:N3} {si[i],-4}   {bytes / Math.Pow(1024, i + 1),16:N3} {iec[i]}\n");
        return sb.ToString();
    }

    private static string HttpStatus(string s)
    {
        var codes = new Dictionary<int, string>
        {
            [100] = "Continue", [101] = "Switching Protocols", [200] = "OK", [201] = "Created", [202] = "Accepted", [204] = "No Content",
            [206] = "Partial Content", [301] = "Moved Permanently", [302] = "Found", [303] = "See Other", [304] = "Not Modified",
            [307] = "Temporary Redirect", [308] = "Permanent Redirect", [400] = "Bad Request", [401] = "Unauthorized (not signed in)",
            [403] = "Forbidden (signed in, not allowed)", [404] = "Not Found", [405] = "Method Not Allowed", [406] = "Not Acceptable",
            [408] = "Request Timeout", [409] = "Conflict", [410] = "Gone", [411] = "Length Required", [412] = "Precondition Failed",
            [413] = "Content Too Large", [414] = "URI Too Long", [415] = "Unsupported Media Type", [418] = "I'm a teapot",
            [422] = "Unprocessable Content (validation failed)", [425] = "Too Early", [426] = "Upgrade Required", [428] = "Precondition Required",
            [429] = "Too Many Requests (rate limited)", [431] = "Request Header Fields Too Large", [451] = "Unavailable For Legal Reasons",
            [500] = "Internal Server Error", [501] = "Not Implemented", [502] = "Bad Gateway", [503] = "Service Unavailable",
            [504] = "Gateway Timeout", [505] = "HTTP Version Not Supported", [507] = "Insufficient Storage", [511] = "Network Authentication Required",
        };
        var t = s.Trim();
        var found = int.TryParse(t, out var code)
            ? codes.Where(c => c.Key == code || (t.Length == 1 && c.Key / 100 == code))
            : codes.Where(c => c.Value.Contains(t, StringComparison.OrdinalIgnoreCase));
        var lines = found.Select(c => $"{c.Key}  {c.Value}").ToList();
        return lines.Count > 0 ? string.Join('\n', lines) : "No such status code.";
    }

    private static string Mime(string s)
    {
        var map = new Dictionary<string, string>
        {
            ["html"] = "text/html", ["css"] = "text/css", ["js"] = "text/javascript", ["mjs"] = "text/javascript", ["json"] = "application/json",
            ["xml"] = "application/xml", ["txt"] = "text/plain", ["csv"] = "text/csv", ["md"] = "text/markdown", ["yaml"] = "application/yaml",
            ["png"] = "image/png", ["jpg"] = "image/jpeg", ["jpeg"] = "image/jpeg", ["gif"] = "image/gif", ["webp"] = "image/webp", ["avif"] = "image/avif",
            ["svg"] = "image/svg+xml", ["ico"] = "image/vnd.microsoft.icon", ["bmp"] = "image/bmp", ["heic"] = "image/heic", ["tiff"] = "image/tiff",
            ["mp3"] = "audio/mpeg", ["wav"] = "audio/wav", ["ogg"] = "audio/ogg", ["m4a"] = "audio/mp4", ["flac"] = "audio/flac", ["opus"] = "audio/opus",
            ["mp4"] = "video/mp4", ["webm"] = "video/webm", ["mov"] = "video/quicktime", ["mkv"] = "video/x-matroska", ["avi"] = "video/x-msvideo",
            ["pdf"] = "application/pdf", ["zip"] = "application/zip", ["gz"] = "application/gzip", ["tar"] = "application/x-tar", ["7z"] = "application/x-7z-compressed",
            ["wasm"] = "application/wasm", ["woff"] = "font/woff", ["woff2"] = "font/woff2", ["ttf"] = "font/ttf", ["otf"] = "font/otf",
            ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ["glb"] = "model/gltf-binary", ["gltf"] = "model/gltf+json", ["stl"] = "model/stl", ["obj"] = "model/obj",
            ["bin"] = "application/octet-stream", ["exe"] = "application/vnd.microsoft.portable-executable", ["apk"] = "application/vnd.android.package-archive",
        };
        var t = s.Trim().ToLowerInvariant();
        if (t.Contains('/'))
        {
            var exts = map.Where(m => m.Value == t).Select(m => "." + m.Key).ToList();
            return exts.Count > 0 ? string.Join(", ", exts) : "Unknown MIME type.";
        }
        var ext = Path.GetExtension(t.StartsWith('.') ? t : "." + t).TrimStart('.');
        if (ext.Length == 0) ext = t;
        return map.TryGetValue(ext, out var mime) ? mime : "Unknown extension (application/octet-stream is the safe default).";
    }
}
