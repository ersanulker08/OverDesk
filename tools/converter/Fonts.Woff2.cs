using System.Buffers.Binary;
using System.IO.Compression;

namespace Overdesk.Tool.Converter;

/// <summary>
/// WOFF2 (W3C spec) back into the TrueType / OpenType font it was made from: Brotli-compressed table stream, transformed
/// glyf + loca (streams and triplet coordinates) and transformed hmtx rebuilt, checksums recomputed. Collections are refused.
/// </summary>
public static partial class Fonts
{
    private const uint GlyfTag = 0x676C7966;
    private const uint LocaTag = 0x6C6F6361;
    private const uint HmtxTag = 0x686D7478;
    private const uint HheaTag = 0x68686561;
    private const uint MaxpTag = 0x6D617870;
    private const uint HeadTag = 0x68656164;

    private static readonly string[] KnownTags =
    {
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    };

    private sealed class Woff2Table
    {
        public uint Tag;
        public int Transform;
        public bool Transformed;
        public int OrigLength;
        public int StreamLength;
        public byte[] Data = Array.Empty<byte>();
    }

    public static byte[] FromWoff2(byte[] woff)
    {
        if (woff.Length < 48 || BinaryPrimitives.ReadUInt32BigEndian(woff) != 0x774F4632) throw new InvalidOperationException("Not a WOFF2 font.");
        var flavor = BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(4));
        if (flavor == 0x74746366) throw new InvalidOperationException("WOFF2 font collections (TTC) are not supported.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(woff.AsSpan(12));
        var compressedSize = BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(20));
        if (count == 0) throw new InvalidOperationException("The WOFF2 font is damaged (no tables).");

        var at = 48;
        var tables = new List<Woff2Table>();
        for (var i = 0; i < count; i++)
        {
            if (at >= woff.Length) throw Damaged("the table directory is cut off");
            var flags = woff[at++];
            var tagIndex = flags & 0x3F;
            uint tag;
            if (tagIndex == 63)
            {
                if (at + 4 > woff.Length) throw Damaged("the table directory is cut off");
                tag = BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(at));
                at += 4;
            }
            else tag = BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(KnownTags[tagIndex]));
            var table = new Woff2Table { Tag = tag, Transform = flags >> 6 };
            table.OrigLength = (int)ReadUIntBase128(woff, ref at);
            // For glyf and loca, version 0 is the transform and 3 the null transform; every other table is the opposite.
            table.Transformed = tag is GlyfTag or LocaTag ? table.Transform == 0 : table.Transform != 0;
            table.StreamLength = table.Transformed ? (int)ReadUIntBase128(woff, ref at) : table.OrigLength;
            tables.Add(table);
        }
        foreach (var t in tables)
            if (t.Transformed && t.Tag is not (GlyfTag or LocaTag or HmtxTag))
                throw new InvalidOperationException($"WOFF2 transform {t.Transform} of the '{TagName(t.Tag)}' table is not supported.");

        if ((long)at + compressedSize > woff.Length) throw Damaged("the compressed data runs past the end");
        var total = tables.Sum(t => (long)t.StreamLength);
        if (total > int.MaxValue) throw Damaged("the tables are too large");
        var data = new byte[total];
        try
        {
            using var brotli = new BrotliStream(new MemoryStream(woff, at, (int)compressedSize), CompressionMode.Decompress);
            brotli.ReadExactly(data);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            throw Damaged("the compressed data cannot be unpacked");
        }
        var offset = 0;
        foreach (var t in tables)
        {
            t.Data = data.AsSpan(offset, t.StreamLength).ToArray();
            offset += t.StreamLength;
        }

        var glyf = tables.FirstOrDefault(t => t.Tag == GlyfTag);
        var loca = tables.FirstOrDefault(t => t.Tag == LocaTag);
        if (glyf?.Transformed == true || loca?.Transformed == true)
        {
            if (glyf is null || loca is null || !glyf.Transformed || !loca.Transformed) throw Damaged("glyf and loca must be transformed together");
            (glyf.Data, loca.Data) = ReconstructGlyf(glyf.Data);
            if (loca.Data.Length != loca.OrigLength) throw Damaged("the rebuilt loca table has the wrong size");
        }
        var hmtx = tables.FirstOrDefault(t => t.Tag == HmtxTag);
        if (hmtx?.Transformed == true)
        {
            if (hmtx.Transform != 1) throw new InvalidOperationException($"WOFF2 transform {hmtx.Transform} of the 'hmtx' table is not supported.");
            var hhea = tables.FirstOrDefault(t => t.Tag == HheaTag)?.Data;
            var maxp = tables.FirstOrDefault(t => t.Tag == MaxpTag)?.Data;
            if (hhea is null || hhea.Length < 36 || maxp is null || maxp.Length < 6 || glyf is null || loca is null) throw Damaged("the hmtx table cannot be rebuilt");
            hmtx.Data = ReconstructHmtx(hmtx.Data, BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34)), BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4)), glyf.Data, loca.Data);
            if (hmtx.Data.Length != hmtx.OrigLength) throw Damaged("the rebuilt hmtx table has the wrong size");
        }
        return BuildSfnt(flavor, tables.Select(t => (t.Tag, t.Data)).ToList());
    }

    private static InvalidOperationException Damaged(string what) => new($"The WOFF2 font is damaged ({what}).");

    private static string TagName(uint tag) => System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(tag)));

    private static uint ReadUIntBase128(byte[] data, ref int at)
    {
        uint result = 0;
        for (var i = 0; i < 5; i++)
        {
            if (at >= data.Length) throw Damaged("the table directory is cut off");
            var b = data[at++];
            if (i == 0 && b == 0x80) throw Damaged("a length has a leading zero");
            if ((result & 0xFE000000) != 0) throw Damaged("a length overflows");
            result = (result << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return result;
        }
        throw Damaged("a length is longer than 5 bytes");
    }

    private sealed class Cursor
    {
        private readonly byte[] _data;
        private readonly int _end;
        public int Position;

        public Cursor(byte[] data, int start, int length)
        {
            _data = data;
            Position = start;
            _end = start + length;
            if (_end > data.Length) throw Damaged("a glyph stream runs past the end");
        }

        public byte Byte()
        {
            if (Position >= _end) throw Damaged("a glyph stream is cut off");
            return _data[Position++];
        }

        public ushort U16() => (ushort)((Byte() << 8) | Byte());
        public short I16() => (short)U16();

        public int U255()
        {
            var code = Byte();
            return code switch
            {
                253 => U16(),
                255 => Byte() + 253,
                254 => Byte() + 253 * 2,
                _ => code,
            };
        }

        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (count < 0 || Position + count > _end) throw Damaged("a glyph stream is cut off");
            var span = _data.AsSpan(Position, count);
            Position += count;
            return span;
        }
    }

    private static (byte[] Glyf, byte[] Loca) ReconstructGlyf(byte[] t)
    {
        if (t.Length < 36) throw Damaged("the glyf table is cut off");
        var optionFlags = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(2));
        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(4));
        int indexFormat = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(6));
        var sizes = new int[7];
        for (var i = 0; i < 7; i++)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan(8 + 4 * i));
            if (size > int.MaxValue) throw Damaged("a glyph stream is too large");
            sizes[i] = (int)size;
        }
        var at = 36;
        Cursor Next(int size) { var c = new Cursor(t, at, size); at += size; return c; }
        var contours = Next(sizes[0]);
        var points = Next(sizes[1]);
        var flags = Next(sizes[2]);
        var glyphs = Next(sizes[3]);
        var composites = Next(sizes[4]);
        var bitmapLength = 4 * ((numGlyphs + 31) / 32);
        if (sizes[5] < bitmapLength) throw Damaged("the bounding box stream is cut off");
        var bboxBitmap = t.AsSpan(at, bitmapLength).ToArray();
        var bboxes = new Cursor(t, at + bitmapLength, sizes[5] - bitmapLength);
        at += sizes[5];
        var instructions = Next(sizes[6]);
        byte[]? overlapBitmap = null;
        if ((optionFlags & 1) != 0)
        {
            var length = (numGlyphs + 7) / 8;
            if (at + length > t.Length) throw Damaged("the overlap bitmap is cut off");
            overlapBitmap = t.AsSpan(at, length).ToArray();
        }

        var glyf = new MemoryStream();
        var offsets = new uint[numGlyphs + 1];
        void Write16(int value) => Write16To(glyf, value);
        for (var g = 0; g < numGlyphs; g++)
        {
            offsets[g] = (uint)glyf.Length;
            var nContours = contours.I16();
            if (nContours == 0) continue;
            var hasBbox = (bboxBitmap[g >> 3] & (0x80 >> (g & 7))) != 0;
            Write16(nContours);
            if (nContours < 0)
            {
                if (!hasBbox) throw Damaged("a composite glyph has no bounding box");
                glyf.Write(bboxes.Bytes(8));
                var start = composites.Position;
                var haveInstructions = false;
                while (true)
                {
                    var componentFlags = composites.U16();
                    composites.U16();
                    composites.Bytes((componentFlags & 0x0001) != 0 ? 4 : 2);
                    if ((componentFlags & 0x0008) != 0) composites.Bytes(2);
                    else if ((componentFlags & 0x0040) != 0) composites.Bytes(4);
                    else if ((componentFlags & 0x0080) != 0) composites.Bytes(8);
                    haveInstructions |= (componentFlags & 0x0100) != 0;
                    if ((componentFlags & 0x0020) == 0) break;
                }
                glyf.Write(t.AsSpan(start, composites.Position - start));
                if (haveInstructions)
                {
                    var length = glyphs.U255();
                    Write16(length);
                    glyf.Write(instructions.Bytes(length));
                }
            }
            else
            {
                var endPoints = new int[nContours];
                var total = 0;
                for (var c = 0; c < nContours; c++)
                {
                    total += points.U255();
                    endPoints[c] = total - 1;
                }
                var xs = new int[total];
                var ys = new int[total];
                var onCurve = new bool[total];
                int x = 0, y = 0;
                for (var p = 0; p < total; p++)
                {
                    var flag = flags.Byte();
                    onCurve[p] = (flag & 0x80) == 0;
                    var (dx, dy) = Triplet(flag & 0x7F, glyphs);
                    x += dx;
                    y += dy;
                    xs[p] = x;
                    ys[p] = y;
                }
                var instructionLength = glyphs.U255();
                var code = instructions.Bytes(instructionLength).ToArray();
                if (hasBbox) glyf.Write(bboxes.Bytes(8));
                else
                {
                    int xMin = 0, yMin = 0, xMax = 0, yMax = 0;
                    if (total > 0) { xMin = xs.Min(); yMin = ys.Min(); xMax = xs.Max(); yMax = ys.Max(); }
                    Write16(xMin); Write16(yMin); Write16(xMax); Write16(yMax);
                }
                foreach (var end in endPoints) Write16(end);
                Write16(instructionLength);
                glyf.Write(code);
                var overlap = overlapBitmap is not null && (overlapBitmap[g >> 3] & (0x80 >> (g & 7))) != 0;
                var xData = new MemoryStream();
                var yData = new MemoryStream();
                int px = 0, py = 0;
                for (var p = 0; p < total; p++)
                {
                    var flag = onCurve[p] ? 0x01 : 0x00;
                    if (p == 0 && overlap) flag |= 0x40;
                    var dx = xs[p] - px;
                    var dy = ys[p] - py;
                    px = xs[p];
                    py = ys[p];
                    if (dx == 0) flag |= 0x10;
                    else if (dx is > -256 and < 256) { flag |= 0x02; if (dx > 0) flag |= 0x10; xData.WriteByte((byte)Math.Abs(dx)); }
                    else Write16To(xData, dx);
                    if (dy == 0) flag |= 0x20;
                    else if (dy is > -256 and < 256) { flag |= 0x04; if (dy > 0) flag |= 0x20; yData.WriteByte((byte)Math.Abs(dy)); }
                    else Write16To(yData, dy);
                    glyf.WriteByte((byte)flag);
                }
                xData.WriteTo(glyf);
                yData.WriteTo(glyf);
            }
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
        }
        offsets[numGlyphs] = (uint)glyf.Length;

        var locaData = new byte[(numGlyphs + 1) * (indexFormat == 0 ? 2 : 4)];
        for (var i = 0; i <= numGlyphs; i++)
        {
            if (indexFormat == 0)
            {
                if (offsets[i] / 2 > ushort.MaxValue) throw Damaged("the glyf table is too large for short loca offsets");
                BinaryPrimitives.WriteUInt16BigEndian(locaData.AsSpan(i * 2), (ushort)(offsets[i] / 2));
            }
            else BinaryPrimitives.WriteUInt32BigEndian(locaData.AsSpan(i * 4), offsets[i]);
        }
        return (glyf.ToArray(), locaData);
    }

    private static void Write16To(MemoryStream stream, int value)
    {
        Span<byte> word = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(word, (short)value);
        stream.Write(word);
    }

    /// <summary>One point's delta from the triplet-encoded glyph stream; the flag's low 7 bits pick the layout.</summary>
    private static (int Dx, int Dy) Triplet(int index, Cursor glyphs)
    {
        static int Sign(int bit, int value) => (bit & 1) != 0 ? value : -value;
        if (index < 10)
            return (0, Sign(index, ((index & 14) << 7) + glyphs.Byte()));
        if (index < 20)
            return (Sign(index, (((index - 10) & 14) << 7) + glyphs.Byte()), 0);
        if (index < 84)
        {
            var b0 = index - 20;
            var b1 = glyphs.Byte();
            return (Sign(b0, 1 + (b0 & 0x30) + (b1 >> 4)), Sign(b0 >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F)));
        }
        if (index < 120)
        {
            var b0 = index - 84;
            var b1 = glyphs.Byte();
            var b2 = glyphs.Byte();
            return (Sign(b0, 1 + ((b0 / 12) << 8) + b1), Sign(b0 >> 1, 1 + (((b0 % 12) >> 2) << 8) + b2));
        }
        if (index < 124)
        {
            var b1 = glyphs.Byte();
            var b2 = glyphs.Byte();
            var b3 = glyphs.Byte();
            return (Sign(index, (b1 << 4) + (b2 >> 4)), Sign(index >> 1, ((b2 & 0x0F) << 8) + b3));
        }
        var hi = glyphs.U16();
        var lo = glyphs.U16();
        return (Sign(index, hi), Sign(index >> 1, lo));
    }

    private static byte[] ReconstructHmtx(byte[] t, int numberOfHMetrics, int numGlyphs, byte[] glyf, byte[] loca)
    {
        if (t.Length < 1 || numberOfHMetrics > numGlyphs) throw Damaged("the hmtx table is cut off");
        var flags = t[0];
        var cursor = new Cursor(t, 1, t.Length - 1);
        var advances = new ushort[numberOfHMetrics];
        for (var i = 0; i < numberOfHMetrics; i++) advances[i] = cursor.U16();
        var bearings = new short[numGlyphs];
        var longLoca = loca.Length == (numGlyphs + 1) * 4;
        if (!longLoca && loca.Length != (numGlyphs + 1) * 2) throw Damaged("the loca table does not match the glyph count");
        short XMin(int g)
        {
            var start = longLoca ? BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4)) : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(g * 2)) * 2u;
            var end = longLoca ? BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4 + 4)) : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(g * 2 + 2)) * 2u;
            return end - start >= 10 && end <= glyf.Length ? BinaryPrimitives.ReadInt16BigEndian(glyf.AsSpan((int)start + 2)) : (short)0;
        }
        for (var i = 0; i < numberOfHMetrics; i++) bearings[i] = (flags & 1) == 0 ? cursor.I16() : XMin(i);
        for (var i = numberOfHMetrics; i < numGlyphs; i++) bearings[i] = (flags & 2) == 0 ? cursor.I16() : XMin(i);

        var result = new byte[numberOfHMetrics * 4 + (numGlyphs - numberOfHMetrics) * 2];
        var at = 0;
        for (var i = 0; i < numGlyphs; i++)
        {
            if (i < numberOfHMetrics)
            {
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(at), advances[i]);
                at += 2;
            }
            BinaryPrimitives.WriteInt16BigEndian(result.AsSpan(at), bearings[i]);
            at += 2;
        }
        return result;
    }

    /// <summary>A plain sfnt from tables, with fresh table checksums and head's checkSumAdjustment.</summary>
    private static byte[] BuildSfnt(uint flavor, List<(uint Tag, byte[] Data)> tables)
    {
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        var count = tables.Count;
        var size = 12 + 16 * count + tables.Sum(t => Pad(t.Data.Length));
        var sfnt = new byte[size];
        var span = sfnt.AsSpan();
        var power = 1;
        var log = 0;
        while (power * 2 <= count) { power *= 2; log++; }
        BinaryPrimitives.WriteUInt32BigEndian(span, flavor);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)(power * 16));
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(count * 16 - power * 16));
        var at = 12 + 16 * count;
        var headAt = -1;
        for (var i = 0; i < count; i++)
        {
            var (tag, data) = tables[i];
            data.CopyTo(span[at..]);
            if (tag == HeadTag && data.Length >= 12)
            {
                headAt = at;
                span.Slice(at + 8, 4).Clear();
            }
            var record = span[(12 + i * 16)..];
            BinaryPrimitives.WriteUInt32BigEndian(record, tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(span.Slice(at, Pad(data.Length))));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
            at += Pad(data.Length);
        }
        if (headAt >= 0) BinaryPrimitives.WriteUInt32BigEndian(span[(headAt + 8)..], 0xB1B0AFBA - Checksum(span));
        return sfnt;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 4 <= data.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        return sum;
    }
}
