using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Overdesk.Tool.Converter;

/// <summary>
/// Markdown to a clean standalone HTML page (headings, paragraphs, lists, task lists, quotes, code, tables, links,
/// emphasis), plus the reverse direction for plain text out of HTML.
/// </summary>
public static partial class Markdown
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")] private static partial Regex Rule();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")] private static partial Regex TableSeparator();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"\*\*(.+?)\*\*|__(.+?)__")] private static partial Regex Bold();
    [GeneratedRegex(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])|(?<![\w_])_(?!\s)(.+?)(?<!\s)_(?![\w_])")] private static partial Regex Italic();
    [GeneratedRegex(@"~~(.+?)~~")] private static partial Regex Strike();
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]+)\)")] private static partial Regex Image();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")] private static partial Regex Link();

    private const string Style = """
        body{font:16px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI",Helvetica,Arial,sans-serif;max-width:760px;margin:40px auto;padding:0 20px;color:#1d1d1f}
        h1,h2,h3{line-height:1.25}code{font-family:Menlo,Consolas,monospace;background:#f2f2f5;padding:1px 5px;border-radius:4px;font-size:.92em}
        pre{background:#f5f5f8;padding:12px 14px;border-radius:8px;overflow:auto}pre code{background:none;padding:0}
        blockquote{border-left:3px solid #d0d0d8;margin:0;padding:0 14px;color:#555}table{border-collapse:collapse}
        th,td{border:1px solid #ddd;padding:6px 10px;text-align:left}th{background:#f5f5f8}img{max-width:100%}
        """;

    public static string ToHtmlPage(string markdown, string title)
        => $"<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>\n<style>{Style}</style></head>\n<body>\n{ToHtml(markdown)}</body></html>\n";

    public static string PlainToHtmlPage(string text, string title)
    {
        var body = string.Join("\n", text.Replace("\r\n", "\n").Split("\n\n").Select(p => $"<p>{WebUtility.HtmlEncode(p).Replace("\n", "<br>")}</p>"));
        return $"<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>\n<style>{Style}</style></head>\n<body>\n{body}\n</body></html>\n";
    }

    public static string ToHtml(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var html = new StringBuilder();
        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>").Append(string.Join("<br>\n", paragraph.Select(Inline))).Append("</p>\n");
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var language = trimmed[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i++]);
                i++;
                html.Append(language.Length > 0 ? $"<pre><code class=\"language-{WebUtility.HtmlEncode(language)}\">" : "<pre><code>")
                    .Append(WebUtility.HtmlEncode(string.Join("\n", code))).Append("</code></pre>\n");
                continue;
            }
            if (trimmed.Length == 0) { FlushParagraph(); i++; continue; }
            if (Heading().Match(trimmed) is { Success: true } h)
            {
                FlushParagraph();
                html.Append($"<h{h.Groups[1].Length}>{Inline(h.Groups[2].Value)}</h{h.Groups[1].Length}>\n");
                i++;
                continue;
            }
            if (Rule().IsMatch(trimmed) && paragraph.Count == 0) { html.Append("<hr>\n"); i++; continue; }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>')) quote.Add(lines[i++].TrimStart()[1..].TrimStart());
                html.Append("<blockquote>\n").Append(ToHtml(string.Join("\n", quote))).Append("</blockquote>\n");
                continue;
            }
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1]))
            {
                FlushParagraph();
                static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                html.Append("<table>\n<tr>").Append(string.Concat(Cells(line).Select(c => $"<th>{Inline(c)}</th>"))).Append("</tr>\n");
                i += 2;
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                    html.Append("<tr>").Append(string.Concat(Cells(lines[i++]).Select(c => $"<td>{Inline(c)}</td>"))).Append("</tr>\n");
                html.Append("</table>\n");
                continue;
            }
            if (ListItem().Match(line) is { Success: true } first)
            {
                FlushParagraph();
                var ordered = char.IsDigit(first.Groups[2].Value[0]);
                html.Append(ordered ? "<ol>\n" : "<ul>\n");
                while (i < lines.Length && ListItem().Match(lines[i]) is { Success: true } item)
                {
                    var body = item.Groups[3].Value;
                    if (body.StartsWith("[ ] ", StringComparison.Ordinal)) body = "&#9744; " + Inline(body[4..]);
                    else if (body.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) body = "&#9745; " + Inline(body[4..]);
                    else body = Inline(body);
                    html.Append("<li>").Append(body).Append("</li>\n");
                    i++;
                }
                html.Append(ordered ? "</ol>\n" : "</ul>\n");
                continue;
            }
            paragraph.Add(line);
            i++;
        }
        FlushParagraph();
        return html.ToString();
    }

    /// <summary>Inline Markdown on HTML-escaped text; code spans are protected from the other rules.</summary>
    private static string Inline(string text)
    {
        var codes = new List<string>();
        text = InlineCode().Replace(text, m =>
        {
            codes.Add($"<code>{WebUtility.HtmlEncode(m.Groups[1].Value)}</code>");
            return $"\u0001{codes.Count - 1}\u0001";
        });
        text = WebUtility.HtmlEncode(text);
        text = Image().Replace(text, m => $"<img src=\"{m.Groups[2].Value}\" alt=\"{m.Groups[1].Value}\">");
        text = Link().Replace(text, m => $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
        text = Bold().Replace(text, m => $"<strong>{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}</strong>");
        text = Italic().Replace(text, m => $"<em>{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}</em>");
        text = Strike().Replace(text, m => $"<del>{m.Groups[1].Value}</del>");
        return Regex.Replace(text, "\u0001(\\d+)\u0001", m => codes[int.Parse(m.Groups[1].Value)]);
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex Scripts();
    [GeneratedRegex(@"<\s*(br|/p|/div|/h\d|/li|/tr)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex Breaks();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex BlankLines();

    public static string HtmlToText(string html)
    {
        var text = Scripts().Replace(html, "");
        text = Breaks().Replace(text, "\n");
        text = WebUtility.HtmlDecode(Tags().Replace(text, ""));
        return BlankLines().Replace(string.Join("\n", text.Split('\n').Select(l => l.Trim())), "\n\n").Trim() + "\n";
    }
}
