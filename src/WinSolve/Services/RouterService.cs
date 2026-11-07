using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>The active network adapter and its router (default gateway).</summary>
public sealed record LanInfo(string Adapter, IPAddress Local, IPAddress Mask, IPAddress Gateway, string Mac);

/// <summary>A device seen on the local network.</summary>
public sealed record LanDevice(string Ip, string Mac, string Name, string Note, bool IsRouter, bool IsThisPc);

/// <summary>Result of the "is my Internet working?" check.</summary>
public sealed record InternetCheckResult(long? RouterMs, long? InternetMs, bool DnsWorks)
{
    public bool RouterReachable => RouterMs is not null;
    public bool InternetReachable => InternetMs is not null;

    /// <summary>Plain-language diagnosis (English source text; the UI translates it).</summary>
    public string Verdict =>
        !RouterReachable ? "This PC can't reach the router. Check the cable or the Wi-Fi connection, then restart the router."
        : !InternetReachable ? "The router answers but there is no Internet. The problem is outside this PC: restart the router and, if it continues, call your Internet provider."
        : !DnsWorks ? "Internet works but names don't resolve (DNS). Try another DNS in Tweaks, or flush the DNS cache."
        : InternetMs > 150 ? "Internet works but it is slow to answer. Wi-Fi interference or a busy connection are the usual causes."
        : "Everything works: the router, the Internet and the DNS.";
}

/// <summary>Finds the router and the devices connected to the same network.</summary>
public static class RouterService
{
    /// <summary>The adapter that is up and has a default gateway (the one used to reach the Internet).</summary>
    public static LanInfo? Detect()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            var gateway = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            var unicast = props.UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (gateway is null || unicast is null) continue;
            return new LanInfo(ni.Name, unicast.Address, unicast.IPv4Mask ?? IPAddress.Parse("255.255.255.0"), gateway,
                FormatMac(ni.GetPhysicalAddress().GetAddressBytes()));
        }
        return null;
    }

    /// <summary>Home and office routers live on private addresses; the saved password is never sent anywhere else.</summary>
    public static bool IsPrivateAddress(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] is >= 64 and <= 127); // carrier-grade NAT modems
    }

    /// <summary>Parses what the user typed ("192.168.1.1", "http://192.168.0.1:8080/") into a router address; null if it isn't a private LAN address.</summary>
    public static bool TryParseAddress(string? text, out Uri uri)
    {
        uri = null!;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        if (!t.Contains("://", StringComparison.Ordinal)) t = "http://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https")) return false;
        if (!IPAddress.TryParse(parsed.Host, out var ip) || !IsPrivateAddress(ip)) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;
        uri = parsed;
        return true;
    }

    // ───────────── Internet check ─────────────

    public static async Task<InternetCheckResult> CheckInternetAsync(IPAddress? gateway, CancellationToken ct = default)
    {
        var router = gateway is null ? null : await PingAsync(gateway.ToString(), 1500);
        long? internet = null;
        foreach (var host in new[] { "1.1.1.1", "8.8.8.8" })
        {
            ct.ThrowIfCancellationRequested();
            internet = await PingAsync(host, 2000);
            if (internet is not null) break;
        }
        var dns = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(3000);
            var entry = await Dns.GetHostEntryAsync("www.microsoft.com", timeout.Token);
            dns = entry.AddressList.Length > 0;
        }
        catch { /* no DNS */ }
        return new InternetCheckResult(router, internet, dns);
    }

    private static async Task<long?> PingAsync(string host, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs);
            return reply.Status == IPStatus.Success ? Math.Max(1, reply.RoundtripTime) : null;
        }
        catch { return null; }
    }

    // ───────────── Devices ─────────────

    /// <summary>Lists the devices on the same network: wakes the /24 with pings, then reads the ARP table.</summary>
    public static async Task<List<LanDevice>> ScanAsync(LanInfo lan, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var local = lan.Local.GetAddressBytes();
        // Never sweep more than a /24 (a larger network would take minutes and look like a port scan).
        var targets = Enumerable.Range(1, 254).Select(i => new IPAddress(new byte[] { local[0], local[1], local[2], (byte)i })).ToList();

        var done = 0;
        using var gate = new SemaphoreSlim(48);
        await Task.WhenAll(targets.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try { await PingAsync(ip.ToString(), 350); }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done) * 100 / targets.Count);
            }
        }));

        var arp = await ProcessRunner.RunAsync("arp.exe", "-a", ct: ct);
        var seen = ParseArp(arp.Output, lan.Local, lan.Mask);
        if (!seen.Any(s => s.Ip == lan.Local.ToString())) seen.Add((lan.Local.ToString(), lan.Mac));

        var devices = await Task.WhenAll(seen.Select(async s =>
        {
            var isMe = s.Ip == lan.Local.ToString();
            var name = await ResolveNameAsync(s.Ip, isMe);
            return new LanDevice(s.Ip, s.Mac, name, Describe(s.Mac), s.Ip == lan.Gateway.ToString(), isMe);
        }));
        return devices.OrderBy(d => d.IsRouter ? 0 : d.IsThisPc ? 1 : 2).ThenBy(d => AddressKey(d.Ip)).ToList();
    }

    private static async Task<string> ResolveNameAsync(string ip, bool isMe)
    {
        if (isMe) return Environment.MachineName;
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(1500)) != lookup) return "";
            var host = (await lookup).HostName;
            return host == ip ? "" : host;
        }
        catch { return ""; }
    }

    private static long AddressKey(string ip) => IPAddress.TryParse(ip, out var a)
        ? a.GetAddressBytes().Aggregate(0L, (acc, b) => acc * 256 + b) : 0;

    private static readonly Regex ArpLine = new(
        @"^\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<mac>[0-9a-fA-F]{2}(?:[-:][0-9a-fA-F]{2}){5})\b", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Reads <c>arp -a</c> output (same in every Windows language): devices on this subnet, without broadcast or multicast entries.</summary>
    internal static List<(string Ip, string Mac)> ParseArp(string output, IPAddress local, IPAddress mask)
    {
        var result = new List<(string Ip, string Mac)>();
        var seen = new HashSet<string>();
        foreach (Match m in ArpLine.Matches(output))
        {
            if (!IPAddress.TryParse(m.Groups["ip"].Value, out var ip)) continue;
            if (!SameSubnet(ip, local, mask)) continue;
            var bytes = ip.GetAddressBytes();
            if (bytes[3] is 0 or 255) continue;
            var mac = FormatMac(m.Groups["mac"].Value);
            if (mac == "FF:FF:FF:FF:FF:FF" || mac.StartsWith("01:00:5E", StringComparison.Ordinal)) continue;
            if (seen.Add(ip.ToString())) result.Add((ip.ToString(), mac));
        }
        return result;
    }

    internal static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (var i = 0; i < 4; i++)
            if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }

    /// <summary>"aa-bb-cc-dd-ee-ff" or bytes to "AA:BB:CC:DD:EE:FF".</summary>
    internal static string FormatMac(string mac)
        => string.Join(":", mac.Split('-', ':').Select(p => p.PadLeft(2, '0').ToUpperInvariant()));

    internal static string FormatMac(byte[] bytes) => string.Join(":", bytes.Select(b => b.ToString("X2")));

    /// <summary>What the address tells about the device, without a manufacturer database to keep up to date.</summary>
    internal static string Describe(string mac)
    {
        var first = mac.Length >= 2 && byte.TryParse(mac.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var b) ? b : (byte)0;
        // Bit 1 of the first byte set: made-up address. Phones and tablets do this on purpose for privacy.
        if ((first & 0x02) != 0) return "Private address (phone, tablet or virtual machine)";
        var oui = mac.Length >= 8 ? mac[..8] : mac;
        return oui switch
        {
            "B8:27:EB" or "DC:A6:32" or "E4:5F:01" => "Raspberry Pi",
            "00:0C:29" or "00:50:56" => "VMware virtual machine",
            "08:00:27" => "VirtualBox virtual machine",
            "00:15:5D" => "Hyper-V virtual machine",
            _ => "",
        };
    }
}

/// <summary>The router's address and sign-in, saved with the signed-in user's Windows encryption (DPAPI).</summary>
public sealed record RouterLogin(string Address, string User, string Password);

public static class RouterVault
{
    private sealed class Stored
    {
        public string Address { get; set; } = "";
        public string User { get; set; } = "";
        public string? Password { get; set; }
    }

    public static string FilePath => Path.Combine(DataLocation.UserFolder, "router.json");

    public static RouterLogin? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
            if (stored is null || stored.Address.Length == 0) return null;
            return new RouterLogin(stored.Address, stored.User, stored.Password is { Length: > 0 } p ? Unprotect(p) : "");
        }
        catch { return null; }
    }

    public static void Save(RouterLogin login)
    {
        var stored = new Stored
        {
            Address = login.Address,
            User = login.User,
            Password = login.Password.Length == 0 ? null : Protect(login.Password),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored));
    }

    public static void Forget()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* in use */ }
    }

    // ───────────── DPAPI (crypt32) ─────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    internal static string Protect(string text)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(Transform(plain, protect: true));
    }

    internal static string Unprotect(string base64)
        => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(base64), protect: false));

    private static byte[] Transform(byte[] input, bool protect)
    {
        var handle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { cbData = input.Length, pbData = handle.AddrOfPinnedObject() };
            DataBlob outBlob;
            var ok = protect
                ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out outBlob);
            if (!ok) throw new InvalidOperationException("Windows could not " + (protect ? "encrypt" : "decrypt") + " the saved password.");
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { handle.Free(); }
    }
}
