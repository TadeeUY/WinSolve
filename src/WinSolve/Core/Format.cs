namespace WinSolve.Core;

public static class Format
{
    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.#} {units[i]}";
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m";
    }
}
