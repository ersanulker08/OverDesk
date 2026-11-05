namespace Overdesk.Core.Tabs;

/// <summary>A website open inside Overdesk (the web strip under the program tabs).</summary>
public sealed class WebTab : ObservableObject
{
    private string _title;
    private string _url;
    private bool _isActive;
    private bool _isLoading;

    public WebTab(string url, string title, string? serviceId)
    {
        _url = url;
        _title = title;
        ServiceId = serviceId;
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>What pairs the tab with its pill: a known service id (GitHub, Claude...) or the site's address.</summary>
    public string? ServiceId { get; set; }

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
}
