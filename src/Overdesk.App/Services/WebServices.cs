using Avalonia.Media;

namespace Overdesk.App.Services;

/// <summary>A well-known web service that can sit in the web strip. AppNames are the desktop apps that stand for it.</summary>
internal sealed record WebService(string Id, string Name, string Url, Color Brand, string[] AppNames, string Group);

/// <summary>The catalog of services the web strip knows (pinned by default, or addable from its "+" menu).</summary>
internal static class WebServices
{
    public static readonly IReadOnlyList<WebService> All = new[]
    {
        new WebService("github", "GitHub", "https://github.com", Color.Parse("#C9D1D9"), new[] { "GitHub Desktop" }, "Developer"),
        new WebService("chatgpt", "ChatGPT", "https://chatgpt.com", Color.Parse("#10A37F"), new[] { "ChatGPT" }, "AI"),
        new WebService("codex", "Codex", "https://chatgpt.com/codex", Color.Parse("#7C83FD"), new[] { "Codex" }, "AI"),
        new WebService("claude", "Claude", "https://claude.ai", Color.Parse("#D97757"), new[] { "Claude" }, "AI"),
        new WebService("claude-code", "Claude Code", "https://claude.ai/code", Color.Parse("#E08A5E"), Array.Empty<string>(), "AI"),
        new WebService("gemini", "Gemini", "https://gemini.google.com", Color.Parse("#4F8DF5"), new[] { "Gemini" }, "AI"),
        new WebService("perplexity", "Perplexity", "https://www.perplexity.ai", Color.Parse("#20B8CD"), new[] { "Perplexity" }, "AI"),
        new WebService("copilot", "Copilot", "https://copilot.microsoft.com", Color.Parse("#5B8CFF"), new[] { "Copilot" }, "AI"),
        new WebService("huggingface", "Hugging Face", "https://huggingface.co", Color.Parse("#FFC93D"), Array.Empty<string>(), "AI"),
        new WebService("gitlab", "GitLab", "https://gitlab.com", Color.Parse("#FC6D26"), Array.Empty<string>(), "Developer"),
        new WebService("stackoverflow", "Stack Overflow", "https://stackoverflow.com", Color.Parse("#F48024"), Array.Empty<string>(), "Developer"),
        new WebService("vercel", "Vercel", "https://vercel.com/dashboard", Color.Parse("#E6E6E6"), Array.Empty<string>(), "Developer"),
        new WebService("devdocs", "DevDocs", "https://devdocs.io", Color.Parse("#3E8FD8"), Array.Empty<string>(), "Developer"),
        new WebService("figma", "Figma", "https://www.figma.com", Color.Parse("#A259FF"), new[] { "Figma" }, "Design"),
        new WebService("notion", "Notion", "https://www.notion.so", Color.Parse("#E6E6E6"), new[] { "Notion" }, "Productivity"),
        new WebService("youtube", "YouTube", "https://www.youtube.com", Color.Parse("#FF3B30"), Array.Empty<string>(), "Media"),
    };

    public static WebService? Find(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>The installed desktop app for a service, if any (matched by program name).</summary>
    public static string? InstalledApp(WebService service, IReadOnlyList<string> installed)
        => service.AppNames.Length == 0
            ? null
            : installed.FirstOrDefault(p => service.AppNames.Any(n =>
                string.Equals(Path.GetFileNameWithoutExtension(p.TrimEnd('/', '\\')), n, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Normalize what the user typed into a URL ("github.com" → "https://github.com").</summary>
    public static string? NormalizeUrl(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }
}
