using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;

namespace Overdesk.App;

/// <summary>Live output of one task (dev server, build, shell command), with stop / rerun / copy / ask-AI.</summary>
public partial class TaskLogView : UserControl
{
    private RunningTask? _task;
    private TaskRunner? _runner;

    public TaskLogView()
    {
        InitializeComponent();
        Backdrop.PointerPressed += (_, _) => Hide();
        CloseButton.Click += (_, _) => Hide();
        StopButton.Click += (_, _) => { if (_task is not null) _runner?.Stop(_task); };
        RestartButton.Click += (_, _) =>
        {
            if (_task is null || _runner is null) return;
            _runner.Stop(_task);
            Show(_runner.Start(_task.Name, _task.Command, _task.Folder, _task.ProjectId), _runner);
        };
        CopyButton.Click += async (_, _) =>
        {
            if (_task is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_task.Output);
        };
        AskAiButton.Click += (_, _) =>
        {
            if (_task is null) return;
            var output = _task.Output;
            if (output.Length > 30_000) output = "…" + output[^30_000..];
            AskAi?.Invoke($"This is the output of `{_task.Command}` (run in {_task.Folder}). Explain what went wrong, if anything, and how to fix it:\n\n```\n{output}\n```");
            Hide();
        };
    }

    /// <summary>The host opens the AI panel with this question.</summary>
    internal event Action<string>? AskAi;

    internal void Show(RunningTask task, TaskRunner runner)
    {
        if (_runner is not null) _runner.Changed -= Refresh;
        _task = task;
        _runner = runner;
        runner.Changed += Refresh;
        if (!IsVisible)
        {
            IsVisible = true;
            Dialog.Opacity = 0;
            Animator.Run(this, 0.25, t =>
            {
                Dialog.Opacity = Ease.OutCubic(t);
                Dialog.RenderTransform = new TranslateTransform(0, 12 * (1 - Ease.OutCubic(t)));
            });
        }
        Refresh();
    }

    public void Hide()
    {
        if (_runner is not null) _runner.Changed -= Refresh;
        IsVisible = false;
    }

    private void Refresh()
    {
        if (_task is not { } task || !IsVisible) return;
        TitleText.Text = task.Name;
        DetailText.Text = $"{task.Command}   ·   {task.Folder}";
        StatusDot.Background = Res.Brush(task.IsRunning ? "SuccessBrush" : task.ExitCode == 0 ? "MutedBrush" : "DangerBrush");
        StatusDot.BoxShadow = task.IsRunning ? new BoxShadows(new BoxShadow { Blur = 8, Color = Color.Parse("#AA2FD98B") }) : default;
        StopButton.IsVisible = task.IsRunning;
        var atBottom = Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - 30;
        Output.Text = task.Output;
        if (atBottom) Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }
}
