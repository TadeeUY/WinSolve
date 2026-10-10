using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinSolve.Core;

namespace WinSolve.Services;

// ═══════════════════════ Disk speed test ═══════════════════════

public sealed record BenchResult(double SeqReadMBs, double SeqWriteMBs, double RandReadMBs, double RandWriteMBs, double RandReadIops, double RandWriteIops);

/// <summary>
/// CrystalDiskMark-style test: sequential 1 MiB and random 4 KiB (queue depth 1) reads and
/// writes on a temporary file, bypassing the Windows cache so the drive itself is measured.
/// </summary>
public static class DiskBench
{
    private const int SeqBlock = 1 << 20;
    private const int RandBlock = 4096;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    public static async Task<BenchResult> RunAsync(string driveRoot, int sizeMb, Action<string> log, Action<int, int> progress, CancellationToken ct)
        => await Task.Run(() => Run(driveRoot, sizeMb, log, progress, ct), ct);

    private static unsafe BenchResult Run(string driveRoot, int sizeMb, Action<string> log, Action<int, int> progress, CancellationToken ct)
    {
        var free = new DriveInfo(driveRoot).AvailableFreeSpace;
        if (free < (long)sizeMb * 1024 * 1024 * 2)
            throw new InvalidOperationException($"Not enough free space on {driveRoot} for a {sizeMb} MB test.");

        var path = Path.Combine(driveRoot, $"WinSolveSpeedTest-{Guid.NewGuid():N}.tmp");
        var size = (long)sizeMb * 1024 * 1024;
        var buffer = (byte*)NativeMemory.AlignedAlloc(SeqBlock, 4096);
        try
        {
            RandomNumberGenerator.Fill(new Span<byte>(buffer, SeqBlock)); // incompressible data
            using var h = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                FileOptions.WriteThrough | FileOptions.DeleteOnClose | (FileOptions)FILE_FLAG_NO_BUFFERING, size);

            // 1) Sequential write
            log($"Sequential write ({sizeMb} MB)...");
            var sw = Stopwatch.StartNew();
            for (long off = 0; off < size; off += SeqBlock)
            {
                ct.ThrowIfCancellationRequested();
                RandomAccess.Write(h, new ReadOnlySpan<byte>(buffer, SeqBlock), off);
                progress((int)(off * 25 / size), 100);
            }
            var seqWrite = size / 1048576.0 / sw.Elapsed.TotalSeconds;

            // 2) Sequential read
            log($"Sequential read ({sizeMb} MB)...");
            sw.Restart();
            for (long off = 0; off < size; off += SeqBlock)
            {
                ct.ThrowIfCancellationRequested();
                RandomAccess.Read(h, new Span<byte>(buffer, SeqBlock), off);
                progress(25 + (int)(off * 25 / size), 100);
            }
            var seqRead = size / 1048576.0 / sw.Elapsed.TotalSeconds;

            // 3/4) Random 4 KiB, 5 seconds each
            var blocks = size / RandBlock;
            var rng = new Random(42);
            (double MBs, double Iops) Random4K(bool write, int baseProgress)
            {
                var ops = 0L;
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed.TotalSeconds < 5)
                {
                    ct.ThrowIfCancellationRequested();
                    var off = rng.NextInt64(blocks) * RandBlock;
                    if (write) RandomAccess.Write(h, new ReadOnlySpan<byte>(buffer, RandBlock), off);
                    else RandomAccess.Read(h, new Span<byte>(buffer, RandBlock), off);
                    ops++;
                    if ((ops & 255) == 0) progress(baseProgress + (int)(timer.Elapsed.TotalSeconds * 5), 100);
                }
                var iops = ops / timer.Elapsed.TotalSeconds;
                return (iops * RandBlock / 1048576.0, iops);
            }
            log("Random 4K read...");
            var rr = Random4K(write: false, 50);
            log("Random 4K write...");
            var rw = Random4K(write: true, 75);
            progress(100, 100);
            return new BenchResult(seqRead, seqWrite, rr.MBs, rw.MBs, rr.Iops, rw.Iops);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}

// ═══════════════════════ Locked files ═══════════════════════

public sealed record LockingProcess(int Pid, string Name, string? Path, bool IsService);

/// <summary>Finds which programs have a file open (Restart Manager, the API installers use).</summary>
public static class LockFinder
{
    public static List<LockingProcess> Find(IEnumerable<string> files)
    {
        var list = files.Take(5000).ToArray();
        var result = new List<LockingProcess>();
        if (list.Length == 0) return result;

        var key = new StringBuilder(64);
        if (RmStartSession(out var session, 0, key) != 0) throw new InvalidOperationException("Restart Manager is not available.");
        try
        {
            var err = RmRegisterResources(session, (uint)list.Length, list, 0, null, 0, null);
            if (err != 0) throw new System.ComponentModel.Win32Exception(err);

            uint needed = 0, count = 0, reasons = 0;
            var infos = Array.Empty<RM_PROCESS_INFO>();
            // The list can grow between the calls when another program opens the file meanwhile.
            for (int attempt = 0; ; attempt++)
            {
                count = (uint)infos.Length;
                err = RmGetList(session, out needed, ref count, infos.Length == 0 ? null : infos, ref reasons);
                if (err == 0) break;
                if (err != ERROR_MORE_DATA || attempt == 5) throw new System.ComponentModel.Win32Exception(err);
                infos = new RM_PROCESS_INFO[needed + 4];
            }

            for (int i = 0; i < count; i++)
            {
                var info = infos[i];
                string? exe = null;
                try
                {
                    using var p = Process.GetProcessById(info.Process.dwProcessId);
                    exe = p.MainModule?.FileName;
                }
                catch { }
                result.Add(new LockingProcess(info.Process.dwProcessId,
                    string.IsNullOrEmpty(info.strAppName) ? $"PID {info.Process.dwProcessId}" : info.strAppName,
                    exe, info.ApplicationType == 3 /* RmService */));
            }
        }
        finally
        {
            RmEndSession(session);
        }
        return result;
    }

    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint nFiles, string[] files, uint nApps, RM_UNIQUE_PROCESS[]? apps, uint nServices, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? info, ref uint rebootReasons);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string? newName, int flags);

    /// <summary>Deletes the file at the next restart, before any program can open it.</summary>
    public static bool DeleteOnRestart(string path) => MoveFileEx(path, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
}

// ═══════════════════════ Secure delete ═══════════════════════

/// <summary>Overwrites files with random data before deleting them.</summary>
public static class SecureDelete
{
    /// <summary>Refuses system and program folders; returns how many files were shredded.</summary>
    public static int Shred(IReadOnlyList<string> paths, Action<string> log, Action<int, int> progress, CancellationToken ct)
    {
        var files = new List<string>();
        var dirs = new List<string>();
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);
            if (Directory.Exists(full))
            {
                if (SafePath.IsProtectedFolder(full) || InsideProgramOrSystemFolder(full) || SafePath.HasReparsePoint(full))
                {
                    log($"Skipped {full}: protected location or link.");
                    continue;
                }
                files.AddRange(Directory.EnumerateFiles(full, "*", SafePath.NoLinks(recursive: true)));
                dirs.Add(full);
            }
            else if (File.Exists(full))
            {
                var parent = Path.GetDirectoryName(full)!;
                if ((SafePath.IsProtectedFolder(parent) && !SafePath.IsSameOrInside(parent, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
                    || InsideProgramOrSystemFolder(full))
                {
                    log($"Skipped {full}: inside a system or program folder.");
                    continue;
                }
                files.Add(full);
            }
        }

        var done = 0;
        var failed = new List<string>();
        var buffer = new byte[1 << 20];
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];
            try
            {
                File.SetAttributes(f, FileAttributes.Normal);
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
                {
                    var remaining = fs.Length;
                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        RandomNumberGenerator.Fill(buffer);
                        var n = (int)Math.Min(buffer.Length, remaining);
                        fs.Write(buffer, 0, n);
                        remaining -= n;
                    }
                    fs.Flush(flushToDisk: true);
                    fs.SetLength(0);
                }
                // A random name too, so the old file name doesn't stay in the folder index.
                var renamed = Path.Combine(Path.GetDirectoryName(f)!, Guid.NewGuid().ToString("N"));
                File.Move(f, renamed);
                File.Delete(renamed);
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Could not shred {f}: {ex.Message}");
                failed.Add(f);
            }
            progress(i + 1, files.Count);
        }
        foreach (var d in dirs)
        {
            // A folder with a file that couldn't be overwritten stays: deleting it would remove that
            // file without wiping it, and it could be recovered.
            if (failed.Any(f => SafePath.IsSameOrInside(f, d)))
            {
                log($"Kept {d}: some files in it could not be shredded.");
                continue;
            }
            try { Directory.Delete(d, recursive: true); } catch { }
        }
        return done;
    }

    /// <summary>Program Files, ProgramData and Windows: shredding there breaks programs or Windows.</summary>
    private static bool InsideProgramOrSystemFolder(string path)
    {
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Windows,
        })
        {
            var f = Environment.GetFolderPath(folder);
            if (f.Length > 0 && SafePath.IsSameOrInside(path, f)) return true;
        }
        return false;
    }
}

// ═══════════════════════ Network ═══════════════════════

public sealed record PingStat(string Target, string Label, double? AvgMs, double? JitterMs, int LossPercent);

public sealed record AdapterInfo(string Name, string Type, string IPv4, string Gateway, string Dns, string Speed);

public static class NetworkTest
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static List<AdapterInfo> Adapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n =>
            {
                var p = n.GetIPProperties();
                var v4 = p.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                var gw = p.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return new AdapterInfo(n.Name, n.NetworkInterfaceType.ToString(), v4?.Address.ToString() ?? "—", gw?.Address.ToString() ?? "—",
                    string.Join(", ", p.DnsAddresses.Where(d => d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Take(2)),
                    n.Speed > 0 ? $"{n.Speed / 1_000_000} Mbps" : "—");
            })
            .OrderBy(a => a.Gateway == "—")
            .ToList();

    public static async Task<PingStat> PingAsync(string target, string label, int count, CancellationToken ct)
    {
        using var ping = new Ping();
        var times = new List<long>();
        var lost = 0;
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await ping.SendPingAsync(target, 2000);
                if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime); else lost++;
            }
            catch { lost++; }
            await Task.Delay(150, ct);
        }
        if (times.Count == 0) return new PingStat(target, label, null, null, 100);
        var jitter = times.Count > 1 ? times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
        return new PingStat(target, label, times.Average(), jitter, lost * 100 / count);
    }

    /// <summary>Public IP and location as seen by Cloudflare.</summary>
    public static async Task<(string Ip, string Country)> PublicIpAsync(CancellationToken ct)
    {
        var text = await Http.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", ct);
        string Field(string name) => text.Split('\n').FirstOrDefault(l => l.StartsWith(name + "="))?[(name.Length + 1)..].Trim() ?? "—";
        return (Field("ip"), Field("loc"));
    }

    /// <summary>Download speed in Mbit/s (Cloudflare's speed test endpoint).</summary>
    public static async Task<double> DownloadMbpsAsync(CancellationToken ct)
    {
        const int bytes = 25_000_000;
        var sw = Stopwatch.StartNew();
        using var response = await Http.GetAsync($"https://speed.cloudflare.com/__down?bytes={bytes}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var s = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[1 << 16];
        long total = 0;
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0) total += n;
        return total * 8 / 1_000_000.0 / sw.Elapsed.TotalSeconds;
    }

    /// <summary>Upload speed in Mbit/s.</summary>
    /// <param name="downloadMbps">Sizes the upload so slow connections finish in a few seconds too.</param>
    public static async Task<double> UploadMbpsAsync(double downloadMbps, CancellationToken ct)
    {
        // Uploads are usually slower than downloads: aim for ~8 s at a quarter of the download speed.
        var size = (int)Math.Clamp(downloadMbps / 4 * 1_000_000 / 8 * 8, 1_000_000, 10_000_000);
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);
        var sw = Stopwatch.StartNew();
        using var content = new ByteArrayContent(data);
        using var response = await Http.PostAsync("https://speed.cloudflare.com/__up", content, ct);
        response.EnsureSuccessStatusCode();
        return data.Length * 8 / 1_000_000.0 / sw.Elapsed.TotalSeconds;
    }
}

// ═══════════════════════ Wi-Fi passwords ═══════════════════════

public sealed record WifiProfile(string Name, string Security, string? Password);

/// <summary>Saved Wi-Fi networks of this PC and their keys (Native Wi-Fi API, language independent).</summary>
public static class WifiPasswords
{
    public static List<WifiProfile> Read()
    {
        var result = new List<WifiProfile>();
        var err = WlanOpenHandle(2, IntPtr.Zero, out _, out var client);
        if (err != 0) throw new InvalidOperationException(err == 1062 ? "The WLAN AutoConfig service isn't running (this PC may not have Wi-Fi)." : $"Wi-Fi API error {err}.");
        try
        {
            if (WlanEnumInterfaces(client, IntPtr.Zero, out var ifList) != 0) return result;
            try
            {
                var count = Marshal.ReadInt32(ifList, 0);
                for (int i = 0; i < count; i++)
                {
                    // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] (512) + state (4) = 532 bytes, list header 8.
                    var guid = Marshal.PtrToStructure<Guid>(ifList + 8 + i * 532);
                    if (WlanGetProfileList(client, ref guid, IntPtr.Zero, out var profiles) != 0) continue;
                    try
                    {
                        var pCount = Marshal.ReadInt32(profiles, 0);
                        for (int j = 0; j < pCount; j++)
                        {
                            // WLAN_PROFILE_INFO: WCHAR[256] (512) + flags (4) = 516 bytes.
                            var name = Marshal.PtrToStringUni(profiles + 8 + j * 516)!;
                            uint flags = WLAN_PROFILE_GET_PLAINTEXT_KEY;
                            if (WlanGetProfile(client, ref guid, name, IntPtr.Zero, out var xmlPtr, ref flags, out _) != 0) continue;
                            try
                            {
                                var xml = Marshal.PtrToStringUni(xmlPtr)!;
                                result.Add(Parse(name, xml));
                            }
                            finally
                            {
                                WlanFreeMemory(xmlPtr);
                            }
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(profiles);
                    }
                }
            }
            finally
            {
                WlanFreeMemory(ifList);
            }
        }
        finally
        {
            WlanCloseHandle(client, IntPtr.Zero);
        }
        return result.DistinctBy(p => p.Name).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static WifiProfile Parse(string name, string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = doc.Root!.Name.Namespace;
            var auth = doc.Descendants(ns + "authentication").FirstOrDefault()?.Value ?? "";
            var shared = doc.Descendants(ns + "sharedKey").FirstOrDefault();
            var isProtected = shared?.Element(ns + "protected")?.Value == "true";
            var key = isProtected ? null : shared?.Element(ns + "keyMaterial")?.Value;
            var security = auth switch
            {
                "open" => "Open (no password)",
                "WPA2PSK" => "WPA2-Personal",
                "WPA3SAE" => "WPA3-Personal",
                "WPAPSK" => "WPA-Personal",
                "WPA2" or "WPA" or "WPA3" or "WPA3ENT192" => "Enterprise",
                _ => auth,
            };
            return new WifiProfile(name, security, key);
        }
        catch
        {
            return new WifiProfile(name, "?", null);
        }
    }

    private const uint WLAN_PROFILE_GET_PLAINTEXT_KEY = 4;

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WlanGetProfile(IntPtr handle, ref Guid iface, string profile, IntPtr reserved, out IntPtr xml, ref uint flags, out uint access);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}

// ═══════════════════════ Hosts file ═══════════════════════

public static class HostsFile
{
    public static string PathName => System.IO.Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    public const string WindowsDefault = """
        # Copyright (c) 1993-2009 Microsoft Corp.
        #
        # This is a sample HOSTS file used by Microsoft TCP/IP for Windows.
        #
        # This file contains the mappings of IP addresses to host names. Each
        # entry should be kept on an individual line. The IP address should
        # be placed in the first column followed by the corresponding host name.
        # The IP address and the host name should be separated by at least one
        # space.
        #
        # localhost name resolution is handled within DNS itself.
        #	127.0.0.1       localhost
        #	::1             localhost

        """;

    public static string Read() => File.Exists(PathName) ? File.ReadAllText(PathName) : "";

    /// <summary>Saves (keeping a timestamped backup next to it) and flushes the DNS cache.</summary>
    public static async Task<string> SaveAsync(string text)
    {
        var backup = PathName + $".winsolve-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak";
        if (File.Exists(PathName) && !File.Exists(backup)) File.Copy(PathName, backup, overwrite: false);
        var tmp = PathName + ".winsolve-new";
        await File.WriteAllTextAsync(tmp, text.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
        File.Move(tmp, PathName, overwrite: true);
        await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns");
        return backup;
    }

    /// <summary>Lines that block a site (and its www. variant).</summary>
    public static string BlockLines(string site)
    {
        var host = site.Trim().ToLowerInvariant();
        if (Uri.TryCreate(host.Contains("://") ? host : "http://" + host, UriKind.Absolute, out var uri)) host = uri.Host;
        if (Uri.CheckHostName(host) != UriHostNameType.Dns) throw new ArgumentException("That doesn't look like a website address.");
        var bare = host.StartsWith("www.") ? host[4..] : host;
        return $"0.0.0.0 {bare}\r\n0.0.0.0 www.{bare}\r\n";
    }
}

// ═══════════════════════ Context menu entries ═══════════════════════

public sealed class ContextMenuEntry
{
    public required string Name { get; init; }
    public required string Where { get; init; }
    public required string Source { get; init; }
    public required bool IsHandler { get; init; }
    public required string RegistryPath { get; init; }
    public string? Clsid { get; init; }
    public RegistryHive Hive { get; init; } = RegistryHive.LocalMachine;
    public bool Enabled { get; set; }
}

/// <summary>
/// Third-party entries of the classic right-click menu ("Show more options" on Windows 11).
/// Commands are hidden with LegacyDisable; shell extensions are blocked by CLSID. Both undo cleanly.
/// </summary>
public static class ContextMenuItems
{
    private const string BlockedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private static readonly (string Root, string Label)[] Roots =
    [
        ("*", "Files"), ("AllFilesystemObjects", "Files and folders"), ("Directory", "Folders"),
        ("Directory\\Background", "Folder background"), ("Folder", "Folders"), ("Drive", "Drives"),
    ];

    private static readonly HashSet<string> BuiltInVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennewwindow", "opennewprocess", "opennewtab", "edit", "print", "printto", "runas", "runasuser", "find", "explore",
        "cmd", "Powershell", "pintohome", "pintostartscreen", "properties", "UpdateEncryptionSettings", "UpdateEncryptionSettingsWork",
        "change-passphrase", "change-pin", "encrypt-bde", "encrypt-bde-elev", "manage-bde", "resume-bde", "resume-bde-elev", "unlock-bde",
        "format", "share", "copyaspath", "WSL", "TakeOwnership", "Windows.ModernShare",
    };

    // The signed-in user's own classes (per-user installs such as VS Code) and the machine's.
    // Not HKEY_CLASSES_ROOT: when WinSolve was elevated with another account it merges that
    // administrator's classes instead of the user's.
    private static RegistryKey? OpenClasses(RegistryHive hive, bool writable = false)
    {
        if (hive == RegistryHive.LocalMachine) return Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes", writable);
        using var user = Reg.Root(RegistryHive.CurrentUser);
        return user.OpenSubKey(@"Software\Classes", writable);
    }

    private static readonly RegistryHive[] Hives = [RegistryHive.CurrentUser, RegistryHive.LocalMachine];

    public static List<ContextMenuEntry> List()
    {
        var entries = new List<ContextMenuEntry>();
        var blocked = BlockedSet();
        foreach (var hive in Hives)
        {
            using var classes = OpenClasses(hive);
            if (classes is null) continue;
            foreach (var (root, label) in Roots)
            {
                using var shell = classes.OpenSubKey($@"{root}\shell");
                if (shell is not null)
                {
                    foreach (var verb in shell.GetSubKeyNames())
                    {
                        if (BuiltInVerbs.Contains(verb)) continue;
                        using var k = shell.OpenSubKey(verb);
                        if (k is null) continue;
                        var name = ResolveName(k.GetValue("MUIVerb") as string ?? k.GetValue("") as string) ?? verb;
                        using var commandKey = k.OpenSubKey("command");
                        var command = commandKey?.GetValue("") as string ?? "";
                        if (command.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) && !command.Contains("Program", StringComparison.OrdinalIgnoreCase)) continue;
                        entries.Add(new ContextMenuEntry
                        {
                            Name = name.Replace("&", ""), Where = label, Source = ProgramOf(command) ?? verb, IsHandler = false,
                            RegistryPath = $@"{root}\shell\{verb}", Hive = hive, Enabled = k.GetValue("LegacyDisable") is null,
                        });
                    }
                }

                using var handlers = classes.OpenSubKey($@"{root}\shellex\ContextMenuHandlers");
                if (handlers is null) continue;
                foreach (var h in handlers.GetSubKeyNames())
                {
                    using var hk = handlers.OpenSubKey(h);
                    var clsid = (h.StartsWith('{') ? h : hk?.GetValue("") as string)?.Trim();
                    if (string.IsNullOrEmpty(clsid) || !clsid.StartsWith('{')) continue;
                    var (clsName, dll) = DescribeClsid(clsid);
                    if (dll.Length == 0 || dll.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;
                    entries.Add(new ContextMenuEntry
                    {
                        Name = clsName is { Length: > 0 } n ? n : h, Where = label, Source = ProgramOf(dll) ?? Path.GetFileName(dll),
                        IsHandler = true, RegistryPath = $@"{root}\shellex\ContextMenuHandlers\{h}", Hive = hive, Clsid = clsid,
                        Enabled = !blocked.Contains(clsid),
                    });
                }
            }
        }
        return entries.DistinctBy(e => e.Clsid ?? e.Hive + e.RegistryPath).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Name and DLL of a shell extension; the user's registration wins, like in Windows.</summary>
    private static (string? Name, string Dll) DescribeClsid(string clsid)
    {
        foreach (var hive in Hives)
        {
            using var classes = OpenClasses(hive);
            using var cls = classes?.OpenSubKey($@"CLSID\{clsid}");
            if (cls is null) continue;
            using var server = cls.OpenSubKey("InprocServer32");
            var dll = Environment.ExpandEnvironmentVariables(server?.GetValue("") as string ?? "");
            return (cls.GetValue("") as string, dll);
        }
        return (null, "");
    }

    public static void SetEnabled(ContextMenuEntry e, bool enabled)
    {
        if (e.IsHandler && e.Clsid is { } clsid)
        {
            using var key = Registry.LocalMachine.CreateSubKey(BlockedKey);
            if (enabled) key.DeleteValue(clsid, throwOnMissingValue: false);
            else key.SetValue(clsid, "WinSolve", RegistryValueKind.String);
        }
        else
        {
            using var classes = OpenClasses(e.Hive, writable: true);
            using var key = classes?.OpenSubKey(e.RegistryPath, writable: true) ?? throw new InvalidOperationException("The entry no longer exists.");
            if (enabled) key.DeleteValue("LegacyDisable", throwOnMissingValue: false);
            else key.SetValue("LegacyDisable", "", RegistryValueKind.String);
        }
        e.Enabled = enabled;
    }

    private static HashSet<string> BlockedSet()
    {
        using var key = Registry.LocalMachine.OpenSubKey(BlockedKey);
        return new HashSet<string>(key?.GetValueNames() ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private static string? ProgramOf(string commandOrPath)
    {
        var s = commandOrPath.Trim();
        if (s.StartsWith('"')) s = s[1..s.IndexOf('"', 1).Clamp(1, s.Length)];
        else if (s.Contains(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..(s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4)];
        var dir = Path.GetDirectoryName(s);
        return string.IsNullOrEmpty(dir) ? null : Path.GetFileName(dir);
    }

    private static string? ResolveName(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!value.StartsWith('@')) return value;
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(value, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    private static int Clamp(this int v, int min, int max) => Math.Max(min, Math.Min(max, v));

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);
}

// ═══════════════════════ Restore points ═══════════════════════

public sealed record RestorePoint(int Sequence, string Description, DateTime Created, string Type);

public static class RestorePoints
{
    public static List<RestorePoint> List()
        => Wmi.Query("SELECT SequenceNumber, Description, CreationTime, RestorePointType FROM SystemRestore", @"root\default")
            .Select(r => new RestorePoint(
                (int)r.Get<uint>("SequenceNumber"),
                r.Str("Description"),
                ParseDmtf(r.Str("CreationTime")),
                r.Get<uint>("RestorePointType") switch
                {
                    0 => "App installed", 1 => "App removed", 10 => "Driver installed", 12 => "Settings changed", 13 => "Cancelled",
                    _ => "Restore point",
                }))
            .OrderByDescending(p => p.Created)
            .ToList();

    public static bool Delete(int sequence) => SRRemoveRestorePoint(sequence) == 0;

    private static DateTime ParseDmtf(string s)
    {
        try { return System.Management.ManagementDateTimeConverter.ToDateTime(s); }
        catch { return DateTime.MinValue; }
    }

    [DllImport("srclient.dll")]
    private static extern int SRRemoveRestorePoint(int sequence);
}
