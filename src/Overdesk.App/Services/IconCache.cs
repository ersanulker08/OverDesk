using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Overdesk.Core.Platform;

namespace Overdesk.App.Services;

/// <summary>
/// Program icons as Avalonia bitmaps, read once per path through the OS catalog.
/// Icons are decoded on a background thread so long lists never stall the UI.
/// </summary>
internal static class IconCache
{
    private const int PixelSize = 128;
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, List<Action<Bitmap?>>> Pending = new(StringComparer.Ordinal);
    private static readonly BlockingCollection<string> Queue = new();
    private static Thread? _worker;

    public static IProgramCatalog Catalog { get; set; } = new EmptyProgramCatalog();

    /// <summary>The icon if it is already loaded; otherwise null, and <paramref name="loaded"/> runs on the UI thread later.</summary>
    public static bool TryGet(string? programPath, out Bitmap? bitmap, Action<Bitmap?> loaded)
    {
        bitmap = null;
        if (string.IsNullOrEmpty(programPath)) return true;
        if (Cache.TryGetValue(programPath, out bitmap)) return true;

        var waiting = Pending.GetOrAdd(programPath, _ => new List<Action<Bitmap?>>());
        lock (waiting)
        {
            waiting.Add(loaded);
            if (waiting.Count == 1) Enqueue(programPath);
        }
        return false;
    }

    private static void Enqueue(string path)
    {
        if (_worker is null)
        {
            _worker = new Thread(Work) { IsBackground = true, Name = "Overdesk icon loader" };
            _worker.Start();
        }
        Queue.Add(path);
    }

    private static void Work()
    {
        foreach (var path in Queue.GetConsumingEnumerable())
        {
            var bitmap = Load(path);
            Cache[path] = bitmap;
            if (!Pending.TryRemove(path, out var waiting)) continue;
            Action<Bitmap?>[] callbacks;
            lock (waiting) callbacks = waiting.ToArray();
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var callback in callbacks) callback(bitmap);
            });
        }
    }

    private static Bitmap? Load(string programPath)
    {
        try
        {
            if (Catalog.GetIcon(programPath, PixelSize) is not { } icon) return null;
            return icon.IsPng ? FromPng(icon.Data) : FromBgra(icon);
        }
        catch (Exception)
        {
            return null; // a broken icon only costs us the picture, never the app
        }
    }

    private static Bitmap FromPng(byte[] data)
    {
        using var stream = new MemoryStream(data);
        return new Bitmap(stream);
    }

    private static unsafe Bitmap FromBgra(ProgramIcon icon)
    {
        fixed (byte* p = icon.Data)
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, (IntPtr)p,
                new PixelSize(icon.Width, icon.Height), new Vector(96, 96), icon.Width * 4);
        }
    }
}
