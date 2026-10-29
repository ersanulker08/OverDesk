using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Overdesk.App.Theme;

/// <summary>Application resource lookup for UI built in code (works before a control is attached).</summary>
internal static class Res
{
    public static T Get<T>(string key)
        => Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var value) && value is T typed
            ? typed
            : throw new KeyNotFoundException($"Resource '{key}' is missing or not a {typeof(T).Name}.");

    public static Geometry Icon(string key) => Get<Geometry>(key);
    public static IBrush Brush(string key) => Get<IBrush>(key);
}
