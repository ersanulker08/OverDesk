using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Overdesk.App.Services;

/// <summary>Website icons, downloaded once (via a public favicon service) and kept on disk next to the settings.</summary>
internal static class FaviconCache
{
    private static readonly Dictionary<string, Bitmap?> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Action<Bitmap?>>> Waiting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static string Directory { get; set; } = Path.Combine(Path.GetTempPath(), "overdesk-favicons");

    /// <summary>The icon for a site's host, or null now and <paramref name="loaded"/> later (on the UI thread).</summary>
    public static Bitmap? Get(string url, Action<Bitmap?> loaded)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host;
        if (Memory.TryGetValue(host, out var known)) return known;
        if (Waiting.TryGetValue(host, out var list))
        {
            list.Add(loaded);
            return null;
        }
        Waiting[host] = new List<Action<Bitmap?>> { loaded };
        _ = LoadAsync(host);
        return null;
    }

    private static async Task LoadAsync(string host)
    {
        Bitmap? bitmap = null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var file = Path.Combine(Directory, host + ".png");
            if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-30))
            {
                var bytes = await Http.GetByteArrayAsync($"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=64");
                await File.WriteAllBytesAsync(file, bytes);
            }
            await using var stream = File.OpenRead(file);
            bitmap = new Bitmap(stream);
        }
        catch (Exception)
        {
            bitmap = null; // offline or unknown site: the pill shows a letter instead
        }

        Dispatcher.UIThread.Post(() =>
        {
            Memory[host] = bitmap;
            if (Waiting.Remove(host, out var callbacks))
                foreach (var callback in callbacks) callback(bitmap);
        });
    }
}
