using System.Text;
using System.Text.RegularExpressions;

namespace Overdesk.Tool.Converter;

/// <summary>SubRip (.srt) and WebVTT (.vtt) subtitles into each other: same cues, the other time format.</summary>
public static partial class Subtitles
{
    [GeneratedRegex(@"^\s*(\d{1,2}:)?(\d{1,2}):(\d{2})[.,](\d{1,3})\s*-->\s*(\d{1,2}:)?(\d{1,2}):(\d{2})[.,](\d{1,3})(.*)$")]
    private static partial Regex TimeLine();

    private sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    public static string Convert(string text, string to)
    {
        var cues = Parse(text);
        var sb = new StringBuilder();
        if (to == "vtt") sb.Append("WEBVTT\n\n");
        for (var i = 0; i < cues.Count; i++)
        {
            if (to == "srt") sb.Append(i + 1).Append('\n');
            sb.Append(Format(cues[i].Start, to)).Append(" --> ").Append(Format(cues[i].End, to)).Append('\n');
            sb.Append(cues[i].Text).Append("\n\n");
        }
        return sb.ToString();
    }

    private static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var lines = text.Replace("\r\n", "\n").TrimStart('﻿').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (TimeLine().Match(lines[i]) is not { Success: true } m) continue;
            var body = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++) body.Add(lines[i].TrimEnd());
            cues.Add(new Cue(Time(m, 1), Time(m, 5), string.Join("\n", body)));
        }
        if (cues.Count == 0) throw new FormatException("No subtitle cues found.");
        return cues;
    }

    private static TimeSpan Time(Match m, int at)
    {
        int Part(int group) => m.Groups[group].Success ? int.Parse(m.Groups[group].Value.TrimEnd(':')) : 0;
        var fraction = m.Groups[at + 3].Value.PadRight(3, '0');
        return new TimeSpan(0, Part(at), Part(at + 1), Part(at + 2), int.Parse(fraction));
    }

    private static string Format(TimeSpan t, string to)
        => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{(to == "srt" ? ',' : '.')}{t.Milliseconds:000}";
}
