using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinSolve.Services;

/// <summary>
/// Frees the standby list: memory Windows keeps filled with recently used files "just in case".
/// It is handed back to programs on demand anyway, so this mostly helps games or tools that check
/// "free" memory before starting; the cache fills up again as files are used.
/// </summary>
public static class MemoryCleaner
{
    /// <param name="Freed">Cache released (standby list before minus after).</param>
    /// <param name="FreeAfter">Memory that is now completely free (free and zeroed pages).</param>
    public sealed record Result(long Freed, long FreeAfter, long Total);

    public static Result PurgeStandbyList()
    {
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        // "Available" memory already includes the standby list, so it barely moves: measure the
        // list itself instead.
        var before = Lists();
        int command = MemoryPurgeStandbyList;
        var status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        if (status != 0)
            throw new Win32Exception(RtlNtStatusToDosError(status));
        var after = Lists();
        return new Result(Math.Max(0, before.Standby - after.Standby), after.Free, (long)Status().ullTotalPhys);
    }

    private static unsafe (long Standby, long Free) Lists()
    {
        var info = new SYSTEM_MEMORY_LIST_INFORMATION();
        var status = NtQuerySystemInformation(SystemMemoryListInformation, ref info, Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>(), out _);
        if (status != 0) throw new Win32Exception(RtlNtStatusToDosError(status));
        long page = Environment.SystemPageSize;
        long standby = 0;
        for (int i = 0; i < 8; i++) standby += (long)info.PageCountByPriority[i];
        return (standby * page, ((long)info.ZeroPageCount + (long)info.FreePageCount) * page);
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SYSTEM_MEMORY_LIST_INFORMATION
    {
        public nuint ZeroPageCount, FreePageCount, ModifiedPageCount, ModifiedNoWritePageCount, BadPageCount;
        public fixed ulong PageCountByPriority[8];
        public fixed ulong RepurposedPagesByPriority[8];
        public nuint ModifiedPageCountPageFile;
    }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, ref SYSTEM_MEMORY_LIST_INFORMATION info, int length, out int returned);

    private static MEMORYSTATUSEX Status()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) throw new Win32Exception();
        return m;
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception();
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            // Succeeds without enabling anything when the account lacks the privilege.
            if (Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                throw new UnauthorizedAccessException("Freeing memory needs administrator rights.");
        }
        finally { CloseHandle(token); }
    }

    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 2;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("ntdll.dll")] private static extern int RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);
}
