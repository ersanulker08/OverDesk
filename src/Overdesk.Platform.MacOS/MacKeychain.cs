using System.Runtime.InteropServices;
using System.Text;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>Secrets in the user's login keychain (service "OverDesk"), through the Security framework.</summary>
public sealed class MacKeychain : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Service = "OverDesk";
    private readonly Dictionary<string, string?> _cache = new();

    public string? Get(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(name);
        string? value = null;
        if (SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out var length, out var data, out var item) == 0)
        {
            value = Marshal.PtrToStringUTF8(data, (int)length);
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) Native.CFRelease(item);
        }
        _cache[name] = value;
        return value;
    }

    public void Set(string name, string? value)
    {
        _cache[name] = string.IsNullOrEmpty(value) ? null : value;
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(name);
        var found = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            out _, out var oldData, out var item) == 0;
        if (found && oldData != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, oldData);
        try
        {
            if (string.IsNullOrEmpty(value))
            {
                if (found) SecKeychainItemDelete(item);
                return;
            }
            var secret = Encoding.UTF8.GetBytes(value);
            if (found) SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret);
            else SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                (uint)secret.Length, secret, IntPtr.Zero);
        }
        finally
        {
            if (found && item != IntPtr.Zero) Native.CFRelease(item);
        }
    }

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr passwordData, out IntPtr item);
    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr item);
    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
}
