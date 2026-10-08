using Microsoft.Win32;

namespace WinSolve.Core;

/// <summary>Registry helpers (always the 64-bit view).</summary>
public static class Reg
{
    public static RegistryKey Root(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    public static object? Get(RegistryHive hive, string path, string name)
    {
        using var root = Root(hive);
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public static void Set(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        using var root = Root(hive);
        using var key = root.CreateSubKey(path, writable: true);
        key.SetValue(name, value, kind);
    }

    public static void Delete(RegistryHive hive, string path, string name)
    {
        using var root = Root(hive);
        using var key = root.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public static bool KeyExists(RegistryHive hive, string path)
    {
        using var root = Root(hive);
        using var key = root.OpenSubKey(path);
        return key is not null;
    }

    public static void DeleteKeyTree(RegistryHive hive, string path)
    {
        using var root = Root(hive);
        root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    /// <summary>Compares a registry value with the expected one (numbers and strings).</summary>
    public static bool ValueEquals(object? actual, object expected)
    {
        if (actual is null) return false;
        return expected switch
        {
            int i => actual is int a && a == i,
            uint u => actual is int a2 && unchecked((uint)a2) == u,
            long l => actual is long b && b == l,
            string s => actual is string str && string.Equals(str, s, StringComparison.OrdinalIgnoreCase),
            _ => Equals(actual, expected),
        };
    }
}
