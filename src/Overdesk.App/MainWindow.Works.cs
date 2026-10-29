using Avalonia.Controls;
using Overdesk.App.Services;
using Overdesk.Core.Plugins;
using Overdesk.Core.Tabs;
using Overdesk.Core.Works;

namespace Overdesk.App;

// Works: notes, lists, stories and game variables the user writes. Shown as cards on the home page ("Works" tab),
// as a tool in the right-hand panel and, when opened, as a tab of their own.
public partial class MainWindow
{
    private const string WorksToolId = "overdesk.works";
    private WorkService _works = null!;
    private WorksPanelView? _worksPanel;
    private WorkEditorView? _workEditor;

    private void InitializeWorks()
    {
        _works = new WorkService(Path.Combine(_store.Directory, "works"), _settings);
        _works.TabRequested += OpenWorkTab;
        _works.PanelRequested += OpenWorkInPanel;
        _works.Library.Changed += OnWorksChanged;
        Home.InitializeWorks(_works);
    }

    private void RegisterWorksTool()
    {
        _plugins.AddBuiltIn(new PluginManifest
        {
            Id = WorksToolId, Name = "Works", Run = PluginRunMode.InProcess, Icon = "IconNote",
            Description = "Notes, lists, stories and game variables",
            BuiltIn = () => new BuiltInTool(() => _worksPanel ??= new WorksPanelView(_works)),
        });
    }

    private void OpenWorkTab(Work work)
    {
        if (OverlayOpen) return;
        var path = HostedTab.WorkPath(work.Id);
        var tab = _tabs.Tabs.FirstOrDefault(t => t.ProgramPath == path) ?? _tabs.Add(path, activate: false, title: work.Title);
        tab.Title = TabTitle(work);
        _tabs.Activate(tab);
    }

    private void OpenWorkInPanel(Work work)
    {
        var tool = _plugins.Tools.FirstOrDefault(t => t.Id == WorksToolId);
        if (tool is null) return;
        if (!(_toolPanelOpen && _activeTool == tool)) ToggleTool(tool);
        _worksPanel ??= new WorksPanelView(_works);
        _worksPanel.OpenWork(work);
    }

    private void AddWorkPaletteItems(List<PaletteItem> items)
    {
        items.Add(new PaletteItem("New work…", "Action", () =>
        {
            ShowHome();
            Home.ShowWorksTab(true);
            Home.WorksHub.ShowGallery();
        }, "IconPlus"));
        items.Add(new PaletteItem("Show my works", "Action", () =>
        {
            ShowHome();
            Home.ShowWorksTab(true);
        }, "IconNote"));
        foreach (var work in _works.Library.Works.Take(40))
        {
            var w = work;
            items.Add(new PaletteItem($"Open {TabTitle(w)}", "Work", () => OpenWorkTab(w), "IconNote", Detail: WorkTemplates.Find(w.Template).Name));
        }
    }

    private static string TabTitle(Work work) => string.IsNullOrWhiteSpace(work.Title) ? "Untitled" : work.Title;

    /// <summary>Keep tab titles in step with work titles, and close tabs of deleted works.</summary>
    private void OnWorksChanged(Work? changed, object? sender)
    {
        foreach (var tab in _tabs.Tabs.Where(t => t.IsWork).ToList())
        {
            if (_works.Library.Find(tab.WorkId) is { } work) tab.Title = TabTitle(work);
            else CloseTab(tab);
        }
    }

    /// <summary>Show the editor of the active tab when it is a work, hide it otherwise.</summary>
    private void UpdateWorkPage(HostedTab? tab)
    {
        if (tab is { IsWork: true } && _works.Library.Find(tab.WorkId) is { } work)
        {
            if (_workEditor?.Work != work || !WorkPage.IsVisible)
            {
                _workEditor = new WorkEditorView(_works, work, compact: false);
                _workEditor.Deleted += () => { if (_tabs.Tabs.FirstOrDefault(t => t.WorkId == work.Id) is { } t) CloseTab(t); };
                WorkPage.Child = _workEditor;
            }
            WorkPage.IsVisible = true;
        }
        else if (WorkPage.IsVisible)
        {
            WorkPage.IsVisible = false;
            WorkPage.Child = null;
            _workEditor = null;
            _works.Flush();
        }
    }

    // ---- hooks for the development snapshot tool (Services/DevSnapshot)
    internal void DevSeedWorks()
    {
        foreach (var id in new[] { "gdd", "variables", "character", "story", "todo", "quest", "journal", "ideas" })
        {
            var work = _works.Library.Create(WorkTemplates.Find(id));
            if (id == "todo") work.Blocks[0].Checked = true;
            if (id == "story") work.Title = "The Last Lighthouse";
            if (id == "character") { work.Title = "Aria Vale"; work.Blocks[0].Value = "Aria Vale"; work.Blocks[1].Value = "Smuggler"; }
            if (id == "variables") work.Blocks[1].Checked = true;
            _works.Library.Touch(work, listChanged: true);
        }
        _works.Flush();
    }

    internal void DevShowWorksTab(bool gallery)
    {
        ShowHome();
        Home.ShowWorksTab(true);
        if (gallery) Home.WorksHub.ShowGallery();
    }

    internal void DevOpenWork(int index, bool drawer)
    {
        OpenWorkTab(_works.Library.Works[index]);
        if (drawer) _workEditor?.ShowDrawer();
    }

    internal void DevOpenWorkInPanel(int index) => OpenWorkInPanel(_works.Library.Works[index]);

    internal void DevOpenWorksList()
    {
        if (_plugins.Tools.FirstOrDefault(t => t.Id == WorksToolId) is { } tool) OpenTool(tool);
    }
}
