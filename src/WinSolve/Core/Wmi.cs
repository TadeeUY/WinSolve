using System.Management;

namespace WinSolve.Core;

/// <summary>WMI queries that never throw (an empty list is returned on failure).</summary>
public static class Wmi
{
    public static List<ManagementBaseObject> Query(string query, string scope = @"root\cimv2")
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (var obj in searcher.Get())
                list.Add(obj);
        }
        catch (Exception ex)
        {
            Logger.Write($"WMI ({scope}) '{query}' failed: {ex.Message}");
        }
        return list;
    }

    public static T? Get<T>(this ManagementBaseObject obj, string property)
    {
        try
        {
            var value = obj[property];
            if (value is null) return default;
            if (value is T t) return t;
            return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
        }
        catch
        {
            return default;
        }
    }

    public static string Str(this ManagementBaseObject obj, string property)
        => obj.Get<object>(property)?.ToString()?.Trim() ?? string.Empty;
}
