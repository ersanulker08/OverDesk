using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Overdesk.Tool.DevKit;

/// <summary>One utility: turns the input text into output text, or throws a message for the user.</summary>
internal sealed record Converter(string Name, string Hint, Func<string, string> Run);

internal static class Converters
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static readonly IReadOnlyList<Converter> All =
    [
        new("JSON format", "Paste JSON to pretty-print it",
            s => JsonSerializer.Serialize(JsonDocument.Parse(s).RootElement, Pretty)),
        new("JSON minify", "Paste JSON to compact it",
            s => JsonSerializer.Serialize(JsonDocument.Parse(s).RootElement)),
        new("Base64 encode", "Text to encode",
            s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))),
        new("Base64 decode", "Base64 to decode",
            s => Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim()))),
        new("URL encode", "Text to encode",
            s => Uri.EscapeDataString(s)),
        new("URL decode", "Percent-encoded text to decode",
            s => Uri.UnescapeDataString(s)),
        new("SHA-256", "Text to hash",
            s => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))),
        new("SHA-1", "Text to hash",
            s => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)))),
        new("MD5", "Text to hash",
            s => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)))),
        new("UUID", "Anything (a new UUID per line is made for every input line; empty = one)",
            s => string.Join('\n', Enumerable.Range(0, Math.Max(1, s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length))
                .Select(_ => Guid.NewGuid().ToString()))),
        new("Unix time", "A Unix timestamp (seconds or milliseconds) or an ISO date",
            UnixTime),
    ];

    private static string UnixTime(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            var time = Math.Abs(n) >= 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
            return $"UTC    {time:yyyy-MM-dd HH:mm:ss}\nLocal  {time.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}";
        }
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return $"seconds       {date.ToUnixTimeSeconds()}\nmilliseconds  {date.ToUnixTimeMilliseconds()}";
        throw new FormatException("Not a timestamp or a date.");
    }
}
