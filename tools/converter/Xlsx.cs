using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Overdesk.Tool.Converter;

/// <summary>
/// Excel workbooks without a library: a table (header row + rows) written as a minimal .xlsx (one sheet, bold header,
/// frozen first row, numbers and true/false as real cell types), and the first sheet of any .xlsx read back as rows.
/// </summary>
public static class Xlsx
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    // ---------------------------------------------------------------- writing

    public static void Write(string path, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Entry(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);
        Entry(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        Entry(zip, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        Entry(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
        // Style 0: normal, style 1: bold (the header row).
        Entry(zip, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
              <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
              <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs>
            </styleSheet>
            """);

        var sheet = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sheet.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        sheet.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        var widths = Enumerable.Range(0, rows.Count == 0 ? 0 : rows.Max(r => r.Count))
            .Select(c => Math.Clamp(rows.Take(200).Max(r => c < r.Count ? r[c].Length : 0) + 2, 6, 60)).ToList();
        if (widths.Count > 0)
            sheet.Append("<cols>").Append(string.Concat(widths.Select((w, i) => $"""<col min="{i + 1}" max="{i + 1}" width="{w}" customWidth="1"/>"""))).Append("</cols>");
        sheet.Append("<sheetData>");
        for (var r = 0; r < rows.Count; r++)
        {
            sheet.Append($"""<row r="{r + 1}">""");
            for (var c = 0; c < rows[r].Count; c++)
            {
                var value = rows[r][c];
                if (value.Length == 0) continue;
                var reference = Column(c) + (r + 1);
                var style = r == 0 ? " s=\"1\"" : "";
                if (r > 0 && IsNumber(value))
                    sheet.Append($"""<c r="{reference}"{style}><v>{value}</v></c>""");
                else if (r > 0 && value is "true" or "false")
                    sheet.Append($"""<c r="{reference}"{style} t="b"><v>{(value == "true" ? 1 : 0)}</v></c>""");
                else
                    sheet.Append($"""<c r="{reference}"{style} t="inlineStr"><is><t xml:space="preserve">{SecurityElement.Escape(Clean(value))}</t></is></c>""");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");
        Entry(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
    }

    /// <summary>Plain numbers only: leading zeros (zip codes, ids) and very long digit runs stay text.</summary>
    private static bool IsNumber(string value)
        => value.Length <= 15 && !(value.Length > 1 && value[0] == '0' && value[1] != '.')
           && double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);

    /// <summary>XML 1.0 cannot hold most control characters.</summary>
    private static string Clean(string text) => new(text.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray());

    private static string Column(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content.Trim());
    }

    // ---------------------------------------------------------------- reading

    /// <summary>The first sheet as rows of text (numbers in invariant form, booleans as true/false, empty cells as "").</summary>
    public static List<List<string>> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument Load(string name) => XDocument.Load(zip.GetEntry(name)?.Open() ?? throw new InvalidDataException($"The workbook has no {name}."));

        var shared = zip.GetEntry("xl/sharedStrings.xml") is { } entry
            ? XDocument.Load(entry.Open()).Root!.Elements(Main + "si").Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList()
            : new List<string>();

        // The first sheet listed in the workbook, through its relationship.
        var firstSheet = Load("xl/workbook.xml").Root!.Element(Main + "sheets")?.Elements(Main + "sheet").FirstOrDefault()
                         ?? throw new InvalidDataException("The workbook has no sheets.");
        var id = (string?)firstSheet.Attribute(Rel + "id");
        var target = Load("xl/_rels/workbook.xml.rels").Root!.Elements(PackageRel + "Relationship")
            .FirstOrDefault(r => (string?)r.Attribute("Id") == id)?.Attribute("Target")?.Value ?? "worksheets/sheet1.xml";
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

        var rows = new List<List<string>>();
        foreach (var row in Load(sheetPath).Descendants(Main + "row"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements(Main + "c"))
            {
                var column = ColumnIndex((string?)cell.Attribute("r") ?? Column(cells.Count) + "1");
                while (cells.Count < column) cells.Add("");
                var type = (string?)cell.Attribute("t");
                var value = cell.Element(Main + "v")?.Value ?? "";
                cells.Add(type switch
                {
                    "s" when int.TryParse(value, out var index) && index < shared.Count => shared[index],
                    "inlineStr" => string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)),
                    "b" => value == "1" ? "true" : "false",
                    _ => value,
                });
            }
            var number = int.TryParse((string?)row.Attribute("r"), out var n) ? n : rows.Count + 1;
            while (rows.Count < number - 1) rows.Add(new List<string>());
            rows.Add(cells);
        }
        while (rows.Count > 0 && rows[^1].All(c => c.Length == 0)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference.TakeWhile(char.IsLetter)) index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        return index - 1;
    }
}
