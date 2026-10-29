using Avalonia.Controls;
using Overdesk.Core.Tabs;

namespace Overdesk.App;

/// <summary>
/// Split view: a second program shares the stage, on the right half, next to the active tab. Both windows are glued
/// to their halves (and mirrored on macOS); the handle between them resizes the halves.
/// </summary>
public partial class MainWindow
{
    private HostedTab? _splitTab;

    private void SplitWith(HostedTab partner)
    {
        if (_tabs.Active is not { IsInternal: false } active || partner == active || partner.IsInternal) return;
        _splitTab = partner;
        if (partner.Status != TabStatus.Running && !_tabStarts.ContainsKey(partner.Id)) _ = StartTabAsync(partner);
        ApplySplitLayout();
        UpdateTabContent();
        SyncLayout(force: true);
        ShowToast($"Split view: {active.Title} | {partner.Title}", "IconSplit");
    }

    private void EndSplit()
    {
        if (_splitTab is null) return;
        _embedding.SetVisible(TabKey(_splitTab), false);
        _splitTab = null;
        ApplySplitLayout();
        UpdateTabContent();
        SyncLayout(force: true);
    }

    private void ApplySplitLayout()
    {
        var on = _splitTab is not null;
        StageSplit.ColumnDefinitions[1].Width = new GridLength(on ? 14 : 0);
        StageSplit.ColumnDefinitions[2].Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitFrame.IsVisible = on;
        SplitHandle.IsVisible = on;
    }

    /// <summary>Called from UpdateTabContent: the split partner is visible next to the active tab, if any.</summary>
    private void UpdateSplitContent(HostedTab? active, bool visible)
    {
        // A split only makes sense next to a program; leaving for home, a website or settings ends it.
        if (_splitTab is not null && (active is null || active.IsInternal || active == _splitTab || !_tabs.Tabs.Contains(_splitTab)))
        {
            _embedding.SetVisible(TabKey(_splitTab), false);
            _splitTab = null;
            ApplySplitLayout();
        }
        if (_splitTab is not { } split) return;
        _embedding.SetVisible(TabKey(split), visible && split.Status == TabStatus.Running);
        SplitStatus.IsVisible = split.Status != TabStatus.Running;
        SplitStatusText.Text = split.Status switch
        {
            TabStatus.Starting => $"Opening {split.Title}…",
            TabStatus.Failed => $"Couldn't open {split.Title}",
            TabStatus.Exited => $"{split.Title} was closed",
            _ => "",
        };
    }
}
