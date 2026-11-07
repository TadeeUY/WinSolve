using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using QRCoder;

namespace WinSolve.Services;

public enum WifiQrSecurity { Wpa, Wep, Open }

/// <summary>The QR code phones read to join a Wi-Fi network (the standard "WIFI:" format).</summary>
public static class WifiQr
{
    /// <summary>"WIFI:T:WPA;S:name;P:password;;" with the special characters escaped.</summary>
    public static string Payload(string ssid, string password, WifiQrSecurity security, bool hidden)
    {
        var type = security switch { WifiQrSecurity.Wep => "WEP", WifiQrSecurity.Open => "nopass", _ => "WPA" };
        var text = $"WIFI:T:{type};S:{Field(ssid)};";
        if (security != WifiQrSecurity.Open) text += $"P:{Field(password)};";
        if (hidden) text += "H:true;";
        return text + ";";
    }

    /// <summary>
    /// Escapes \ ; , : and ". A name or password made only of hexadecimal digits would be read as
    /// hex by some phones, so it goes between quotes.
    /// </summary>
    internal static string Field(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace(":", "\\:").Replace("\"", "\\\"");
        return escaped.Length > 0 && Regex.IsMatch(escaped, "^[0-9A-Fa-f]+$") ? "\"" + escaped + "\"" : escaped;
    }

    /// <summary>The code's modules (true = dark), with the quiet margin the scanners need.</summary>
    public static bool[,] Modules(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var modules = new bool[size, size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                modules[y, x] = matrix[y][x];
        return modules;
    }

    /// <summary>Black on white, always: scanners need that contrast even in a dark theme.</summary>
    public static Bitmap Render(string payload, int pixelsPerModule = 10)
    {
        var modules = Modules(payload);
        var size = modules.GetLength(0);
        var bitmap = new Bitmap(size * pixelsPerModule, size * pixelsPerModule);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.None;
        using var dark = new SolidBrush(Color.Black);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (modules[y, x]) g.FillRectangle(dark, x * pixelsPerModule, y * pixelsPerModule, pixelsPerModule, pixelsPerModule);
        return bitmap;
    }
}
