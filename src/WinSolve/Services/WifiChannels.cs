using System.Runtime.InteropServices;

namespace WinSolve.Services;

public enum WifiBand { Ghz24, Ghz5, Ghz6 }

/// <summary>One Wi-Fi access point heard by this PC.</summary>
public sealed record WifiAp(string Ssid, string Bssid, int Channel, int FrequencyMhz, int Rssi, WifiBand Band);

/// <summary>What the Wi-Fi card heard, and the access point this PC is connected to (if any).</summary>
public sealed record WifiScan(List<WifiAp> Aps, string? ConnectedBssid);

/// <summary>How crowded a channel is.</summary>
public sealed record ChannelLoad(int Channel, double Load, int Networks, bool IsCurrent, bool IsBest);

/// <summary>Reads the nearby Wi-Fi networks with the Windows Native Wi-Fi API (same in every language).</summary>
public static class WifiScanner
{
    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr client);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr client, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanScan(IntPtr client, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanGetNetworkBssList(IntPtr client, ref Guid iface, IntPtr ssid, int bssType, int securityEnabled, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr client, ref Guid iface, int opCode, IntPtr reserved, out int dataSize, out IntPtr data, IntPtr opcodeValueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

    private const int InterfaceInfoSize = 532;   // GUID (16) + WCHAR[256] (512) + state (4) = 532
    private const int BssEntrySize = 360;        // WLAN_BSS_ENTRY on 64-bit Windows
    private const int CurrentConnection = 7;     // wlan_intf_opcode_current_connection

    public static async Task<WifiScan> ScanAsync(CancellationToken ct = default)
    {
        var err = WlanOpenHandle(2, IntPtr.Zero, out _, out var client);
        if (err != 0) throw new InvalidOperationException(err == 1062 ? "The WLAN AutoConfig service isn't running (this PC may not have Wi-Fi)." : $"Wi-Fi API error {err}.");
        try
        {
            if (WlanEnumInterfaces(client, IntPtr.Zero, out var list) != 0) throw new InvalidOperationException("This PC has no Wi-Fi adapter.");
            var guids = new List<Guid>();
            try
            {
                var count = Marshal.ReadInt32(list, 0);
                for (var i = 0; i < count; i++) guids.Add(Marshal.PtrToStructure<Guid>(list + 8 + i * InterfaceInfoSize));
            }
            finally { WlanFreeMemory(list); }
            if (guids.Count == 0) throw new InvalidOperationException("This PC has no Wi-Fi adapter.");

            // Ask each card to listen again, then give it a moment: a scan takes a few seconds.
            foreach (var g in guids)
            {
                var guid = g;
                WlanScan(client, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            await Task.Delay(4000, ct);

            var aps = new List<WifiAp>();
            string? connected = null;
            foreach (var g in guids)
            {
                var guid = g;
                connected ??= ConnectedBssid(client, ref guid);
                if (WlanGetNetworkBssList(client, ref guid, IntPtr.Zero, 3, 0, IntPtr.Zero, out var bss) != 0) continue;
                try { aps.AddRange(ReadBssList(bss)); }
                finally { WlanFreeMemory(bss); }
            }
            return new WifiScan(aps, connected);
        }
        finally { WlanCloseHandle(client, IntPtr.Zero); }
    }

    private static string? ConnectedBssid(IntPtr client, ref Guid guid)
    {
        if (WlanQueryInterface(client, ref guid, CurrentConnection, IntPtr.Zero, out var size, out var data, IntPtr.Zero) != 0) return null;
        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: state (4) + mode (4) + profile name (512), then the association:
            // SSID (36) + BSS type (4) + BSSID (6).
            const int bssidOffset = 8 + 512 + 36 + 4;
            if (size < bssidOffset + 6) return null;
            var mac = new byte[6];
            Marshal.Copy(data + bssidOffset, mac, 0, 6);
            return RouterService.FormatMac(mac);
        }
        finally { WlanFreeMemory(data); }
    }

    private static IEnumerable<WifiAp> ReadBssList(IntPtr list)
    {
        // WLAN_BSS_LIST: total size (4), count (4), then the entries.
        var count = Marshal.ReadInt32(list, 4);
        for (var i = 0; i < count && i < 512; i++)
        {
            var entry = list + 8 + i * BssEntrySize;
            var ssidLength = Math.Min(Marshal.ReadInt32(entry, 0), 32);
            var ssid = ssidLength > 0 ? System.Text.Encoding.UTF8.GetString(ReadBytes(entry + 4, ssidLength)) : "";
            var mac = ReadBytes(entry + 40, 6);
            var rssi = Marshal.ReadInt32(entry, 56);
            var mhz = Marshal.ReadInt32(entry, 92) / 1000;   // center frequency, in kHz
            var channel = ChannelPlanner.ChannelOf(mhz);
            if (channel <= 0) continue;                       // not a Wi-Fi frequency: ignore the entry
            yield return new WifiAp(ssid, RouterService.FormatMac(mac), channel, mhz, rssi, ChannelPlanner.BandOf(mhz));
        }
    }

    private static byte[] ReadBytes(IntPtr at, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(at, bytes, 0, length);
        return bytes;
    }
}

/// <summary>Works out how crowded each Wi-Fi channel is and which one to move to.</summary>
public static class ChannelPlanner
{
    /// <summary>Channel number of a center frequency in MHz; 0 when it isn't a Wi-Fi channel.</summary>
    public static int ChannelOf(int mhz) => mhz switch
    {
        2484 => 14,
        >= 2412 and <= 2472 => (mhz - 2407) / 5,
        >= 5160 and <= 5885 => (mhz - 5000) / 5,
        >= 5955 and <= 7115 => (mhz - 5950) / 5,
        _ => 0,
    };

    public static WifiBand BandOf(int mhz) => mhz < 3000 ? WifiBand.Ghz24 : mhz < 5900 ? WifiBand.Ghz5 : WifiBand.Ghz6;

    /// <summary>Channels worth recommending: the three that don't overlap on 2.4 GHz, the ones without radar checks on 5 GHz.</summary>
    public static int[] Candidates(WifiBand band) => band switch
    {
        WifiBand.Ghz24 => new[] { 1, 6, 11 },
        WifiBand.Ghz5 => new[] { 36, 40, 44, 48, 149, 153, 157, 161 },
        _ => new[] { 1, 5, 9, 13, 17, 21, 25, 29 },
    };

    /// <summary>How strongly a neighbor on <paramref name="other"/> disturbs <paramref name="channel"/> (0 to 1).</summary>
    internal static double Overlap(WifiBand band, int channel, int other)
    {
        var distance = Math.Abs(channel - other);
        // 2.4 GHz channels are 22 MHz wide but only 5 MHz apart, so they overlap their neighbors.
        return band == WifiBand.Ghz24 ? Math.Max(0, 1 - distance / 5.0) : distance == 0 ? 1 : 0;
    }

    /// <summary>A neighbor's weight: about 0 at -95 dBm (far away) and 1 at -40 dBm (next to the PC).</summary>
    internal static double Weight(int rssi)
    {
        var s = Math.Clamp((rssi + 95) / 55.0, 0, 1);
        return s * s;
    }

    public static List<ChannelLoad> Score(IEnumerable<WifiAp> aps, WifiBand band, int? currentChannel = null)
    {
        var inBand = aps.Where(a => a.Band == band).ToList();
        var candidates = Candidates(band);
        var channels = band == WifiBand.Ghz24
            ? Enumerable.Range(1, 13)
            : candidates.Concat(inBand.Select(a => a.Channel)).Concat(currentChannel is { } c ? new[] { c } : Array.Empty<int>()).Distinct().Order();

        var loads = channels.Select(ch => new
        {
            Channel = ch,
            Load = inBand.Sum(a => Overlap(band, ch, a.Channel) * Weight(a.Rssi)),
            Networks = inBand.Count(a => a.Channel == ch),
        }).ToList();

        // Best among the channels worth recommending; the one in use wins ties (no pointless change).
        var best = loads.Where(l => candidates.Contains(l.Channel))
            .OrderBy(l => Math.Round(l.Load, 2)).ThenBy(l => l.Channel == currentChannel ? 0 : 1).ThenBy(l => l.Channel)
            .FirstOrDefault()?.Channel;
        return loads.Select(l => new ChannelLoad(l.Channel, l.Load, l.Networks, l.Channel == currentChannel, l.Channel == best)).ToList();
    }

    /// <summary>The current channel is already as good as the best one, within a small margin.</summary>
    public static bool CurrentIsFine(IReadOnlyList<ChannelLoad> loads)
    {
        var current = loads.FirstOrDefault(l => l.IsCurrent);
        var best = loads.FirstOrDefault(l => l.IsBest);
        return current is null || best is null || current.Load <= best.Load + 0.15;
    }
}
