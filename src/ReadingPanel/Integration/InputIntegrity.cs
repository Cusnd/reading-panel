using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ReadingPanel.Integration;

// Windows blocks replaying input into a more privileged app. Leave its physical click untouched.
internal static class InputIntegrity
{
    private static readonly int Current = CurrentLevel();

    internal static bool CanSendTo(uint processId)
    {
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) return false;
        using (token)
        {
            int target = Level(token);
            return target >= 0 && target <= Current;
        }
    }

    private static int CurrentLevel()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return Level(identity.AccessToken);
    }

    private static int Level(SafeAccessTokenHandle token)
    {
        const int TokenIntegrityLevel = 25;
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
        if (length <= 0) return -1;
        var label = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, label, length, out _)) return -1;
            var sid = Marshal.ReadIntPtr(label);
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return count == 0 ? -1 : Marshal.ReadInt32(GetSidSubAuthority(sid, count - 1));
        }
        finally { Marshal.FreeHGlobal(label); }
    }

    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint process);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr data, int size, out int needed);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);
}
