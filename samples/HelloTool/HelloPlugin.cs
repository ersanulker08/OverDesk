using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Overdesk.Core.Plugins;

namespace Overdesk.Samples.HelloTool;

/// <summary>
/// OverDesk creates this class (named by "typeName" in the manifest) and puts the returned control in the tools panel.
/// The view inherits OverDesk's theme, so plain Avalonia controls already look at home.
/// </summary>
public sealed class HelloPlugin : IToolPlugin
{
    public object CreateView()
    {
        var count = 0;
        var label = new TextBlock { Text = "Clicked 0 times", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
        var button = new Button { Content = "Click me", HorizontalAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => label.Text = $"Clicked {++count} times";

        return new StackPanel
        {
            Spacing = 12,
            Margin = new Avalonia.Thickness(20),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Hello from a plugin", FontSize = 18, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock
                {
                    Text = "This view lives in samples/HelloTool. Use it as the starting point for your own tool.",
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.7,
                },
                button,
                label,
            },
        };
    }
}
