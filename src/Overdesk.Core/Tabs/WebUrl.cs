using System.Net;
using System.Net.Sockets;

namespace Overdesk.Core.Tabs;

/// <summary>What people type into an address box, turned into web addresses.</summary>
public static class WebUrl
{
    /// <summary>"github.com" → https, "localhost:5173" and LAN addresses → http; null when it is no web address.</summary>
    public static string? Normalize(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = (IsPrivateHost(HostOf(text)) ? "http://" : "https://") + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    /// <summary>True when an address box should go to <paramref name="text"/> instead of searching for it.</summary>
    public static bool IsAddress(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return false;
        if (text.Contains("://", StringComparison.Ordinal)) return true;
        var host = HostOf(text);
        return host.Contains('.') || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _);
    }

    /// <summary>Hosts that only mean something on this computer or network: no outside service can know them.</summary>
    public static bool IsPrivateHost(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal) return true;
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return !host.Contains('.') || new[] { ".localhost", ".local", ".test", ".internal", ".lan" }
            .Any(end => host.EndsWith(end, StringComparison.OrdinalIgnoreCase));
    }

    private static string HostOf(string text)
    {
        var end = text.IndexOfAny(new[] { '/', '?', '#' });
        var authority = end < 0 ? text : text[..end];
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            return close > 0 ? authority[1..close] : authority;
        }
        var colon = authority.LastIndexOf(':');
        return colon >= 0 ? authority[..colon] : authority;
    }
}
