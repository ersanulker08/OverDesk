using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls;

/// <summary>
/// The Markdown AI answers use, drawn with native controls: headings, paragraphs, bullet and numbered lists, quotes,
/// rules, tables and fenced code blocks (with a copy button), plus inline bold, italic, strike, code and links.
/// Not a full CommonMark parser: just what chat answers actually contain, and it never throws on half-streamed text.
/// </summary>
internal static partial class ChatMarkdown
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")] private static partial Regex Rule();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")] private static partial Regex TableSeparator();

    public static IEnumerable<Control> Render(string text, Func<string, Task> copy)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var i = 0;

        IEnumerable<Control> FlushParagraph()
        {
            if (paragraph.Count == 0) yield break;
            yield return Paragraph(string.Join("\n", paragraph));
            paragraph.Clear();
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                foreach (var c in FlushParagraph()) yield return c;
                var language = trimmed[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i++]);
                i++; // closing fence (absent while the answer is still streaming)
                yield return CodeBlock(language, string.Join("\n", code), copy);
                continue;
            }
            if (trimmed.Length == 0)
            {
                foreach (var c in FlushParagraph()) yield return c;
                i++;
                continue;
            }
            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                foreach (var c in FlushParagraph()) yield return c;
                var level = heading.Groups[1].Length;
                var block = Inline(heading.Groups[2].Value, level <= 2 ? 16 : 14);
                block.FontWeight = FontWeight.Bold;
                block.Margin = new Thickness(0, 4, 0, 0);
                yield return block;
                i++;
                continue;
            }
            if (Rule().IsMatch(trimmed) && paragraph.Count == 0)
            {
                yield return new Border { Height = 1, Background = Res.Brush("StrokeStrongBrush"), Margin = new Thickness(0, 4) };
                i++;
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                foreach (var c in FlushParagraph()) yield return c;
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>')) quote.Add(lines[i++].TrimStart()[1..].TrimStart());
                yield return new Border
                {
                    BorderBrush = Res.Brush("AccentBrush"), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2),
                    Child = Paragraph(string.Join("\n", quote), muted: true),
                };
                continue;
            }
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1]))
            {
                foreach (var c in FlushParagraph()) yield return c;
                var rows = new List<string> { line };
                i += 2;
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|')) rows.Add(lines[i++]);
                yield return Table(rows);
                continue;
            }
            if (ListItem().Match(line) is { Success: true })
            {
                foreach (var c in FlushParagraph()) yield return c;
                var list = new StackPanel { Spacing = 3 };
                while (i < lines.Length && ListItem().Match(lines[i]) is { Success: true } item)
                {
                    var indent = item.Groups[1].Value.Replace("\t", "    ").Length / 2;
                    var marker = item.Groups[2].Value;
                    var body = new StringBuilder(item.Groups[3].Value);
                    i++;
                    // Lines that continue the item (indented, not a new item or block).
                    while (i < lines.Length && lines[i].StartsWith("  ", StringComparison.Ordinal) && lines[i].Trim().Length > 0
                           && !ListItem().IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                        body.Append('\n').Append(lines[i++].Trim());
                    list.Children.Add(ListRow(marker, body.ToString(), indent));
                }
                yield return list;
                continue;
            }
            paragraph.Add(line);
            i++;
        }
        foreach (var c in FlushParagraph()) yield return c;
    }

    private static SelectableTextBlock Paragraph(string text, bool muted = false)
    {
        var block = Inline(text, 13);
        if (muted) block.Foreground = Res.Brush("MutedBrush");
        return block;
    }

    private static Control ListRow(string marker, string text, int indent)
    {
        var numbered = char.IsDigit(marker[0]);
        var task = !numbered && (text.StartsWith("[ ] ", StringComparison.Ordinal) || text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase));
        Control glyph = task
            ? new Avalonia.Controls.Shapes.Path
            {
                Classes = { "icon", "small" }, Height = 11, Margin = new Thickness(0, 4, 0, 0),
                Data = Res.Icon(text[1] == ' ' ? "IconStop" : "IconCheck"),
                Stroke = Res.Brush(text[1] == ' ' ? "FaintBrush" : "SuccessBrush"),
            }
            : new TextBlock
            {
                Text = numbered ? marker : indent > 0 ? "◦" : "•", FontSize = 13, Foreground = Res.Brush(numbered ? "MutedBrush" : "AccentBrush"),
                FontWeight = numbered ? FontWeight.SemiBold : FontWeight.Bold,
            };
        if (task) text = text[4..];
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(indent * 16, 0, 0, 0) };
        glyph.MinWidth = numbered ? 20 : 14;
        glyph.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(glyph);
        var body = Inline(text, 13);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    private static Control Table(List<string> rows)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var cells = rows.Select(Cells).ToList();
        var columns = cells.Max(c => c.Length);
        var grid = new Grid();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < cells.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var cell = Inline(c < cells[r].Length ? cells[r][c] : "", 12);
                cell.TextWrapping = TextWrapping.NoWrap;
                if (r == 0) cell.FontWeight = FontWeight.SemiBold;
                var border = new Border
                {
                    Padding = new Thickness(10, 5), Child = cell,
                    Background = r == 0 ? Res.Brush("Bg3Brush") : r % 2 == 0 ? Res.Brush("Bg2Brush") : null,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return new Border
        {
            CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = Res.Brush("StrokeBrush"), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = grid },
        };
    }

    private static Control CodeBlock(string language, string code, Func<string, Task> copy)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 4, 4, 0) };
        header.Children.Add(new TextBlock { Text = language.Length > 0 ? language : "code", Classes = { "faint", "mono" }, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Classes = { "icon" }, Width = 22, Height = 22, Content = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconCopy"), Height = 10 } };
        ToolTip.SetTip(button, "Copy code");
        button.Click += async (_, _) => await copy(code);
        Grid.SetColumn(button, 1);
        header.Children.Add(button);
        var body = new SelectableTextBlock
        {
            Text = code, Classes = { "mono" }, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 2, 10, 10), SelectionBrush = Res.Brush("AccentSoftBrush"),
        };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);
        return new Border { Classes = { "codeblock" }, Child = stack };
    }

    // ---------------------------------------------------------------- inline

    [Flags]
    private enum Style { None = 0, Bold = 1, Italic = 2, Strike = 4 }

    /// <summary>A text block whose text carries inline Markdown (**bold**, *italic*, ~~strike~~, `code`, [link](url)).</summary>
    public static SelectableTextBlock Inline(string text, double size)
    {
        var block = new SelectableTextBlock { Classes = { "msg" }, FontSize = size, Inlines = new InlineCollection() };
        var plain = new StringBuilder();
        var style = Style.None;
        var length = 0;
        var links = new List<(int Start, int End, string Url)>();

        void Flush()
        {
            if (plain.Length == 0) return;
            var run = new Run(plain.ToString());
            if (style.HasFlag(Style.Bold)) run.FontWeight = FontWeight.SemiBold;
            if (style.HasFlag(Style.Italic)) run.FontStyle = FontStyle.Italic;
            if (style.HasFlag(Style.Strike)) run.TextDecorations = TextDecorations.Strikethrough;
            block.Inlines!.Add(run);
            length += plain.Length;
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\' && i + 1 < text.Length && "*_`~[]()\\#".Contains(text[i + 1]))
            {
                plain.Append(text[++i]);
                continue;
            }
            if (ch == '`' && text.IndexOf('`', i + 1) is var end and > 0)
            {
                Flush();
                length += end - i - 1;
                block.Inlines!.Add(new Run(text[(i + 1)..end])
                {
                    FontFamily = Res.Mono, FontSize = size - 1, Background = Res.Brush("Bg3Brush"), Foreground = Res.Brush("AccentBrush"),
                });
                i = end;
                continue;
            }
            if ((ch == '*' || ch == '_') && i + 1 < text.Length && text[i + 1] == ch && (style.HasFlag(Style.Bold) || Closes(text, i + 2, new string(ch, 2))))
            {
                Flush();
                style ^= Style.Bold;
                i++;
                continue;
            }
            if (ch == '~' && i + 1 < text.Length && text[i + 1] == '~' && (style.HasFlag(Style.Strike) || Closes(text, i + 2, "~~")))
            {
                Flush();
                style ^= Style.Strike;
                i++;
                continue;
            }
            // Single * or _ toggles italic, but not inside words (snake_case, 2*3).
            if ((ch == '*' || ch == '_') && (style.HasFlag(Style.Italic) ? i > 0 && !char.IsWhiteSpace(text[i - 1]) : i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && Closes(text, i + 1, ch.ToString())))
            {
                Flush();
                style ^= Style.Italic;
                continue;
            }
            if (ch == '[' && Link(text, i) is { } link)
            {
                Flush();
                links.Add((length, length + link.Label.Length, link.Url));
                length += link.Label.Length;
                block.Inlines!.Add(new Run(link.Label) { Foreground = Res.Brush("AccentBrush"), TextDecorations = TextDecorations.Underline });
                i = link.End;
                continue;
            }
            plain.Append(ch);
        }
        Flush();
        if (links.Count > 0) MakeLinksClickable(block, links);
        return block;
    }

    private static bool Closes(string text, int from, string marker) => from < text.Length && text.IndexOf(marker, from, StringComparison.Ordinal) > from;

    private static (string Label, string Url, int End)? Link(string text, int start)
    {
        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return null;
        var end = text.IndexOf(')', close + 2);
        if (end < 0) return null;
        var url = text[(close + 2)..end].Trim();
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? (text[(start + 1)..close], url, end)
            : null;
    }

    /// <summary>
    /// Links are runs (so they sit on the text's baseline and can be selected); a click that lands on one, without
    /// selecting text, opens it.
    /// </summary>
    private static void MakeLinksClickable(SelectableTextBlock block, List<(int Start, int End, string Url)> links)
    {
        string? UrlAt(PointerEventArgs e)
        {
            var hit = block.TextLayout.HitTestPoint(e.GetPosition(block) - new Point(block.Padding.Left, block.Padding.Top));
            if (!hit.IsInside) return null;
            var index = hit.TextPosition;
            return links.FirstOrDefault(l => index >= l.Start && index < l.End).Url;
        }
        block.PointerMoved += (_, e) => block.Cursor = UrlAt(e) is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        block.PointerReleased += async (_, e) =>
        {
            if (block.SelectionStart != block.SelectionEnd || UrlAt(e) is not { } url) return;
            if (TopLevel.GetTopLevel(block)?.Launcher is { } launcher && Uri.TryCreate(url, UriKind.Absolute, out var uri)) await launcher.LaunchUriAsync(uri);
        };
    }
}
