using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>Secrets in the user's login keychain (service "OverDesk"), through the Security framework.</summary>
public sealed class MacKeychain : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Service = "OverDesk";
    private const int ErrItemNotFound = -25300;

    // Read on provider threads while Settings writes on the UI thread.
    private readonly ConcurrentDictionary<string, string?> _cache = new();

    public string? Get(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(name);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            out var length, out var data, out var item);
        if (status == 0)
        {
            var value = Marshal.PtrToStringUTF8(data, (int)length);
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) Native.CFRelease(item);
            _cache[name] = value;
            return value;
        }
        // Only a real "not there" is remembered: after a refused prompt the next read must ask again.
        if (status == ErrItemNotFound) _cache[name] = null;
        return null;
    }

    public void Set(string name, string? value) => TrySet(name, value);

    public bool TrySet(string name, string? value)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(name);
        // Looks up the item without its secret, which needs no permission and also finds an item we may not read.
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            IntPtr.Zero, IntPtr.Zero, out var item);
        if (status != 0 && status != ErrItemNotFound) return false;
        var found = status == 0;
        try
        {
            if (string.IsNullOrEmpty(value)) status = found ? SecKeychainItemDelete(item) : 0;
            else
            {
                var secret = Encoding.UTF8.GetBytes(value);
                status = found
                    ? SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret)
                    : SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                        (uint)secret.Length, secret, IntPtr.Zero);
            }
        }
        finally
        {
            if (found && item != IntPtr.Zero) Native.CFRelease(item);
        }
        if (status != 0) return false;
        _cache[name] = string.IsNullOrEmpty(value) ? null : value;
        return true;
    }

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr passwordData, out IntPtr item);
    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, IntPtr passwordLength, IntPtr passwordData, out IntPtr item);
    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr item);
    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
}
