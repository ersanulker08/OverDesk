using System.Text;
using System.Text.Json.Serialization;

namespace Overdesk.Core.Works;

public enum BlockKind
{
    /// <summary>A title line (Level 1-3).</summary>
    Heading,
    /// <summary>Free multi-line writing.</summary>
    Text,
    /// <summary>A checkbox with a label.</summary>
    Todo,
    /// <summary>A named true/false variable ("hasKey: on").</summary>
    Toggle,
    /// <summary>A named text value ("Name: Aria").</summary>
    Field,
    /// <summary>A named number ("Gold: 120").</summary>
    Number,
    Bullet,
    Quote,
    Divider,
}

/// <summary>One row of a work: text, a to-do, a variable...</summary>
public sealed class WorkBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BlockKind Kind { get; set; } = BlockKind.Text;

    /// <summary>Content, or the label/name of a variable.</summary>
    public string Text { get; set; } = "";

    /// <summary>Field blocks: the value.</summary>
    public string Value { get; set; } = "";

    /// <summary>Todo and Toggle blocks.</summary>
    public bool Checked { get; set; }

    /// <summary>Number blocks.</summary>
    public double Number { get; set; }

    /// <summary>Heading blocks: 1 (big) to 3 (small).</summary>
    public int Level { get; set; } = 1;

    public WorkBlock Clone() => new()
    {
        Kind = Kind, Text = Text, Value = Value, Checked = Checked, Number = Number, Level = Level,
    };
}

/// <summary>A note, list, story, sheet... anything the user writes. Saved as one JSON file.</summary>
public sealed class Work
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled";

    /// <summary>Id of the template it was made from (see <see cref="WorkTemplates"/>).</summary>
    public string Template { get; set; } = "blank";

    /// <summary>Name of an accent theme; colors the card and the page.</summary>
    public string Color { get; set; } = "Crimson";

    /// <summary>Name of a built-in icon (Icons.axaml).</summary>
    public string Icon { get; set; } = "IconNote";

    /// <summary>Font family names from the font catalog; null follows the default chosen in Settings.</summary>
    public string? TitleFont { get; set; }
    public string? BodyFont { get; set; }

    /// <summary>Text size in px; 0 follows Settings.</summary>
    public double FontSize { get; set; }
    public double LineSpacing { get; set; } = 1.5;

    /// <summary>Page width in px (the writing column).</summary>
    public double PageWidth { get; set; } = 720;

    public bool Pinned { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public List<WorkBlock> Blocks { get; set; } = new();

    [JsonIgnore] public int TodoTotal => Blocks.Count(b => b.Kind == BlockKind.Todo);
    [JsonIgnore] public int TodoDone => Blocks.Count(b => b is { Kind: BlockKind.Todo, Checked: true });

    /// <summary>A short line for cards: the first real text of the work.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            foreach (var b in Blocks)
            {
                var text = b.Kind switch
                {
                    BlockKind.Field => b.Value.Length > 0 ? $"{b.Text}: {b.Value}" : "",
                    BlockKind.Toggle or BlockKind.Number or BlockKind.Divider or BlockKind.Heading => "",
                    _ => b.Text,
                };
                text = text.Replace('\n', ' ').Trim();
                if (text.Length > 0) return text.Length > 110 ? text[..110] + "…" : text;
            }
            return "";
        }
    }

    public Work Duplicate() => new()
    {
        Title = Title + " copy", Template = Template, Color = Color, Icon = Icon, TitleFont = TitleFont, BodyFont = BodyFont,
        FontSize = FontSize, LineSpacing = LineSpacing, PageWidth = PageWidth,
        Blocks = Blocks.Select(b => b.Clone()).ToList(),
    };

    public string ToMarkdown()
    {
        var sb = new StringBuilder().Append("# ").AppendLine(Title).AppendLine();
        foreach (var b in Blocks)
        {
            switch (b.Kind)
            {
                case BlockKind.Heading: sb.AppendLine().Append(new string('#', Math.Clamp(b.Level, 1, 3) + 1)).Append(' ').AppendLine(b.Text); break;
                case BlockKind.Text: sb.AppendLine(b.Text).AppendLine(); break;
                case BlockKind.Todo: sb.Append(b.Checked ? "- [x] " : "- [ ] ").AppendLine(b.Text); break;
                case BlockKind.Toggle: sb.Append("- **").Append(b.Text).Append("**: ").AppendLine(b.Checked ? "yes" : "no"); break;
                case BlockKind.Field: sb.Append("- **").Append(b.Text).Append("**: ").AppendLine(b.Value); break;
                case BlockKind.Number: sb.Append("- **").Append(b.Text).Append("**: ").AppendLine(b.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                case BlockKind.Bullet: sb.Append("- ").AppendLine(b.Text); break;
                case BlockKind.Quote: sb.Append("> ").AppendLine(b.Text.Replace("\n", "\n> ")); break;
                case BlockKind.Divider: sb.AppendLine("---"); break;
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}
