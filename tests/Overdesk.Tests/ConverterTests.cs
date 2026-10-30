using System.Text;
using System.Text.Json.Nodes;
using Overdesk.Core.Text;
using Overdesk.Tool.Converter;
using SkiaSharp;

namespace Overdesk.Tests;

public class ConverterTests
{
    [Fact]
    public void Csv_becomes_typed_json_and_back()
    {
        const string csv = "name,age,zip,active,note\nAria,31,02134,true,\"likes \"\"tea\"\", coffee\"\nBo,,10001,false,\"two\nlines\"\n";
        var json = JsonNode.Parse(DataFormats.Convert(csv, "csv", "json"))!.AsArray();
        Assert.Equal(2, json.Count);
        Assert.Equal(31, json[0]!["age"]!.GetValue<long>());
        Assert.Equal("02134", json[0]!["zip"]!.GetValue<string>());
        Assert.True(json[0]!["active"]!.GetValue<bool>());
        Assert.Equal("likes \"tea\", coffee", json[0]!["note"]!.GetValue<string>());
        Assert.Null(json[1]!["age"]);
        Assert.Equal("two\nlines", json[1]!["note"]!.GetValue<string>());

        var back = DataFormats.ParseDelimited(DataFormats.Convert(json.ToJsonString(), "json", "csv"), ',');
        Assert.Equal(new[] { "name", "age", "zip", "active", "note" }, back[0]);
        Assert.Equal("likes \"tea\", coffee", back[1][4]);
    }

    [Fact]
    public void Json_with_one_list_inside_an_object_becomes_a_table()
    {
        var csv = DataFormats.Convert("""{"users":[{"id":1,"tags":["a","b"]},{"id":2,"extra":"x"}]}""", "json", "csv");
        var rows = DataFormats.ParseDelimited(csv, ',');
        Assert.Equal(new[] { "id", "tags", "extra" }, rows[0]);
        Assert.Equal("[\"a\",\"b\"]", rows[1][1]);
        Assert.Equal("x", rows[2][2]);
    }

    [Fact]
    public void Xml_and_json_convert_both_ways()
    {
        var json = JsonNode.Parse(DataFormats.Convert("<shop name=\"Corner\"><item>apple</item><item>pear</item><open>true</open></shop>", "xml", "json"))!;
        Assert.Equal("Corner", json["@name"]!.GetValue<string>());
        Assert.Equal(2, json["item"]!.AsArray().Count);
        Assert.True(json["open"]!.GetValue<bool>());

        var xml = DataFormats.Convert("""{"shop":{"@name":"Corner","item":["apple","pear"],"my key":1}}""", "json", "xml");
        Assert.Contains("<shop name=\"Corner\">", xml);
        Assert.Contains("<item>pear</item>", xml);
        Assert.Contains("<my_x0020_key>1</my_x0020_key>", xml);
    }

    [Fact]
    public void Markdown_becomes_html()
    {
        var html = Markdown.ToHtml("# Title\n\nSome **bold** and *italic* and `a<b`.\n\n- [x] done\n- todo\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```cs\nvar x = 1 < 2;\n```\n[site](https://example.com)");
        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<em>italic</em>", html);
        Assert.Contains("<code>a&lt;b</code>", html);
        Assert.Contains("&#9745; done", html);
        Assert.Contains("<th>A</th>", html);
        Assert.Contains("<code class=\"language-cs\">var x = 1 &lt; 2;</code>", html);
        Assert.Contains("<a href=\"https://example.com\">site</a>", html);
    }

    [Fact]
    public void Html_becomes_plain_text()
    {
        Assert.Equal("Hello\nworld & co\n", Markdown.HtmlToText("<html><style>p{}</style><p>Hello</p><div>world &amp; co</div></html>"));
    }

    private static string MakePng(TempFolder dir, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Transparent);
        var path = Path.Combine(dir.Path, "pic.png");
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("webp")]
    public async Task Images_convert_and_shrink(string target)
    {
        using var dir = new TempFolder();
        var png = MakePng(dir, 400, 200);
        var output = await Engine.ConvertAsync(png, target, new ConvertOptions { MaxSize = 100 }, null, CancellationToken.None);
        Assert.EndsWith("." + target, output);
        using var codec = SKCodec.Create(output);
        Assert.Equal(100, codec.Info.Width);
        Assert.Equal(50, codec.Info.Height);
    }

    [Fact]
    public async Task Results_never_overwrite_existing_files()
    {
        using var dir = new TempFolder();
        var input = dir.Write("data.json", "[{\"a\":1}]");
        dir.Write("data.csv", "keep me");
        var output = await Engine.ConvertAsync(input, "csv", new ConvertOptions(), null, CancellationToken.None);
        Assert.Equal(Path.Combine(dir.Path, "data (2).csv"), output);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(dir.Path, "data.csv")));
    }

    [Fact]
    public async Task Unsupported_targets_are_refused()
    {
        using var dir = new TempFolder();
        var input = dir.Write("data.json", "[]");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine.ConvertAsync(input, "mp3", new ConvertOptions(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Markdown_becomes_a_word_document_on_macos()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dir = new TempFolder();
        var input = dir.Write("notes.md", "# Plan\n\n- one\n- two\n");
        var output = await Engine.ConvertAsync(input, "docx", new ConvertOptions(), null, CancellationToken.None);
        Assert.True(new FileInfo(output).Length > 1000);
        var text = await Engine.ConvertAsync(output, "txt", new ConvertOptions(), null, CancellationToken.None);
        Assert.Contains("Plan", File.ReadAllText(text));
    }

    [Fact]
    public async Task Audio_converts_with_the_tools_on_the_computer()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dir = new TempFolder();
        var wav = Path.Combine(dir.Path, "tone.wav");
        File.WriteAllBytes(wav, Wave(seconds: 1));
        var output = await Engine.ConvertAsync(wav, "m4a", new ConvertOptions(), null, CancellationToken.None);
        Assert.True(new FileInfo(output).Length > 500);
    }

    private static int PdfPages(string path) => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path, Encoding.Latin1), @"/Type\s*/Page\b").Count;

    [Fact]
    public async Task Markdown_becomes_a_pdf_with_page_breaks()
    {
        using var dir = new TempFolder();
        var long_ = string.Join("\n\n", Enumerable.Range(1, 80).Select(i => $"Paragraph {i}: some **bold** words and a [link](https://example.com) that wrap across the line width of an A4 page."));
        var input = dir.Write("notes.md", $"# Plan\n\n- [x] done\n- open\n\n> a quote\n\n```\nvar x = 1;\n```\n\n---\n\n{long_}");
        var output = await Engine.ConvertAsync(input, "pdf", new ConvertOptions(), null, CancellationToken.None);
        Assert.StartsWith("%PDF", File.ReadAllText(output, Encoding.Latin1));
        Assert.True(PdfPages(output) >= 2);
    }

    [Fact]
    public async Task Images_become_one_page_pdfs()
    {
        using var dir = new TempFolder();
        var png = MakePng(dir, 300, 200);
        var output = await Engine.ConvertAsync(png, "pdf", new ConvertOptions(), null, CancellationToken.None);
        Assert.Equal(1, PdfPages(output));
    }

    /// <summary>A 16-bit mono 22 kHz sine wave as a WAV file.</summary>
    private static byte[] Wave(int seconds)
    {
        const int rate = 22050;
        var samples = rate * seconds;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
        for (var i = 0; i < samples; i++) w.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 8000));
        return ms.ToArray();
    }
}
