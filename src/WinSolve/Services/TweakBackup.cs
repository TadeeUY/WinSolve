using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>A registry value as it was before WinSolve changed it (<see cref="Absent"/>: it didn't exist).</summary>
public sealed class StoredValue
{
    public bool Absent { get; set; }

    /// <summary><see cref="RegistryValueKind"/> name, or "Marker" for a flag with no registry value.</summary>
    public string Kind { get; set; } = "";

    public string? Data { get; set; }

    public static StoredValue Missing() => new() { Absent = true };

    public static StoredValue Marker() => new() { Kind = "Marker" };

    public static StoredValue From(object? value, RegistryValueKind kind)
    {
        if (value is null) return Missing();
        var data = value switch
        {
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            string s => s,
            string[] m => JsonSerializer.Serialize(m),
            byte[] b => Convert.ToBase64String(b),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
        // A value whose type doesn't match its kind (never written by Windows itself) is kept as text.
        if (kind == RegistryValueKind.Unknown || (kind is RegistryValueKind.DWord && value is not int)
            || (kind is RegistryValueKind.QWord && value is not long) || (kind is RegistryValueKind.MultiString && value is not string[])
            || (kind is RegistryValueKind.Binary or RegistryValueKind.None && value is not byte[]))
            kind = value switch { int => RegistryValueKind.DWord, long => RegistryValueKind.QWord, string[] => RegistryValueKind.MultiString, byte[] => RegistryValueKind.Binary, _ => RegistryValueKind.String };
        return new StoredValue { Kind = kind.ToString(), Data = data };
    }

    public bool IsMarker => Kind == "Marker";

    public RegistryValueKind ValueKind
        => Enum.TryParse<RegistryValueKind>(Kind, out var k) ? k : RegistryValueKind.Unknown;

    /// <summary>The value to write back, or null when it can't be decoded (callers then fall back).</summary>
    public object? ToObject()
    {
        if (Absent || IsMarker || Data is null) return null;
        try
        {
            return ValueKind switch
            {
                RegistryValueKind.DWord => int.Parse(Data, NumberStyles.Integer, CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => long.Parse(Data, NumberStyles.Integer, CultureInfo.InvariantCulture),
                RegistryValueKind.String or RegistryValueKind.ExpandString => Data,
                RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(Data),
                RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(Data),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// JSON file of registry values as they were before WinSolve changed them, so Undo can put back
/// exactly what was there. Writes are atomic (temporary file + rename); a file that can't be
/// parsed is moved aside and replaced by an empty store, so it never blocks Apply or Undo.
/// </summary>
internal sealed class PreviousValueStore(string path)
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string FilePath { get; } = path;

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, StoredValue> Values { get; set; } = [];
    }

    /// <summary>
    /// Reads the store. A missing file is empty; a corrupt one is renamed to *.corrupt-* and read as
    /// empty. Read errors (file locked, access denied) throw: saving over a file that couldn't be
    /// read would lose every other entry.
    /// </summary>
    public Dictionary<string, StoredValue> Load()
    {
        lock (Gate)
        {
            var result = new Dictionary<string, StoredValue>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return result;
            var text = File.ReadAllText(FilePath);
            try
            {
                var model = JsonSerializer.Deserialize<FileModel>(text) ?? throw new JsonException("empty document");
                foreach (var (key, value) in model.Values ?? [])
                    if (!string.IsNullOrEmpty(key) && value is not null) result[key] = value;
                return result;
            }
            catch (JsonException ex)
            {
                var aside = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                try { File.Move(FilePath, aside, overwrite: true); }
                catch (Exception moveEx) { Logger.Write($"Could not move the damaged {FilePath} aside: {moveEx.Message}"); throw; }
                Logger.Write($"{FilePath} was damaged ({ex.Message}); kept a copy as {aside} and started a new one.");
                return result;
            }
        }
    }

    /// <summary>Writes the whole store atomically.</summary>
    public void Save(Dictionary<string, StoredValue> values)
    {
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            if (values.Count == 0)
            {
                File.Delete(FilePath);
                return;
            }
            var temp = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(new FileModel { Values = values }, JsonOptions);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
                writer.Write(json);
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    public StoredValue? Get(string key)
    {
        lock (Gate) return Load().GetValueOrDefault(key);
    }

    /// <summary>Adds the entries whose key isn't stored yet (the first, original value is kept).</summary>
    public void RecordIfMissing(IEnumerable<KeyValuePair<string, StoredValue>> entries)
    {
        lock (Gate)
        {
            var values = Load();
            var changed = false;
            foreach (var (key, value) in entries)
                changed |= values.TryAdd(key, value);
            if (changed) Save(values);
        }
    }

    public void Set(string key, StoredValue value)
    {
        lock (Gate)
        {
            var values = Load();
            values[key] = value;
            Save(values);
        }
    }

    public void Remove(IEnumerable<string> keys)
    {
        lock (Gate)
        {
            var values = Load();
            var changed = false;
            foreach (var key in keys) changed |= values.Remove(key);
            if (changed) Save(values);
        }
    }

    /// <summary>Keys that start with <paramref name="prefix"/>.</summary>
    public List<string> KeysStartingWith(string prefix)
    {
        lock (Gate) return Load().Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}

/// <summary>
/// Remembers the registry values tweaks (and the Windows Update presets) overwrite, so Undo
/// restores the previous state instead of a guessed default. Per-user values are kept per
/// signed-in account (SID). Stored in the administrators-only data folder.
/// </summary>
public static class TweakBackup
{
    private static readonly Lazy<PreviousValueStore> LazyStore = new(() => new PreviousValueStore(Path.Combine(SafePath.DataFolder, "tweak-backup.json")));

    internal static PreviousValueStore Store => LazyStore.Value;

    /// <summary>Store key of one registry value changed by <paramref name="owner"/> (a tweak id).</summary>
    internal static string Key(string owner, string scope, RegistryHive hive, string path, string name)
        => $"{owner}|{scope}|{hive}\\{path.Trim('\\')}|{name}";

    private static string Key(string owner, RegistryHive hive, string path, string name)
        => Key(owner, hive == RegistryHive.CurrentUser ? InteractiveUser.Sid.Value : "machine", hive, path, name);

    /// <summary>Key of a flag (no registry value) for the signed-in account.</summary>
    public static string UserFlag(string owner, string flag) => $"{owner}|{InteractiveUser.Sid.Value}|flag|{flag}";

    /// <summary>The value as it is now (with its type), or Absent.</summary>
    public static StoredValue ReadCurrent(RegistryHive hive, string path, string name)
    {
        using var root = Reg.Root(hive);
        using var key = root.OpenSubKey(path);
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? StoredValue.Missing() : StoredValue.From(value, key!.GetValueKind(name));
    }

    /// <summary>Writes a stored value back (Absent deletes it). False if it couldn't be decoded.</summary>
    public static bool Write(RegistryHive hive, string path, string name, StoredValue value)
    {
        if (value.Absent)
        {
            Reg.Delete(hive, path, name);
            return true;
        }
        if (value.ToObject() is not { } data) return false;
        Reg.Set(hive, path, name, data, value.ValueKind);
        return true;
    }

    /// <summary>
    /// Call before writing the given values: saves each one's current state the first time it is
    /// changed. When every value already has its target (already applied), nothing is saved and
    /// Undo uses the defaults; otherwise every value is saved, including one that happens to
    /// match already, so Undo puts back exactly what was there.
    /// Never throws for the store: without a backup Undo falls back to the default values.
    /// </summary>
    public static void Remember(string owner, IEnumerable<(RegistryHive Hive, string Path, string Name, object Target)> values)
    {
        var current = values.Select(v => (v.Hive, v.Path, v.Name, v.Target,
            Value: ReadCurrent(v.Hive, v.Path, v.Name))).ToList(); // may throw (unresolved user): nothing is written then
        if (current.All(c => !c.Value.Absent && Reg.ValueEquals(c.Value.ToObject(), c.Target))) return;
        var entries = current.Select(c => new KeyValuePair<string, StoredValue>(Key(owner, c.Hive, c.Path, c.Name), c.Value)).ToList();
        try { Store.RecordIfMissing(entries); }
        catch (Exception ex) { Logger.Write($"Could not save the previous values for {owner}: {ex.Message}. Undo will use the Windows defaults."); }
    }

    /// <summary>True if a previous value of this registry value is stored.</summary>
    public static bool Has(string owner, RegistryHive hive, string path, string name)
    {
        try { return Store.Get(Key(owner, hive, path, name)) is not null; }
        catch (Exception ex) { Logger.Write($"Could not read {Store.FilePath}: {ex.Message}"); return false; }
    }

    /// <summary>Puts back the stored previous value. False when none is stored (use the default).</summary>
    public static bool TryRestore(string owner, RegistryHive hive, string path, string name)
    {
        StoredValue? previous;
        try { previous = Store.Get(Key(owner, hive, path, name)); }
        catch (Exception ex) { Logger.Write($"Could not read {Store.FilePath}: {ex.Message}"); return false; }
        return previous is not null && Write(hive, path, name, previous);
    }

    /// <summary>Drops the stored values once they have been restored.</summary>
    public static void Forget(string owner, IEnumerable<(RegistryHive Hive, string Path, string Name)> values)
    {
        try { Store.Remove(values.Select(v => Key(owner, v.Hive, v.Path, v.Name)).ToList()); }
        catch (Exception ex) { Logger.Write($"Could not update {Store.FilePath}: {ex.Message}"); }
    }

    public static bool HasFlag(string key)
    {
        try { return Store.Get(key) is not null; }
        catch (Exception ex) { Logger.Write($"Could not read {Store.FilePath}: {ex.Message}"); return false; }
    }

    public static void SetFlag(string key, bool on)
    {
        try
        {
            if (on) Store.Set(key, StoredValue.Marker());
            else Store.Remove([key]);
        }
        catch (Exception ex) { Logger.Write($"Could not update {Store.FilePath}: {ex.Message}"); }
    }
}

/// <summary>
/// Whether an organization manages this PC (Active Directory domain or MDM such as Intune).
/// Policy tweaks there can be overwritten by, or conflict with, the organization's settings.
/// </summary>
public static class ManagedPc
{
    private static readonly Lazy<bool> Domain = new(() =>
        Wmi.Query("SELECT PartOfDomain FROM Win32_ComputerSystem").Any(c => c.Get<bool>("PartOfDomain")));

    private static readonly Lazy<bool> Mdm = new(() =>
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var enrollments = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
            foreach (var name in enrollments?.GetSubKeyNames() ?? [])
            {
                using var e = enrollments!.OpenSubKey(name);
                if (e?.GetValue("ProviderID") is string provider && provider.Trim().Length > 0) return true;
            }
        }
        catch (Exception ex) { Logger.Write($"Could not read the MDM enrollments: {ex.Message}"); }
        return false;
    });

    public static bool IsDomainJoined => Domain.Value;

    public static bool IsMdmEnrolled => Mdm.Value;

    public static bool IsManaged => IsDomainJoined || IsMdmEnrolled;

    /// <summary>
    /// Warning shown before applying or undoing policy tweaks on a managed PC (empty when the PC
    /// isn't managed or none of the tweaks writes policies).
    /// </summary>
    public static string PolicyWarning(IEnumerable<Tweak> tweaks)
    {
        var policy = tweaks.Where(t => t.TouchesPolicies).Select(t => Localization.Loc.T(t.Title)).Distinct().ToList();
        if (policy.Count == 0 || !IsManaged) return "";
        return Localization.Loc.T(IsDomainJoined
                   ? "This PC is joined to an organization's domain."
                   : "This PC is managed by an organization (device management).")
               + " " + Localization.Loc.T("These tweaks change policy settings, which the organization can overwrite or which can conflict with its own settings:")
               + "\n- " + string.Join("\n- ", policy);
    }
}
