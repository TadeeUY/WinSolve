using System.Security.AccessControl;
using System.Security.Principal;

namespace WinSolve.Core;

/// <summary>
/// Guards for file operations performed with administrator rights on paths that a
/// non-elevated process could influence (user profile, %TEMP%, registry data).
/// </summary>
public static class SafePath
{
    /// <summary>
    /// True when the path or any of its existing ancestors is a junction or symbolic link.
    /// A non-elevated process could plant one to redirect an elevated delete into C:\Windows.
    /// </summary>
    public static bool HasReparsePoint(string path)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(path));
            while (current is not null)
            {
                if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint) && !IsKnownSystemLink(current.FullName))
                    return true;
                current = current.Parent;
            }
            return false;
        }
        catch
        {
            return true; // when in doubt, treat as unsafe
        }
    }

    // Windows itself ships a few junctions on the way to user folders (e.g. "Documents and Settings").
    private static bool IsKnownSystemLink(string path)
        => path.EndsWith(@"\Documents and Settings", StringComparison.OrdinalIgnoreCase);

    /// <summary>Enumeration options that never follow junctions or symbolic links.</summary>
    public static EnumerationOptions NoLinks(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>
    /// What <see cref="IsProtectedFolder"/> checks against. KeyFolders are protected themselves and
    /// so is every folder that contains one; everything inside SystemAreas is protected; the
    /// folders directly inside ProfileParents are profile roots.
    /// </summary>
    internal sealed record ProtectionRules(
        IReadOnlyList<string> KeyFolders,
        IReadOnlyList<string> SystemAreas,
        IReadOnlyList<string> ProfileParents,
        string SystemDrive,
        bool UserFoldersUnknown = false);

    /// <summary>Windows' own folders at the root of a drive (boot, recovery, restore points, Recycle Bin).</summary>
    internal static readonly string[] DriveSystemFolders =
        ["$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "$WinREAgent", "Config.Msi", "$Windows.~BT", "$Windows.~WS", "$SysReset"];

    // Computed once: the signed-in user doesn't change while WinSolve runs.
    private static readonly Lazy<ProtectionRules> Rules = new(BuildRules);

    private static ProtectionRules BuildRules()
    {
        var keys = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)) return;
            try { keys.Add(Path.GetFullPath(p).TrimEnd('\\')); } catch { }
        }

        // The account WinSolve runs as.
        foreach (Environment.SpecialFolder f in Enum.GetValues<Environment.SpecialFolder>())
        {
            try { Add(Environment.GetFolderPath(f)); } catch { }
        }
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var systemDrive = Path.GetPathRoot(win)!.TrimEnd('\\');
        Add(Path.Combine(pd, "Microsoft"));
        Add(Path.Combine(pf, "WindowsApps"));
        Add(Path.Combine(pf, "dotnet"));
        Add(Path.Combine(pf, "Windows Defender"));
        Add(Path.Combine(pf, "Common Files"));
        if (pf86.Length > 0) Add(Path.Combine(pf86, "Common Files"));
        foreach (var sub in new[] { "System32", "SysWOW64", "WinSxS", "servicing", "assembly", "Microsoft.NET", "Installer", "INF", "Fonts", "Boot", "SystemApps", "SystemResources" })
            Add(Path.Combine(win, sub));
        Add(systemDrive + @"\Users");
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            Add(Environment.GetEnvironmentVariable(name));

        // Every profile on this PC (and so every folder that contains one).
        var parents = new List<string> { systemDrive + @"\Users" };
        foreach (var profile in InteractiveUser.AllProfileFolders())
        {
            Add(profile);
            if (Path.GetDirectoryName(profile) is { Length: > 3 } parent && !parents.Contains(parent, StringComparer.OrdinalIgnoreCase))
                parents.Add(parent);
        }

        // The person at the keyboard, who may not be the account WinSolve runs as.
        var unknown = false;
        try
        {
            foreach (var p in InteractiveUser.KnownFolderPaths()) Add(p);
        }
        catch (Exception ex)
        {
            // Fail closed: without their folders, nothing inside a profile counts as safe to delete.
            unknown = true;
            Logger.Write($"Could not read the signed-in user's folders; every folder in a profile is treated as protected: {ex.Message}");
        }

        string[] areas = [win, Path.Combine(pd, "Microsoft"), Path.Combine(pf, "WindowsApps"), Path.Combine(pf, "Windows Defender"), Path.Combine(pf, "dotnet")];
        return new ProtectionRules(keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            areas.Where(a => a.Length > 3).ToList(), parents, systemDrive, unknown);
    }

    /// <summary>
    /// True for folders that must never be deleted recursively: drive roots and first-level folders,
    /// Windows' boot and recovery folders, anything inside Windows, any known folder of this account
    /// or of the signed-in user (Documents, Desktop, AppData, OneDrive...), Program Files and
    /// ProgramData themselves, every user profile root, and any ancestor of those.
    /// </summary>
    public static bool IsProtectedFolder(string path) => Check(path, anyFirstLevel: true, systemAreas: true);

    /// <summary>
    /// <see cref="IsProtectedFolder"/> except "anything inside Windows": for emptying known cache
    /// folders (C:\Windows\Temp), which must still never be a key folder or contain one.
    /// </summary>
    public static bool IsCriticalFolder(string path) => Check(path, anyFirstLevel: true, systemAreas: false);

    /// <summary>
    /// Folders a person must not delete by hand (Space page): <see cref="IsProtectedFolder"/>, except
    /// that first-level folders of drives other than the Windows one (D:\Photos) may go.
    /// </summary>
    public static bool IsProtectedFromUserDelete(string path) => Check(path, anyFirstLevel: false, systemAreas: true);

    /// <summary>Inside (or one of) Windows, Program Files (x86), ProgramData or a drive's Windows system folders.</summary>
    public static bool IsInsideSystemOrProgramFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return true;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return true; }
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        return roots.Any(r => r.Length > 3 && IsSameOrInside(full, r)) || InDriveSystemFolder(full);
    }

    private static bool Check(string path, bool anyFirstLevel, bool systemAreas)
    {
        // A relative path would resolve against the working folder (System32 when elevated).
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return true;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return true; }
        return IsProtected(full, Rules.Value, anyFirstLevel, systemAreas);
    }

    /// <summary>The checks on a full path, without touching the disk (unit-tested with made-up rules).</summary>
    internal static bool IsProtected(string full, ProtectionRules rules, bool anyFirstLevel, bool systemAreas)
    {
        full = full.TrimEnd('\\');
        var root = Path.GetPathRoot(full)?.TrimEnd('\\');
        if (string.IsNullOrEmpty(root) || full.Length <= root.Length + 1) return true; // a drive root

        var parts = full[(root.Length + 1)..].Split('\\');
        if (parts.Length == 1 && (anyFirstLevel || root.Equals(rules.SystemDrive, StringComparison.OrdinalIgnoreCase))) return true;
        if (InDriveSystemFolder(full)) return true;

        var parent = Path.GetDirectoryName(full);
        foreach (var p in rules.ProfileParents)
        {
            if (string.Equals(parent, p, StringComparison.OrdinalIgnoreCase)) return true; // a profile root
            if (rules.UserFoldersUnknown && IsSameOrInside(full, p)) return true;
        }

        // The folder is a key folder, or contains one.
        if (rules.KeyFolders.Any(k => IsSameOrInside(k, full))) return true;

        // Anything inside a Windows/Microsoft-owned location.
        return systemAreas && rules.SystemAreas.Any(a => IsSameOrInside(full, a));
    }

    /// <summary>C:\Recovery, D:\System Volume Information, C:\$Recycle.Bin... or anything inside them.</summary>
    internal static bool InDriveSystemFolder(string full)
    {
        var root = Path.GetPathRoot(full)?.TrimEnd('\\');
        if (string.IsNullOrEmpty(root) || full.TrimEnd('\\').Length <= root.Length + 1) return false;
        var first = full[(root.Length + 1)..].Split('\\')[0];
        return DriveSystemFolders.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a temporary folder that may be emptied: a fully
    /// qualified path (not relative, not a drive root, no unexpanded variables) strictly inside
    /// <paramref name="localAppData"/> whose own name is Temp or Tmp. Pure string checks.
    /// </summary>
    internal static bool IsTempFolderInside(string? candidate, string? localAppData)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(localAppData)) return false;
        if (candidate.Contains('%') || !Path.IsPathFullyQualified(candidate) || !Path.IsPathFullyQualified(localAppData)) return false;
        string full, local;
        try
        {
            full = Path.GetFullPath(candidate).TrimEnd('\\');
            local = Path.GetFullPath(localAppData).TrimEnd('\\');
        }
        catch { return false; }
        var root = Path.GetPathRoot(full)?.TrimEnd('\\');
        if (string.IsNullOrEmpty(root) || full.Length <= root.Length + 1) return false;
        if (local.Length <= (Path.GetPathRoot(local)?.TrimEnd('\\').Length ?? 0) + 1) return false; // LocalAppData can't be a drive root
        static bool IsTempName(string n) => n.Equals("Temp", StringComparison.OrdinalIgnoreCase) || n.Equals("Tmp", StringComparison.OrdinalIgnoreCase);
        var name = Path.GetFileName(full);
        // Remote Desktop sessions get a numbered folder inside Temp (Temp\2).
        var numbered = name.Length > 0 && name.All(char.IsAsciiDigit) && IsTempName(Path.GetFileName(Path.GetDirectoryName(full) ?? ""));
        if (!IsTempName(name) && !numbered) return false;
        return full.StartsWith(local + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> (a folder read from a user's registry) is a fully
    /// qualified path strictly inside <paramref name="profile"/>. Pure string checks.
    /// </summary>
    internal static bool IsStrictlyInside(string? candidate, string? profile)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(profile)) return false;
        if (!Path.IsPathFullyQualified(candidate) || !Path.IsPathFullyQualified(profile)) return false;
        try
        {
            var full = Path.GetFullPath(candidate).TrimEnd('\\');
            var p = Path.GetFullPath(profile).TrimEnd('\\');
            var root = Path.GetPathRoot(p)?.TrimEnd('\\') ?? "";
            return p.Length > root.Length + 1 && full.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Expands 8.3 short names (Path.GetTempPath can return C:\Users\ADMINI~1\...); unchanged if it doesn't exist.</summary>
    internal static string LongPath(string path)
    {
        try
        {
            var buffer = new char[32768];
            var len = GetLongPathNameW(path, buffer, (uint)buffer.Length);
            return len > 0 && len < buffer.Length ? new string(buffer, 0, (int)len) : path;
        }
        catch { return path; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [System.Runtime.InteropServices.Out] char[] buffer, uint size);

    /// <summary>True if <paramref name="path"/> equals <paramref name="folder"/> or is inside it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        path = path.TrimEnd('\\');
        folder = folder.TrimEnd('\\');
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if a standard (non-elevated) user can modify files in this location.</summary>
    public static bool IsUserWritableLocation(string path)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !(IsSameOrInside(path, pf) || (pf86.Length > 0 && IsSameOrInside(path, pf86)) || IsSameOrInside(path, win));
    }

    /// <summary>
    /// %ProgramData%\WinSolve: settings and logs. WinSolve always runs elevated, so its own
    /// files live where only Administrators and SYSTEM can read and write. Writing
    /// them under the user profile would let a non-elevated process redirect those writes
    /// with links to any file on the system.
    /// </summary>
    public static string DataFolder => DataFolderLazy.Value;

    private static readonly Lazy<string> DataFolderLazy = new(() =>
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinSolve");
        try
        {
            if (Admin.IsElevated) MakeTrusted(dir);
            else Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Logger.WriteEarly($"Could not secure {dir}: {ex.Message}");
            Directory.CreateDirectory(dir);
        }
        return dir;
    });

    /// <summary>Administrators and SYSTEM only: settings, logs and files run elevated live here.</summary>
    private static DirectorySecurity AdminOnlySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Any user can create folders in %ProgramData%. A WinSolve folder that is a link, or that a
    /// non-administrator created (and so can still change), is moved aside and made again, so
    /// nobody but administrators can plant settings or files that WinSolve uses elevated.
    /// </summary>
    internal static void MakeTrusted(string dir)
    {
        if (Directory.Exists(dir) || File.Exists(dir))
        {
            // Renaming works on the link itself and needs only our rights on ProgramData.
            var attributes = File.GetAttributes(dir);
            var untrusted = (attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0 || !OwnedByAdmins(dir);
            if (untrusted)
            {
                var aside = dir + ".untrusted-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(dir, aside);
                Logger.WriteEarly($"{dir} was not created by an administrator; moved it to {aside}.");
            }
        }
        var info = new DirectoryInfo(dir);
        if (!info.Exists) info.Create(AdminOnlySecurity());
        else info.SetAccessControl(AdminOnlySecurity()); // also removes the read access older versions gave to Users
    }

    private static bool OwnedByAdmins(string dir)
    {
        var owner = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || owner.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal) /* TrustedInstaller and other services */
            || owner == System.Security.Principal.WindowsIdentity.GetCurrent().User);
    }

    /// <summary>
    /// Creates a fresh, randomly named folder under %ProgramData%\WinSolve that only
    /// Administrators and SYSTEM can write to. Used for files that are executed elevated.
    /// </summary>
    public static string CreateAdminOnlyFolder(string purpose)
    {
        // Inside the trusted data folder, so the parent can't be swapped by a standard user.
        var root = Path.Combine(DataFolder, purpose);
        if (Directory.Exists(root) && HasReparsePoint(root)) throw new InvalidOperationException($"{root} is a link.");
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        new DirectoryInfo(dir).Create(AdminOnlySecurity());
        return dir;
    }
}
