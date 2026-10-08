using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed record DriveSpace(string Name, string Label, long Total, long Free)
{
    public double FreePercent => Total > 0 ? Free * 100.0 / Total : 0;
}

public sealed class SystemSummary
{
    public string OsName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string ComputerName { get; set; } = Environment.MachineName;
    public string Cpu { get; set; } = "";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public List<string> Gpus { get; set; } = [];
    public long RamTotal { get; set; }
    public long RamFree { get; set; }
    public string Motherboard { get; set; } = "";
    public string Bios { get; set; } = "";
    public TimeSpan Uptime { get; set; }
    public List<DriveSpace> Drives { get; set; } = [];
}

public static class SystemInfoService
{
    public static SystemSummary Collect()
    {
        var s = new SystemSummary();

        foreach (var os in Wmi.Query("SELECT Caption, Version, BuildNumber, TotalVisibleMemorySize, FreePhysicalMemory, LastBootUpTime FROM Win32_OperatingSystem"))
        {
            s.OsName = os.Str("Caption").Replace("Microsoft ", "");
            var display = Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion") as string;
            var ubr = Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
            s.OsVersion = $"{display} (build {os.Str("BuildNumber")}{(ubr is int u ? "." + u : "")})".Trim();
            s.RamTotal = os.Get<long>("TotalVisibleMemorySize") * 1024;
            s.RamFree = os.Get<long>("FreePhysicalMemory") * 1024;
        }
        s.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        foreach (var cpu in Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
        {
            s.Cpu = cpu.Str("Name");
            s.Cores += cpu.Get<int>("NumberOfCores");
            s.Threads += cpu.Get<int>("NumberOfLogicalProcessors");
        }

        s.Gpus = Wmi.Query("SELECT Name FROM Win32_VideoController")
            .Select(g => g.Str("Name"))
            .Where(n => n.Length > 0 && !n.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var b in Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            s.Motherboard = $"{b.Str("Manufacturer")} {b.Str("Product")}".Trim();
        foreach (var b in Wmi.Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            s.Bios = b.Str("SMBIOSBIOSVersion");

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                    s.Drives.Add(new DriveSpace(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace));
            }
            catch { /* drive not ready */ }
        }

        return s;
    }

    /// <summary>Does Windows have a pending restart (updates, servicing)?</summary>
    public static bool IsRebootPending()
    {
        return Reg.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
            || Reg.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
    }
}
