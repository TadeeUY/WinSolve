using System.Text.RegularExpressions;

namespace WinSolve.UI;

/// <summary>
/// Reads the Wi-Fi name and password from a router's Wi-Fi settings page, whatever the brand:
/// those pages hold them in input boxes, and the boxes have telling names (ssid, psk, passphrase...).
/// </summary>
internal static class WifiFromPage
{
    internal sealed record Field(string Id, string Name, string Type, string Value);

    /// <summary>The Wi-Fi name and password found on the page (and its frames), or null.</summary>
    public static (string Ssid, string Password)? Read(HtmlDocument? document)
    {
        if (document is null) return null;
        var fields = new List<Field>();
        Collect(document, fields, 0);
        return Pick(fields);
    }

    private static void Collect(HtmlDocument document, List<Field> into, int depth)
    {
        try
        {
            foreach (HtmlElement e in document.GetElementsByTagName("input"))
            {
                var type = (e.GetAttribute("type") ?? "text").ToLowerInvariant();
                if (type is not ("text" or "password" or "")) continue;
                into.Add(new Field(e.Id ?? "", e.Name ?? "", type, e.GetAttribute("value") ?? ""));
            }
            if (depth >= 3) return;
            foreach (HtmlWindow frame in document.Window.Frames)
            {
                try { if (frame.Document is { } inner) Collect(inner, into, depth + 1); }
                catch { /* a frame from another site: not readable */ }
            }
        }
        catch { /* the page changed while it was read */ }
    }

    private static readonly Regex SsidName = new(@"ssid", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StrongKeyName = new(@"psk|passphrase|wpakey|wpapass|presharedkey|sharedkey|wlkey|wlpwd|wifipass|wifikey|keypass|wlanpass", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WeakKeyName = new(@"pass|pwd|key", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NotWifiKey = new(@"admin|login|user|old|new|confirm|verify|retype|super|web|telnet|ftp|ssh|pin", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The network name is a box named like "ssid" with 1 to 32 characters; the password is the next
    /// box named like a Wi-Fi key with 8 to 63 characters. A box that is just called "password" only
    /// counts when the page also has a network name (the router's own sign-in page has no name).
    /// </summary>
    internal static (string Ssid, string Password)? Pick(IReadOnlyList<Field> fields)
    {
        static string Label(Field f) => f.Id + " " + f.Name;
        var ssidIndex = -1;
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (f.Type != "password" && SsidName.IsMatch(Label(f)) && f.Value.Trim().Length is >= 1 and <= 32) { ssidIndex = i; break; }
        }
        if (ssidIndex < 0) return null;

        bool Looks(Field f, Regex name) => f.Value.Length is >= 8 and <= 63 && name.IsMatch(Label(f)) && !NotWifiKey.IsMatch(Label(f));
        // The names of the router's boxes often come in order: the network name, then its key.
        var ordered = fields.Skip(ssidIndex + 1).Concat(fields.Take(ssidIndex)).ToList();
        var key = ordered.FirstOrDefault(f => Looks(f, StrongKeyName)) ?? ordered.FirstOrDefault(f => Looks(f, WeakKeyName));
        return (fields[ssidIndex].Value.Trim(), key?.Value ?? "");
    }
}
