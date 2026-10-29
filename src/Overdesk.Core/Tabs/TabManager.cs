using System.Collections.ObjectModel;

namespace Overdesk.Core.Tabs;

/// <summary>Owns the ordered tab list and which tab is active. No OS or UI code.</summary>
public sealed class TabManager : ObservableObject
{
    private HostedTab? _active;

    public ObservableCollection<HostedTab> Tabs { get; } = new();

    public HostedTab? Active
    {
        get => _active;
        private set
        {
            var previous = _active;
            if (!Set(ref _active, value)) return;
            if (previous is not null) previous.IsActive = false;
            if (value is not null) value.IsActive = true;
        }
    }

    public HostedTab Add(string programPath, bool activate = true, string? title = null)
    {
        var tab = new HostedTab(programPath, title);
        Tabs.Add(tab);
        if (activate || Active is null) Activate(tab);
        return tab;
    }

    public void Activate(HostedTab tab)
    {
        if (Tabs.Contains(tab)) Active = tab;
    }

    /// <summary>Replace the whole tab row (switching workspaces). The tabs keep their identity and running programs.</summary>
    public void Load(IEnumerable<HostedTab> tabs, HostedTab? active)
    {
        Active = null;
        Tabs.Clear();
        foreach (var tab in tabs)
        {
            tab.IsActive = false;
            Tabs.Add(tab);
        }
        if (active is not null && Tabs.Contains(active)) Active = active;
    }

    /// <summary>No tab is active: the home page is shown.</summary>
    public void ShowHome() => Active = null;

    /// <summary>Remove a tab; the neighbour to the left (or the new first tab) becomes active.</summary>
    public void Close(HostedTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.RemoveAt(index);
        if (ReferenceEquals(Active, tab))
            Active = Tabs.Count == 0 ? null : Tabs[Math.Max(index - 1, 0)];
    }

    public void Move(HostedTab tab, int newIndex)
    {
        var old = Tabs.IndexOf(tab);
        if (old < 0) return;
        newIndex = Math.Clamp(newIndex, 0, Tabs.Count - 1);
        if (old != newIndex) Tabs.Move(old, newIndex);
    }

    public void ActivateNext(int step)
    {
        if (Tabs.Count == 0 || Active is null) return;
        var i = (Tabs.IndexOf(Active) + step + Tabs.Count) % Tabs.Count;
        Activate(Tabs[i]);
    }
}
