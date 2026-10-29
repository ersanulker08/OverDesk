using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Overdesk.Core.Platform;

namespace Overdesk.App.Services;

/// <summary>"Pick a program" dialog; returns local paths (an .app bundle counts as one path on macOS).</summary>
internal static class ProgramPicker
{
    public static async Task<IReadOnlyList<string>> PickAsync(TopLevel owner, IWindowEmbedder embedder, string title)
    {
        var programs = new FilePickerFileType("Programs")
        {
            Patterns = embedder.ProgramPatterns,
            AppleUniformTypeIdentifiers = new[] { "com.apple.application-bundle", "public.unix-executable" },
        };

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = new[] { programs, FilePickerFileTypes.All },
        });

        return files.Select(f => f.TryGetLocalPath()).OfType<string>().Select(BundleRoot).ToList();
    }

    /// <summary>On macOS a file picked inside an .app bundle (Contents/MacOS/…) means the app itself.</summary>
    private static string BundleRoot(string path)
    {
        var index = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? path.TrimEnd('/') : path[..(index + 4)];
    }
}
