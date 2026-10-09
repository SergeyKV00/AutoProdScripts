using System.Runtime.InteropServices;
using System.Text;

namespace AutoProdScripts.Services.Settings;

/// <summary>
/// Minimal Windows Credential Manager wrapper (generic credentials).
/// Safe no-op on non-Windows or if API calls fail.
/// </summary>
internal static class CredentialManagerStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public static bool TrySave(string target, string secret)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var byteArray = Encoding.Unicode.GetBytes(secret);
            var cred = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)byteArray.Length,
                CredentialBlob = Marshal.AllocHGlobal(byteArray.Length),
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                Comment = "AutoProdScripts Azure DevOps PAT"
            };

            try
            {
                Marshal.Copy(byteArray, 0, cred.CredentialBlob, byteArray.Length);
                return CredWrite(ref cred, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(cred.CredentialBlob);
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool TryLoad(string target, out string? secret)
    {
        secret = null;
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            if (!CredRead(target, CredTypeGeneric, 0, out var credPtr) || credPtr == IntPtr.Zero)
                return false;

            try
            {
                var cred = Marshal.PtrToStructure<NativeCredential>(credPtr);
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                    return false;

                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                secret = Encoding.Unicode.GetString(bytes);
                return !string.IsNullOrEmpty(secret);
            }
            finally
            {
                CredFree(credPtr);
            }
        }
        catch
        {
            secret = null;
            return false;
        }
    }

    public static bool TryDelete(string target)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            return CredDelete(target, CredTypeGeneric, 0);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }
}

