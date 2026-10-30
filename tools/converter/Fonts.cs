using System.Buffers.Binary;
using System.IO.Compression;

namespace Overdesk.Tool.Converter;

/// <summary>
/// Web fonts: TTF / OTF into WOFF (each table zlib-compressed, as the WOFF 1.0 spec says) and WOFF back into the
/// TTF or OTF it was made from. The font data itself is copied byte for byte.
/// </summary>
public static class Fonts
{
    private const uint OtfFlavor = 0x4F54544F; // "OTTO": CFF outlines

    /// <summary>"otf" or "ttf": what a WOFF file unpacks to.</summary>
    public static string WoffFlavor(string path)
    {
        Span<byte> header = stackalloc byte[8];
        using var file = File.OpenRead(path);
        file.ReadExactly(header);
        return BinaryPrimitives.ReadUInt32BigEndian(header[4..]) == OtfFlavor ? "otf" : "ttf";
    }

    public static byte[] ToWoff(byte[] sfnt)
    {
        var flavor = BinaryPrimitives.ReadUInt32BigEndian(sfnt);
        int count = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        if (flavor is not (0x00010000 or OtfFlavor or 0x74727565) || count == 0 || 12 + count * 16 > sfnt.Length)
            throw new InvalidOperationException("Not a TrueType / OpenType font.");
        var tables = new List<(uint Tag, uint Checksum, byte[] Data, byte[] Stored)>();
        for (var i = 0; i < count; i++)
        {
            var record = sfnt.AsSpan(12 + i * 16);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            if (offset + length > sfnt.Length) throw new InvalidOperationException("The font is damaged (a table runs past the end).");
            var data = sfnt.AsSpan(offset, length).ToArray();
            var compressed = Compress(data);
            tables.Add((BinaryPrimitives.ReadUInt32BigEndian(record), BinaryPrimitives.ReadUInt32BigEndian(record[4..]), data, compressed.Length < data.Length ? compressed : data));
        }

        var sfntSize = 12 + 16 * count + tables.Sum(t => Pad(t.Data.Length));
        var offsetNow = 44 + 20 * count;
        var total = offsetNow + tables.Sum(t => Pad(t.Stored.Length));
        var woff = new byte[total];
        var span = woff.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, 0x774F4646); // "wOFF"
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], flavor);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)total);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], (ushort)count);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], (uint)sfntSize);
        BinaryPrimitives.WriteUInt16BigEndian(span[20..], 1);
        for (var i = 0; i < tables.Count; i++)
        {
            var (tag, checksum, data, stored) = tables[i];
            var entry = span[(44 + i * 20)..];
            BinaryPrimitives.WriteUInt32BigEndian(entry, tag);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)offsetNow);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)stored.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], (uint)data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[16..], checksum);
            stored.CopyTo(span[offsetNow..]);
            offsetNow += Pad(stored.Length);
        }
        return woff;
    }

    public static byte[] FromWoff(byte[] woff)
    {
        if (woff.Length < 44 || BinaryPrimitives.ReadUInt32BigEndian(woff) != 0x774F4646)
            throw new InvalidOperationException(BinaryPrimitives.ReadUInt32BigEndian(woff) == 0x774F4632 ? "WOFF2 fonts are not supported yet; only WOFF." : "Not a WOFF font.");
        var flavor = BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16BigEndian(woff.AsSpan(12));
        var tables = new List<(uint Tag, uint Checksum, byte[] Data)>();
        for (var i = 0; i < count; i++)
        {
            var entry = woff.AsSpan(44 + i * 20);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var stored = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            var raw = woff.AsSpan(offset, stored).ToArray();
            var data = stored < length ? Decompress(raw, length) : raw;
            tables.Add((BinaryPrimitives.ReadUInt32BigEndian(entry), BinaryPrimitives.ReadUInt32BigEndian(entry[16..]), data));
        }
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));

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
        for (var i = 0; i < tables.Count; i++)
        {
            var record = span[(12 + i * 16)..];
            BinaryPrimitives.WriteUInt32BigEndian(record, tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], tables[i].Checksum);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)tables[i].Data.Length);
            tables[i].Data.CopyTo(span[at..]);
            at += Pad(tables[i].Data.Length);
        }
        return sfnt;
    }

    private static int Pad(int length) => (length + 3) & ~3;

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(data);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data, int length)
    {
        using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        var result = new byte[length];
        zlib.ReadExactly(result);
        return result;
    }
}
