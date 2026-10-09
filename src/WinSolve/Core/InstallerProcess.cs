using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinSolve.Core;

/// <summary>
/// Runs an installer and waits for the whole installation, not just the first process.
/// </summary>
/// <remarks>
/// Driver packages are usually self-extractors: the exe you start unpacks the files, launches
/// the real setup and exits right away. Waiting for that first process made WinSolve offer a
/// restart while AMD's or Intel's setup was still running. The installer is put in a job object
/// (children join it automatically) and we wait until nothing in the job is left running,
/// except apps an installer opens at the end and that keep running (control panels, tray apps).
/// </remarks>
public static class InstallerProcess
{
    /// <summary>Apps that installers start when they finish and that stay open.</summary>
    private static readonly HashSet<string> StaysRunning = new(StringComparer.OrdinalIgnoreCase)
    {
        // NVIDIA
        "NVIDIA app", "NVIDIA Share", "NVIDIA Web Helper", "nvcontainer", "NVIDIA Overlay", "nvsphelper64", "NVDisplay.Container",
        // AMD
        "RadeonSoftware", "AMDRSServ", "AMDRSSrcExt", "cncmd", "atieclxx", "amdow", "AMDSoftwareInstaller_Launcher", "AMD Software",
        // Intel
        "IntelGraphicsSoftware", "IGCC", "igfxEM", "IntelGraphicsSoftware.Service", "DSAService", "DSATray",
        // Generic
        "explorer", "msedge", "chrome", "firefox", "conhost",
    };

    /// <summary>Starts <paramref name="file"/> and waits until the installation it starts has finished.</summary>
    /// <returns>The exit code of the process that was started (not of its children).</returns>
    public static async Task<int?> RunAndWaitAsync(string file, string? arguments, Action<string> log, CancellationToken ct)
    {
        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new InvalidOperationException("Could not create a job object to track the installer.");
        // Installers that start helpers with CREATE_BREAKAWAY_FROM_JOB must not fail because of us.
        var limits = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_BREAKAWAY_OK };
        SetInformationJobObject(job, JobObjectBasicLimitInformation, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_BASIC_LIMIT_INFORMATION>());

        // Started suspended and put in the job before it runs a single instruction: a fast
        // self-extractor could otherwise launch its setup before being tracked.
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var commandLine = $"\"{file}\"" + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments);
        if (!CreateProcessW(file, commandLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero,
                Path.GetDirectoryName(file), ref si, out var pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Could not start {Path.GetFileName(file)}");

        using var process = new SafeJobHandle(pi.hProcess);
        using var thread = new SafeJobHandle(pi.hThread);
        var inJob = AssignProcessToJobObject(job, pi.hProcess);
        ResumeThread(pi.hThread);
        if (!inJob) log("  (could not track the installer's child processes; waiting for the main one only)");

        while (WaitForSingleObject(pi.hProcess, 500) == WAIT_TIMEOUT)
        {
            if (ct.IsCancellationRequested) break;
            await Task.Delay(50, CancellationToken.None);
        }
        ct.ThrowIfCancellationRequested();
        int? exitCode = GetExitCodeProcess(pi.hProcess, out var code) ? (int)code : null;
        if (!inJob) return exitCode;

        // The started exe is done; its setup may still be running. Capped, in case something
        // the installer opened (a browser, a tray app) never closes.
        var announced = false;
        var deadline = DateTime.Now.AddMinutes(90);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.Now > deadline)
            {
                log("  Stopped waiting after 90 minutes; check that the installer has finished.");
                break;
            }
            var running = RunningInJob(job).Where(n => !StaysRunning.Contains(n)).ToList();
            if (running.Count == 0) break;
            if (!announced)
            {
                log($"  Waiting for the installer to finish ({string.Join(", ", running.Distinct())})...");
                announced = true;
            }
            await Task.Delay(2000, ct);
        }
        return exitCode;
    }

    private static List<string> RunningInJob(SafeJobHandle job)
    {
        var names = new List<string>();
        // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORDs (assigned, in list), then ULONG_PTR ids.
        const int max = 2048;
        var size = 8 + IntPtr.Size * max;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, 0, max);
            // A failed query means "unknown", not "finished".
            if (!QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, size, IntPtr.Zero)) return ["(installer)"];
            var count = Marshal.ReadInt32(buffer, 4);
            for (int i = 0; i < count; i++)
            {
                var pid = (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    if (!proc.HasExited) names.Add(proc.ProcessName);
                }
                catch { /* exited between the query and now */ }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return names;
    }

    private const int JobObjectBasicProcessIdList = 3, JobObjectBasicLimitInformation = 2;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x800;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JOBOBJECT_BASIC_LIMIT_INFORMATION info, uint length);
    private const uint CREATE_SUSPENDED = 0x4;
    private const uint WAIT_TIMEOUT = 0x102;

    private sealed class SafeJobHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        public SafeJobHandle(IntPtr existing) : base(true) => SetHandle(existing);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeJobHandle job, int infoClass, IntPtr info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
