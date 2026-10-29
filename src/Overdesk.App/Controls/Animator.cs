using Avalonia;
using Avalonia.Controls;

namespace Overdesk.App.Controls;

/// <summary>Runs a short code-driven animation on the window's frame clock: <c>step</c> gets 0..1.</summary>
internal static class Animator
{
    public static void Run(Visual visual, double seconds, Action<double> step, Action? done = null)
    {
        if (TopLevel.GetTopLevel(visual) is not { } top)
        {
            step(1);
            done?.Invoke();
            return;
        }

        TimeSpan? start = null;
        void Frame(TimeSpan now)
        {
            start ??= now;
            var t = Math.Clamp((now - start.Value).TotalSeconds / seconds, 0, 1);
            step(t);
            if (t < 1) top.RequestAnimationFrame(Frame);
            else done?.Invoke();
        }
        top.RequestAnimationFrame(Frame);
    }
}
