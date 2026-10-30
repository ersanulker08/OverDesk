using System.Text.RegularExpressions;
using SkiaSharp;

namespace Overdesk.Tool.Converter;

/// <summary>
/// PDF output drawn with SkiaSharp, so it works the same on macOS and Windows: Markdown or plain text laid out on A4
/// pages (headings, paragraphs, lists, quotes, code, rules; inline marks are dropped), and images as one page each.
/// </summary>
public static partial class PdfWriter
{
    private const float PageWidth = 595, PageHeight = 842, Margin = 56;
    private const float ContentWidth = PageWidth - 2 * Margin;

    [GeneratedRegex(@"(\*\*|__|~~)")] private static partial Regex StrongMarks();
    [GeneratedRegex(@"`")] private static partial Regex CodeMarks();
    [GeneratedRegex(@"!?\[([^\]]*)\]\(([^)\s]+)\)")] private static partial Regex Links();
    [GeneratedRegex(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])")] private static partial Regex Italic();
    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")] private static partial Regex ListItem();

    private enum Style { Body, H1, H2, H3, Code, Quote }

    private sealed record Block(Style Style, string Text, string? Marker = null, int Indent = 0, bool Rule = false);

    /// <summary>Lay out Markdown (or plain text when <paramref name="markdown"/> is false) and write a PDF.</summary>
    public static void WriteText(string text, string output, bool markdown, string? title = null)
    {
        var blocks = markdown ? ParseMarkdown(text) : text.Replace("\r\n", "\n").Split("\n\n").Select(p => new Block(Style.Body, p.Trim('\n'))).ToList();
        if (title is not null && markdown && blocks.FirstOrDefault()?.Style != Style.H1) { /* keep the document as written */ }

        using var stream = File.Create(output);
        using var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = title ?? "", Creator = "OverDesk Converter", Producer = "OverDesk" });
        var page = new Page(document);
        foreach (var block in blocks)
        {
            if (block.Rule)
            {
                page.Space(8);
                page.Ensure(10);
                page.Canvas.DrawLine(Margin, page.Y, PageWidth - Margin, page.Y, new SKPaint { Color = new SKColor(0xD0, 0xD0, 0xD8), StrokeWidth = 1, IsAntialias = true });
                page.Space(12);
                continue;
            }
            Draw(page, block);
        }
        page.Close();
        document.Close();
    }

    /// <summary>One image per page, scaled to fit A4 (portrait or landscape, whichever suits it).</summary>
    public static void WriteImage(SKBitmap bitmap, string output)
    {
        using var stream = File.Create(output);
        using var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Creator = "OverDesk Converter", Producer = "OverDesk" });
        var landscape = bitmap.Width > bitmap.Height;
        var (w, h) = landscape ? (PageHeight, PageWidth) : (PageWidth, PageHeight);
        var canvas = document.BeginPage(w, h);
        var scale = Math.Min((w - 2 * 36) / bitmap.Width, (h - 2 * 36) / bitmap.Height);
        scale = Math.Min(scale, 1f * 72 / 96 * 2); // never blow small images up past 1.5x
        var rect = SKRect.Create((w - bitmap.Width * scale) / 2, (h - bitmap.Height * scale) / 2, bitmap.Width * scale, bitmap.Height * scale);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, rect, new SKSamplingOptions(SKCubicResampler.Mitchell));
        document.EndPage();
        document.Close();
    }

    // ---------------------------------------------------------------- layout

    private sealed class Page
    {
        private readonly SKDocument _document;

        public Page(SKDocument document)
        {
            _document = document;
            Canvas = document.BeginPage(PageWidth, PageHeight);
            Y = Margin;
        }

        public SKCanvas Canvas { get; private set; }
        public float Y { get; set; }

        public void Space(float points) => Y += points;

        /// <summary>Start a new page when <paramref name="height"/> no longer fits.</summary>
        public void Ensure(float height)
        {
            if (Y + height <= PageHeight - Margin) return;
            _document.EndPage();
            Canvas = _document.BeginPage(PageWidth, PageHeight);
            Y = Margin;
        }

        public void Close() => _document.EndPage();
    }

    private static SKFont Font(Style style)
    {
        var (family, bold, size) = style switch
        {
            Style.H1 => (Sans, true, 22f),
            Style.H2 => (Sans, true, 17f),
            Style.H3 => (Sans, true, 14f),
            Style.Code => (Mono, false, 9.5f),
            Style.Quote => (Sans, false, 11f),
            _ => (Sans, false, 11f),
        };
        var typeface = SKTypeface.FromFamilyName(family, bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                           style == Style.Quote ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright)
                       ?? SKTypeface.Default;
        return new SKFont(typeface, size);
    }

    private static string Sans => OperatingSystem.IsWindows() ? "Segoe UI" : "Helvetica Neue";
    private static string Mono => OperatingSystem.IsWindows() ? "Consolas" : "Menlo";

    private static void Draw(Page page, Block block)
    {
        using var font = Font(block.Style);
        using var paint = new SKPaint { IsAntialias = true, Color = block.Style == Style.Quote ? new SKColor(0x55, 0x55, 0x60) : new SKColor(0x1D, 0x1D, 0x1F) };
        var lineHeight = font.Size * (block.Style == Style.Code ? 1.35f : 1.45f);
        var before = block.Style switch { Style.H1 => 14f, Style.H2 => 12f, Style.H3 => 9f, _ => 0f };
        var indent = block.Indent * 16f + (block.Marker is null ? 0 : 16f) + (block.Style == Style.Quote ? 14f : 0) + (block.Style == Style.Code ? 10f : 0);
        var width = ContentWidth - indent - (block.Style == Style.Code ? 10f : 0);
        var lines = block.Style == Style.Code ? block.Text.Split('\n').SelectMany(l => Wrap(l, font, width, hard: true)).ToList() : Wrap(block.Text, font, width).ToList();

        page.Space(page.Y > Margin ? before : 0);
        if (block.Style == Style.Code)
        {
            // Code: a light box behind the lines, split across pages when it has to.
            var start = 0;
            while (start < lines.Count)
            {
                page.Ensure(lineHeight + 12);
                var room = (int)Math.Max(1, (PageHeight - Margin - page.Y - 12) / lineHeight);
                var count = Math.Min(room, lines.Count - start);
                var box = SKRect.Create(Margin, page.Y, ContentWidth, count * lineHeight + 12);
                page.Canvas.DrawRoundRect(box, 5, 5, new SKPaint { Color = new SKColor(0xF4, 0xF4, 0xF7), IsAntialias = true });
                var y = page.Y + 6;
                for (var i = start; i < start + count; i++, y += lineHeight)
                    page.Canvas.DrawText(lines[i], Margin + indent, y + font.Size, font, paint);
                page.Y = box.Bottom;
                start += count;
            }
            page.Space(8);
            return;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            page.Ensure(lineHeight);
            if (i == 0 && block.Marker is { } marker)
                page.Canvas.DrawText(marker, Margin + block.Indent * 16f, page.Y + font.Size, font, paint);
            if (block.Style == Style.Quote)
                page.Canvas.DrawRect(Margin, page.Y, 2.5f, lineHeight, new SKPaint { Color = new SKColor(0xD0, 0xD0, 0xD8) });
            page.Canvas.DrawText(lines[i], Margin + indent, page.Y + font.Size, font, paint);
            page.Y += lineHeight;
        }
        page.Space(block.Style is Style.H1 or Style.H2 or Style.H3 ? 4 : block.Marker is not null ? 3 : 8);
    }

    /// <summary>Greedy word wrap; words wider than the line (or every character in code) are broken.</summary>
    private static IEnumerable<string> Wrap(string text, SKFont font, float width, bool hard = false)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0) { yield return ""; continue; }
            var line = "";
            foreach (var word in hard ? new[] { paragraph } : paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (font.MeasureText(candidate) <= width) { line = candidate; continue; }
                if (line.Length > 0) { yield return line; line = ""; }
                var rest = word;
                while (font.MeasureText(rest) > width && rest.Length > 1)
                {
                    var fit = rest.Length - 1;
                    while (fit > 1 && font.MeasureText(rest[..fit]) > width) fit--;
                    yield return rest[..fit];
                    rest = rest[fit..];
                }
                line = rest;
            }
            if (line.Length > 0) yield return line;
        }
    }

    // ---------------------------------------------------------------- Markdown

    /// <summary>Text without its inline marks: links keep their label; bold, italic, strike and code marks go.</summary>
    private static string Plain(string text) => CodeMarks().Replace(Italic().Replace(StrongMarks().Replace(Links().Replace(text, "$1"), ""), "$1"), "");

    private static List<Block> ParseMarkdown(string markdown)
    {
        var blocks = new List<Block>();
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new Block(Style.Body, Plain(string.Join(" ", paragraph.Select(p => p.Trim())))));
            paragraph.Clear();
        }
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                var code = new List<string>();
                while (++i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i]);
                blocks.Add(new Block(Style.Code, string.Join("\n", code)));
                continue;
            }
            if (t.Length == 0) { Flush(); continue; }
            // An indented line right after a list item continues that item.
            if (paragraph.Count == 0 && line.StartsWith("  ", StringComparison.Ordinal) && blocks.LastOrDefault() is { Marker: not null } previous
                && !ListItem().IsMatch(line))
            {
                blocks[^1] = previous with { Text = previous.Text + " " + Plain(t) };
                continue;
            }
            var hashes = t.TakeWhile(c => c == '#').Count();
            if (hashes is > 0 and <= 6 && t.Length > hashes && t[hashes] == ' ')
            {
                Flush();
                blocks.Add(new Block(hashes == 1 ? Style.H1 : hashes == 2 ? Style.H2 : Style.H3, Plain(t[(hashes + 1)..].Trim())));
                continue;
            }
            if (t is "---" or "***" or "___") { Flush(); blocks.Add(new Block(Style.Body, "", Rule: true)); continue; }
            if (t.StartsWith('>')) { Flush(); blocks.Add(new Block(Style.Quote, Plain(t[1..].Trim()))); continue; }
            if (t.StartsWith('|'))
            {
                // Tables: one line per row, cells separated by spaces, separator rows skipped.
                Flush();
                if (!t.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Any())
                    continue;
                blocks.Add(new Block(Style.Body, Plain(string.Join("   ", t.Trim('|').Split('|').Select(c => c.Trim())))));
                continue;
            }
            if (ListItem().Match(line) is { Success: true } item)
            {
                Flush();
                var marker = item.Groups[2].Value;
                var body = item.Groups[3].Value;
                if (body.StartsWith("[ ] ", StringComparison.Ordinal)) { marker = "☐"; body = body[4..]; }
                else if (body.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) { marker = "☑"; body = body[4..]; }
                else if (!char.IsDigit(marker[0])) marker = "•";
                blocks.Add(new Block(Style.Body, Plain(body), marker, item.Groups[1].Value.Length / 2));
                continue;
            }
            paragraph.Add(line);
        }
        Flush();
        return blocks;
    }
}
