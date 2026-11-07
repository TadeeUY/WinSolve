using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WinSolve.Services;

/// <summary>Brand and model of the router, as far as it can tell without signing in.</summary>
public sealed record RouterIdentity(string Brand, string Model, string Source)
{
    public string Display
    {
        get
        {
            if (Brand.Length == 0) return Model;
            if (Model.Length == 0) return Brand;
            return Model.StartsWith(Brand, StringComparison.OrdinalIgnoreCase) ? Model : $"{Brand} {Model}";
        }
    }
}

/// <summary>
/// Finds out which router this is, on its own: first the router's UPnP description (manufacturer and
/// model, what it tells any device on the network), then the brand written on its sign-in page.
/// Only the router's own address is contacted; no password is needed and nothing leaves the network.
/// </summary>
public static class RouterDetector
{
    private static readonly string[] Brands =
    [
        "ZTE", "Huawei", "TP-Link", "Mercusys", "Tenda", "D-Link", "Netgear", "ASUS", "MikroTik", "FRITZ!Box", "Xiaomi",
        "Linksys", "Nokia", "Technicolor", "Arris", "Sagemcom", "Askey", "FiberHome", "Ubiquiti", "Zyxel", "Ruijie",
        "Intelbras", "Movistar", "Vodafone", "Orange", "Netis", "Totolink", "Belkin", "Actiontec", "Calix",
    ];

    public static async Task<RouterIdentity?> DetectAsync(LanInfo lan, CancellationToken ct = default)
    {
        RouterIdentity? upnp = null, web = null;
        try { upnp = await ViaUpnpAsync(lan, ct); } catch { /* UPnP is off on many routers */ }
        if (upnp is { Brand.Length: > 0, Model.Length: > 0 }) return upnp;
        try { web = await ViaSignInPageAsync(lan.Gateway, ct); } catch { /* no web page */ }
        if (upnp is null) return web;
        if (web is null) return upnp;
        // Mix the two: the brand from either, the more specific model.
        return new RouterIdentity(upnp.Brand.Length > 0 ? upnp.Brand : web.Brand, upnp.Model.Length > 0 ? upnp.Model : web.Model, upnp.Source);
    }

    // ───────────── UPnP ─────────────

    private static async Task<RouterIdentity?> ViaUpnpAsync(LanInfo lan, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(lan.Local, 0));
        var request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: upnp:rootdevice\r\n\r\n");
        // Straight to the router, and also the usual multicast address.
        await udp.SendAsync(request, new IPEndPoint(lan.Gateway, 1900), ct);
        try { await udp.SendAsync(request, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), ct); } catch { /* no multicast on this adapter */ }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(2500);
        var locations = new List<Uri>();
        try
        {
            while (locations.Count < 4)
            {
                var reply = await udp.ReceiveAsync(window.Token);
                if (!reply.RemoteEndPoint.Address.Equals(lan.Gateway)) continue;
                if (LocationOf(Encoding.ASCII.GetString(reply.Buffer), lan.Gateway) is { } uri && !locations.Contains(uri)) locations.Add(uri);
            }
        }
        catch (OperationCanceledException) { /* the listening window ended */ }

        using var http = NewClient();
        foreach (var uri in locations)
        {
            var xml = await ReadLimitedAsync(http, uri, ct);
            if (xml is not null && ParseUpnp(xml) is { } id) return id;
        }
        return null;
    }

    /// <summary>The LOCATION header of an SSDP answer, only if it points at the router itself.</summary>
    internal static Uri? LocationOf(string response, IPAddress gateway)
    {
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(trimmed[9..].Trim(), UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(uri.Host, out var ip) && ip.Equals(gateway))
                return uri;
        }
        return null;
    }

    /// <summary>Reads manufacturer and model from a UPnP device description.</summary>
    internal static RouterIdentity? ParseUpnp(string xml)
    {
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 500_000 });
            var doc = XDocument.Load(reader);
            var device = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
            if (device is null) return null;
            string Field(string name) => device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
            var manufacturer = Field("manufacturer");
            var model = Field("modelName");
            if (model.Length == 0) model = Field("modelNumber");
            if (model.Length == 0) model = Field("friendlyName");
            var brand = BrandIn(manufacturer + " " + model) ?? Clean(manufacturer);
            if (brand.Length == 0 && model.Length == 0) return null;
            return new RouterIdentity(brand, Clean(model), "UPnP");
        }
        catch { return null; }
    }

    // ───────────── Sign-in page ─────────────

    private static async Task<RouterIdentity?> ViaSignInPageAsync(IPAddress gateway, CancellationToken ct)
    {
        using var http = NewClient();
        var uri = new Uri($"http://{gateway}/");
        for (var hop = 0; hop < 3; hop++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            var server = string.Join(" ", response.Headers.Server.Select(s => s.ToString()));
            var realm = string.Join(" ", response.Headers.WwwAuthenticate.Select(a => a.Parameter ?? ""));
            if ((int)response.StatusCode is >= 301 and <= 308 && response.Headers.Location is { } next)
            {
                var target = next.IsAbsoluteUri ? next : new Uri(uri, next);
                // Follow only inside the router.
                if (target.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(target.Host, out var ip) || !ip.Equals(gateway)) return FromText(server + " " + realm, "");
                uri = target;
                continue;
            }
            var body = await ReadBodyAsync(response, ct);
            return FromText(server + " " + realm, body);
        }
        return null;
    }

    internal static RouterIdentity? FromText(string headers, string html)
    {
        var title = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value;
        var brand = BrandIn(headers + " " + title) ?? BrandIn(html);
        if (brand is null) return null;
        var model = ModelIn(title) ?? ModelIn(html[..Math.Min(html.Length, 6000)]) ?? "";
        return new RouterIdentity(brand, model, "Sign-in page");
    }

    internal static string? BrandIn(string text)
    {
        foreach (var brand in Brands)
            if (Regex.IsMatch(text, $@"(?<![A-Za-z0-9]){Regex.Escape(brand)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase))
                return brand;
        return null;
    }

    private static readonly Regex[] ModelPatterns =
    [
        new(@"\bZXHN\s?[A-Z]?\d{3,4}[A-Z0-9]*\b", RegexOptions.Compiled),
        new(@"\bF\d{3,4}[A-Z0-9]{0,3}\b", RegexOptions.Compiled),
        new(@"\b(?:HG|EG|HS|WS)\d{3,4}[A-Z0-9]*\b", RegexOptions.Compiled),
        new(@"\bArcher\s?[A-Z]{0,2}\d{1,4}[A-Z0-9]*\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bTL-[A-Z]{1,4}\d{2,4}[A-Z0-9]*\b", RegexOptions.Compiled),
        new(@"\b(?:DIR|DSL|DWR)-\d{2,4}[A-Z0-9]*\b", RegexOptions.Compiled),
        new(@"\bRT-[A-Z]{0,3}\d{2,5}[A-Z0-9]*\b", RegexOptions.Compiled),
    ];

    internal static string? ModelIn(string text)
    {
        foreach (var pattern in ModelPatterns)
        {
            var m = pattern.Match(text);
            if (m.Success) return m.Value.Trim();
        }
        return null;
    }

    private static string Clean(string text)
    {
        var t = Regex.Replace(text, @"\s+", " ").Trim();
        return t.Length > 48 ? t[..48] : t;
    }

    // ───────────── HTTP (router only) ─────────────

    private static HttpClient NewClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private static async Task<string?> ReadLimitedAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        return response.IsSuccessStatusCode ? await ReadBodyAsync(response, ct) : null;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[128 * 1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
