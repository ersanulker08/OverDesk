using System.Runtime.InteropServices;
using System.Text;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.Windows;

/// <summary>Secrets in Windows Credential Manager ("OverDesk:&lt;name&gt;" generic credentials).</summary>
public sealed class WinCredentials : ISecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public string? Get(string name)
    {
        if (!CredRead("OverDesk:" + name, CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string name, string? value)
    {
        var target = "OverDesk:" + name;
        if (string.IsNullOrEmpty(value))
        {
            CredDelete(target, CredTypeGeneric, 0);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric, TargetName = target, CredentialBlobSize = bytes.Length, CredentialBlob = blob,
                Persist = CredPersistLocalMachine, UserName = Environment.UserName,
            };
            CredWrite(ref cred, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);
}
