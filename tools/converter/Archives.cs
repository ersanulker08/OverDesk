using System.Formats.Tar;
using System.IO.Compression;

namespace Overdesk.Tool.Converter;

/// <summary>
/// ZIP, TAR, TAR.GZ and GZ into each other, unpacking into a folder, and packing any file into a ZIP or GZ.
/// Everything goes through .NET's own readers and writers, so it works the same on macOS and Windows.
/// </summary>
public static class Archives
{
    /// <summary>"tar.gz" for x.tar.gz / x.tgz, otherwise the plain extension.</summary>
    public static string Format(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz")) return "tar.gz";
        return Path.GetExtension(name).TrimStart('.');
    }

    /// <summary>The file name without any archive extension (x.tar.gz → x).</summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var ext in new[] { ".tar.gz", ".tgz", ".tar", ".zip", ".gz" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    public static bool IsArchive(string path) => Format(path) is "zip" or "tar" or "tar.gz" or "gz";

    /// <summary>Unpack into a new folder; returns it.</summary>
    public static string Extract(string input, string folder, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);
        switch (Format(input))
        {
            case "zip":
                ZipFile.ExtractToDirectory(input, folder, overwriteFiles: false);
                break;
            case "tar":
                TarFile.ExtractToDirectory(input, folder, overwriteFiles: false);
                break;
            case "tar.gz":
            {
                using var file = File.OpenRead(input);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, folder, overwriteFiles: false);
                break;
            }
            case "gz":
            {
                using var file = File.OpenRead(input);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var output = File.Create(Path.Combine(folder, BaseName(input)));
                gzip.CopyTo(output);
                break;
            }
            default:
                throw new InvalidOperationException("Not an archive.");
        }
        cancellation.ThrowIfCancellationRequested();
        return folder;
    }

    /// <summary>
    /// Write <paramref name="output"/> in <paramref name="target"/> format (zip, tar, tar.gz, gz). Archives are repacked,
    /// any other file (or folder) is packed as it is.
    /// </summary>
    public static void Pack(string input, string output, string target, CancellationToken cancellation)
    {
        if (Directory.Exists(input))
        {
            PackFolder(input, output, target, includeBase: true);
            return;
        }
        if (IsArchive(input) && !(target == "gz" && Format(input) == "tar"))
        {
            // Repack: unpack to a temporary folder, then pack its contents.
            var temp = Path.Combine(Path.GetTempPath(), $"overdesk-{Guid.NewGuid():N}");
            try
            {
                Extract(input, temp, cancellation);
                PackFolder(temp, output, target, includeBase: false);
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch (IOException) { }
            }
            return;
        }
        switch (target)
        {
            case "zip":
            {
                using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(input, Path.GetFileName(input), CompressionLevel.Optimal);
                break;
            }
            case "gz":
            {
                using var source = File.OpenRead(input);
                using var file = File.Create(output);
                using var gzip = new GZipStream(file, CompressionLevel.Optimal);
                source.CopyTo(gzip);
                break;
            }
            case "tar" or "tar.gz":
            {
                using var file = File.Create(output);
                using Stream stream = target == "tar" ? file : new GZipStream(file, CompressionLevel.Optimal);
                using var writer = new TarWriter(stream, TarEntryFormat.Pax);
                writer.WriteEntry(input, Path.GetFileName(input));
                break;
            }
            default:
                throw new InvalidOperationException($"Cannot pack into {target}.");
        }
    }

    private static void PackFolder(string folder, string output, string target, bool includeBase)
    {
        switch (target)
        {
            case "zip":
                ZipFile.CreateFromDirectory(folder, output, CompressionLevel.Optimal, includeBase);
                break;
            case "tar":
                TarFile.CreateFromDirectory(folder, output, includeBase);
                break;
            case "tar.gz":
            {
                using var file = File.Create(output);
                using var gzip = new GZipStream(file, CompressionLevel.Optimal);
                TarFile.CreateFromDirectory(folder, gzip, includeBase);
                break;
            }
            case "gz":
            {
                // A folder cannot be one gzip stream: gzip a tar of it.
                using var file = File.Create(output);
                using var gzip = new GZipStream(file, CompressionLevel.Optimal);
                TarFile.CreateFromDirectory(folder, gzip, includeBase);
                break;
            }
            default:
                throw new InvalidOperationException($"Cannot pack into {target}.");
        }
    }
}
