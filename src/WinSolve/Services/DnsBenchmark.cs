using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinSolve.Services;

/// <summary>How fast a DNS server answers (median of several lookups); <c>null</c> when it didn't answer.</summary>
public sealed record DnsSpeed(string Name, string Address, double? Ms, bool IsCurrent);

/// <summary>Times real DNS lookups against the DNS servers WinSolve offers and the ones in use now.</summary>
public static class DnsBenchmark
{
    private static readonly string[] Domains =
        ["google.com", "youtube.com", "facebook.com", "wikipedia.org", "microsoft.com", "amazon.com", "netflix.com", "instagram.com", "whatsapp.com"];

    /// <summary>The IPv4 DNS servers the active connection uses today (usually the router's).</summary>
    public static List<(string Name, string Address)> CurrentServers()
    {
        var result = new List<(string, string)>();
        var lan = RouterService.Detect();
        if (lan is null) return result;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var props = ni.GetIPProperties();
            if (!props.UnicastAddresses.Any(u => u.Address.Equals(lan.Local))) continue;
            foreach (var dns in props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Take(2))
                result.Add(("Current DNS", dns.ToString()));
        }
        return result;
    }

    public static async Task<List<DnsSpeed>> RunAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var servers = new List<(string Name, string Address, bool Current)>();
        foreach (var (name, address) in CurrentServers().Take(1)) servers.Add((name, address, true));
        foreach (var p in DnsService.Providers.Where(p => !p.IsDefault && !p.Name.Contains("block malware", StringComparison.OrdinalIgnoreCase)))
            if (!servers.Any(s => s.Address == p.IPv4[0])) servers.Add((p.Name, p.IPv4[0], false));

        var results = new List<DnsSpeed>();
        for (var i = 0; i < servers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (name, address, current) = servers[i];
            var ms = await MeasureAsync(IPAddress.Parse(address), ct);
            results.Add(new DnsSpeed(name, address, ms, current));
            progress?.Report((i + 1) * 100 / servers.Count);
        }
        return results.OrderBy(r => r.Ms ?? double.MaxValue).ToList();
    }

    /// <summary>Median time of the lookups after a first one that warms the path up; null when the server doesn't answer.</summary>
    private static async Task<double?> MeasureAsync(IPAddress server, CancellationToken ct)
    {
        var times = new List<double>();
        var failures = 0;
        for (var i = 0; i < Domains.Length && failures < 2; i++)
        {
            var ms = await QueryAsync(server, Domains[i], (ushort)(0x5300 + i), ct);
            if (ms is null) { failures++; continue; }
            if (i > 0) times.Add(ms.Value);   // the first lookup only opens the path
        }
        return Median(times);
    }

    internal static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static async Task<double?> QueryAsync(IPAddress server, string domain, ushort id, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Connect(server, 53);
            var query = BuildQuery(domain, id);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1500);
            var watch = Stopwatch.StartNew();
            await udp.SendAsync(query, timeout.Token);
            var reply = await udp.ReceiveAsync(timeout.Token);
            watch.Stop();
            return IsAnswer(reply.Buffer, id) ? watch.Elapsed.TotalMilliseconds : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>A standard DNS question for the A record of <paramref name="domain"/>.</summary>
    internal static byte[] BuildQuery(string domain, ushort id)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
        foreach (var label in domain.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        bytes.AddRange([0x00, 0x01, 0x00, 0x01]);   // type A, class IN
        return [.. bytes];
    }

    /// <summary>A reply to our question, without an error code.</summary>
    internal static bool IsAnswer(byte[] reply, ushort id)
        => reply.Length >= 12 && reply[0] == (byte)(id >> 8) && reply[1] == (byte)id && (reply[2] & 0x80) != 0 && (reply[3] & 0x0F) == 0;
}
