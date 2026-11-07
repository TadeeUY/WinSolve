using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>What a device calls itself, and what it says it is (manufacturer and model).</summary>
public sealed record DeviceIdentity(string Name, string Detail);

/// <summary>
/// Finds the names of the devices on the network. Routers rarely answer a normal DNS question for
/// their clients, so each device is also asked directly, the way each kind of device answers:
/// the DNS (some routers), mDNS (iPhone, Mac, printers, Chromecast...), NetBIOS (Windows PCs, NAS)
/// and UPnP (smart TVs, consoles, speakers). Only the local network is contacted.
/// </summary>
public static class DeviceNames
{
    public static async Task<Dictionary<string, DeviceIdentity>> ResolveAsync(LanInfo lan, IReadOnlyList<string> ips, CancellationToken ct = default)
    {
        var upnpTask = Task.Run(() => UpnpAsync(lan, ct), ct);
        using var gate = new SemaphoreSlim(24);
        var perHost = await Task.WhenAll(ips.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var dns = WithTimeout(ReverseDnsAsync(ip), 1500);
                var mdns = WithTimeout(MdnsAsync(ip, ct), 1300);
                var nb = WithTimeout(NetBiosAsync(ip, ct), 1300);
                await Task.WhenAll(dns, mdns, nb);
                return (Ip: ip, Name: dns.Result ?? mdns.Result ?? nb.Result);
            }
            finally { gate.Release(); }
        }));
        var upnp = await upnpTask;

        var result = new Dictionary<string, DeviceIdentity>();
        foreach (var (ip, name) in perHost)
        {
            upnp.TryGetValue(ip, out var info);
            var finalName = name ?? info?.Name ?? "";
            if (finalName.Length > 0 || info is not null) result[ip] = new DeviceIdentity(finalName, info?.Detail ?? "");
        }
        return result;
    }

    private static async Task<string?> WithTimeout(Task<string?> task, int ms)
    {
        var done = await Task.WhenAny(task, Task.Delay(ms));
        if (done != task) return null;
        try { return await task; }
        catch { return null; }
    }

    // ───────────── DNS ─────────────

    private static async Task<string?> ReverseDnsAsync(string ip)
    {
        try
        {
            var host = (await Dns.GetHostEntryAsync(ip)).HostName;
            return host == ip ? null : FirstLabel(host);
        }
        catch { return null; }
    }

    // ───────────── mDNS (a unicast question to port 5353) ─────────────

    private static async Task<string?> MdnsAsync(string ip, CancellationToken ct)
    {
        try
        {
            var address = IPAddress.Parse(ip);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(BuildPtrQuery(ip), new IPEndPoint(address, 5353), ct);
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(1100);
            while (true)
            {
                var reply = await udp.ReceiveAsync(window.Token);
                if (!reply.RemoteEndPoint.Address.Equals(address)) continue;
                if (ParsePtrAnswer(reply.Buffer) is { } name) return name;
            }
        }
        catch { return null; }
    }

    /// <summary>"What is the name of 192.168.1.20?" as an mDNS question that asks for a direct answer.</summary>
    internal static byte[] BuildPtrQuery(string ip)
    {
        var bytes = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        var name = string.Join(".", ip.Split('.').Reverse()) + ".in-addr.arpa";
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        bytes.AddRange(new byte[] { 0, 12, 0x80, 0x01 });   // PTR, class IN with the "answer me directly" bit
        return [.. bytes];
    }

    /// <summary>The device name inside a DNS/mDNS answer (the first PTR record).</summary>
    internal static string? ParsePtrAnswer(byte[] buf)
    {
        if (buf.Length < 12) return null;
        int Count(int at) => (buf[at] << 8) | buf[at + 1];
        var questions = Count(4);
        var records = Count(6) + Count(8) + Count(10);
        var pos = 12;
        for (var i = 0; i < questions; i++)
        {
            if (ReadName(buf, ref pos) is null) return null;
            pos += 4;
        }
        for (var i = 0; i < records; i++)
        {
            if (ReadName(buf, ref pos) is null || pos + 10 > buf.Length) return null;
            var type = (buf[pos] << 8) | buf[pos + 1];
            var length = (buf[pos + 8] << 8) | buf[pos + 9];
            var data = pos + 10;
            pos = data + length;
            if (type != 12) continue;
            var at = data;
            if (ReadName(buf, ref at) is { } name && FirstLabel(name) is { Length: > 0 } clean) return clean;
        }
        return null;
    }

    /// <summary>Reads a DNS name (with compression pointers) and moves <paramref name="pos"/> past it.</summary>
    internal static string? ReadName(byte[] buf, ref int pos)
    {
        var labels = new List<string>();
        var p = pos;
        var end = -1;
        for (var guard = 0; guard < 64; guard++)
        {
            if (p >= buf.Length) return null;
            int len = buf[p];
            if (len == 0)
            {
                pos = end >= 0 ? end : p + 1;
                return string.Join(".", labels);
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= buf.Length) return null;
                if (end < 0) end = p + 2;
                p = ((len & 0x3F) << 8) | buf[p + 1];
                continue;
            }
            if (p + 1 + len > buf.Length) return null;
            labels.Add(Encoding.UTF8.GetString(buf, p + 1, len));
            p += 1 + len;
        }
        return null;
    }

    /// <summary>"iPhone-de-Ana.local" and "PC-SALA.lan" are shown as "iPhone-de-Ana" and "PC-SALA".</summary>
    internal static string FirstLabel(string host)
    {
        var first = host.Trim().TrimEnd('.').Split('.')[0];
        return first.Length > 40 ? first[..40] : first;
    }

    // ───────────── NetBIOS ─────────────

    private static async Task<string?> NetBiosAsync(string ip, CancellationToken ct)
    {
        try
        {
            var address = IPAddress.Parse(ip);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(BuildNetBiosQuery(), new IPEndPoint(address, 137), ct);
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(1200);
            while (true)
            {
                var reply = await udp.ReceiveAsync(window.Token);
                if (!reply.RemoteEndPoint.Address.Equals(address)) continue;
                if (ParseNetBiosAnswer(reply.Buffer) is { } name) return name;
            }
        }
        catch { return null; }
    }

    /// <summary>NetBIOS "node status" question for the wildcard name: "what are all your names?".</summary>
    internal static byte[] BuildNetBiosQuery()
    {
        var bytes = new List<byte> { 0x13, 0x37, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x20 };
        bytes.AddRange(Encoding.ASCII.GetBytes("CK" + new string('A', 30)));   // "*" followed by zeros, in NetBIOS encoding
        bytes.Add(0);
        bytes.AddRange(new byte[] { 0, 0x21, 0, 1 });                          // NBSTAT, class IN
        return [.. bytes];
    }

    /// <summary>The computer name (suffix 00, not a group) from a node status answer.</summary>
    internal static string? ParseNetBiosAnswer(byte[] buf)
    {
        if (buf.Length < 57) return null;
        var pos = 12;
        if (ReadName(buf, ref pos) is null) return null;
        pos += 10;   // type, class, time to live, data length
        if (pos >= buf.Length) return null;
        int count = buf[pos++];
        for (var i = 0; i < count && pos + 18 <= buf.Length; i++, pos += 18)
        {
            var name = Encoding.ASCII.GetString(buf, pos, 15).Trim();
            int suffix = buf[pos + 15];
            var flags = (buf[pos + 16] << 8) | buf[pos + 17];
            if (suffix == 0 && (flags & 0x8000) == 0 && name.Length > 0) return name;
        }
        return null;
    }

    // ───────────── UPnP (TVs, consoles, speakers, printers) ─────────────

    private static async Task<Dictionary<string, DeviceIdentity>> UpnpAsync(LanInfo lan, CancellationToken ct)
    {
        var found = new Dictionary<string, DeviceIdentity>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(lan.Local, 0));
            var request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");
            await udp.SendAsync(request, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), ct);

            var locations = new Dictionary<string, Uri>();
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(2800);
            try
            {
                while (locations.Count < 60)
                {
                    var reply = await udp.ReceiveAsync(window.Token);
                    var from = reply.RemoteEndPoint.Address;
                    if (!RouterService.SameSubnet(from, lan.Local, lan.Mask) || locations.ContainsKey(from.ToString())) continue;
                    // Only the device's own description is read, never an address it points elsewhere.
                    if (RouterDetector.LocationOf(Encoding.ASCII.GetString(reply.Buffer), from) is { } uri) locations[from.ToString()] = uri;
                }
            }
            catch (OperationCanceledException) { /* the listening window ended */ }

            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(locations.Select(async pair =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var xml = await ReadLimitedAsync(http, pair.Value, ct);
                    if (xml is not null && ParseDescription(xml) is { } id) lock (found) found[pair.Key] = id;
                }
                catch { /* this device does not describe itself */ }
                finally { gate.Release(); }
            }));
        }
        catch { /* no multicast on this adapter */ }
        return found;
    }

    private static async Task<string?> ReadLimitedAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[96 * 1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>The friendly name, manufacturer and model of a UPnP device description.</summary>
    internal static DeviceIdentity? ParseDescription(string xml)
    {
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 500_000 });
            var device = XDocument.Load(reader).Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
            if (device is null) return null;
            string Field(string name) => device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
            var friendly = Field("friendlyName");
            var detail = $"{Field("manufacturer")} {Field("modelName")}".Trim();
            if (friendly.Length == 0 && detail.Length == 0) return null;
            return new DeviceIdentity(friendly.Length > 40 ? friendly[..40] : friendly, detail.Length > 60 ? detail[..60] : detail);
        }
        catch { return null; }
    }
}

/// <summary>The names the user gave to devices (by physical address), kept on this PC.</summary>
public static class DeviceBook
{
    private static string FilePath => Path.Combine(DataLocation.UserFolder, "device-names.json");

    public static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? [];
            return new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Saves a name for a device; an empty name forgets it.</summary>
    public static void Set(string mac, string name)
    {
        var map = Load();
        name = name.Trim();
        if (name.Length == 0) map.Remove(mac);
        else map[mac] = name.Length > 40 ? name[..40] : name;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
    }
}
