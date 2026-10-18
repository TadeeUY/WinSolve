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

    /// <summary>
    /// Windows' own folders at the root of a drive (boot, recovery, restore points, Recycle Bin, and
    /// Store apps and games installed to another drive: WindowsApps, WpSystem).
    /// </summary>
    internal static readonly string[] DriveSystemFolders =
        ["$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "$WinREAgent", "Config.Msi", "$Windows.~BT", "$Windows.~WS", "$SysReset",
         "WindowsApps", "WpSystem"];

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
    public static bool IsProtectedFromUserDelete(string path)
        => Check(path, anyFirstLevel: false, systemAreas: true)
           || (Normalize(path) is { } full && UserWindowsAreas.Value.Any(a => IsSameOrInside(full.TrimEnd('\\'), a)));

    /// <summary>
    /// Folders in the signed-in user's AppData that Windows manages (Store app data, the winget
    /// stubs, the keys that protect saved passwords): deleting them by hand breaks Start, Store
    /// apps or loses secrets. Cleanups of known caches inside them are not affected.
    /// </summary>
    private static readonly Lazy<string[]> UserWindowsAreas = new(() =>
    {
        try
        {
            return new[]
                {
                    Path.Combine(InteractiveUser.LocalAppData, "Packages"),
                    Path.Combine(InteractiveUser.LocalAppData, "Microsoft"),
                    Path.Combine(InteractiveUser.RoamingAppData, "Microsoft"),
                    Path.Combine(InteractiveUser.LocalLow, "Microsoft"),
                }
                .Select(a => Normalize(a)?.TrimEnd('\\')).OfType<string>().ToArray();
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read the signed-in user's AppData folders: {ex.Message}");
            return [];
        }
    });

    /// <summary>Inside (or one of) Windows, Program Files (x86), ProgramData or a drive's Windows system folders.</summary>
    public static bool IsInsideSystemOrProgramFolder(string path)
    {
        if (Normalize(path) is not { } normalized) return true;
        var full = normalized.TrimEnd('\\');
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
        return Normalize(path) is not { } full || IsProtected(full, Rules.Value, anyFirstLevel, systemAreas);
    }

    /// <summary>
    /// The path in the plain form the checks compare against: no \\?\ prefix, "." and ".." resolved,
    /// and 8.3 short names expanded (C:\PROGRA~2, C:\Users\ADMINI~1). Null for a relative path, which
    /// would resolve against the working folder (System32 when elevated), or an invalid one.
    /// </summary>
    internal static string? Normalize(string? path)
    {
        if (StripDevicePrefix(path) is not { } plain || !Path.IsPathFullyQualified(plain)) return null;
        try { return LongPath(Path.GetFullPath(plain)); }
        catch { return null; }
    }

    /// <summary>\\?\C:\x and \\.\C:\x become C:\x, \\?\UNC\server\share becomes \\server\share. Pure.</summary>
    internal static string? StripDevicePrefix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var prefix in new[] { @"\\?\", @"\\.\", @"\??\" })
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = path[prefix.Length..];
            return rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + rest[4..] : rest;
        }
        return path;
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
        var parts = full[(root.Length + 1)..].Split('\\');
        if (DriveSystemFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return true;
        // Store apps: <drive>\Program Files\WindowsApps on any drive.
        return parts.Length > 1 && parts[0].Equals("Program Files", StringComparison.OrdinalIgnoreCase)
               && parts[1].Equals("WindowsApps", StringComparison.OrdinalIgnoreCase);
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
            if (len > 0 && len < buffer.Length) return new string(buffer, 0, (int)len);
            // Doesn't exist (yet): expand the part that does (C:\PROGRA~2\missing).
            if (!path.Contains('~')) return path;
            var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
            if (string.IsNullOrEmpty(parent) || parent.Length >= path.TrimEnd('\\').Length) return path;
            return Path.Combine(LongPath(parent), Path.GetFileName(path.TrimEnd('\\')));
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
    /// The trusted data folder (%ProgramData%\WinSolve, or the portable edition's folder, see
    /// <see cref="DataLocation"/>). WinSolve always runs elevated, so the files it acts on live where
    /// only Administrators and SYSTEM can read and write. Writing them under the user profile
    /// would let a non-elevated process redirect those writes with links to any file on the system.
    /// </summary>
    public static string DataFolder => DataFolderLazy.Value;

    private static readonly Lazy<string> DataFolderLazy = new(() =>
    {
        var dir = DataLocation.TrustedPath;
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

    private static SecurityIdentifier AdministratorsSid => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Administrators and SYSTEM only: settings, logs and files run elevated live here.</summary>
    private static DirectorySecurity AdminOnlySecurity() => ProtectedSecurity(usersCanRead: false);

    /// <summary>
    /// A protected DACL (nothing inherited from the parent) owned by Administrators: Administrators
    /// and SYSTEM have full control and, if <paramref name="usersCanRead"/>, Users can read and run.
    /// </summary>
    private static DirectorySecurity ProtectedSecurity(bool usersCanRead)
    {
        var security = new DirectorySecurity();
        // Explicitly, not the token's default owner: an administrator account whose default owner is
        // itself would otherwise own the folder, and that owner is not trusted on the next start.
        security.SetOwner(AdministratorsSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid,
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (usersCanRead)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
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

    /// <summary>
    /// Owned by Administrators, SYSTEM or a service (TrustedInstaller). Not by the account WinSolve
    /// runs as: a non-elevated process of that same account creates folders owned by it, so such
    /// a folder may have been planted before WinSolve started.
    /// </summary>
    private static bool OwnedByAdmins(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        // When this account never runs anything without full rights (UAC off, or the built-in
        // Administrator with admin approval mode off), there is no non-elevated process of it that
        // could have planted the folder, and Windows makes the account itself the owner.
        return IsTrustedOwner(owner) || (owner is not null && owner == WindowsIdentity.GetCurrent().User && AccountAlwaysElevated.Value);
    }

    private static readonly Lazy<bool> AccountAlwaysElevated = new(() =>
    {
        try
        {
            const string policies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
            if (Reg.Get(Microsoft.Win32.RegistryHive.LocalMachine, policies, "EnableLUA") is int lua && lua == 0) return true;
            var me = WindowsIdentity.GetCurrent().User;
            return me is not null && me.IsAccountSid() && me.Value.EndsWith("-500", StringComparison.Ordinal)
                   && Reg.Get(Microsoft.Win32.RegistryHive.LocalMachine, policies, "FilterAdministratorToken") is not 1;
        }
        catch { return false; }
    });

    /// <summary>Administrators, SYSTEM, TrustedInstaller or another service SID. Pure.</summary>
    internal static bool IsTrustedOwner(SecurityIdentifier? owner)
        => owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || owner.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal)); /* TrustedInstaller and other services */

    /// <summary>
    /// Makes a folder at the root of the drive that a vendor installer unpacks into and then runs
    /// from elevated (C:\NVIDIA, C:\AMD) safe to use. Any user can create folders in C:\, and
    /// folders made there inherit write access for every user, so an existing one that is a link,
    /// or that holds anything a non-administrator created (or a link), is renamed to
    /// "folder.winsolve-time". The folder is then (re)made owned by Administrators with a
    /// protected DACL: Administrators and SYSTEM full control, Users read and run.
    /// Throws when the folder can't be moved aside or secured.
    /// </summary>
    public static void PrepareInstallerFolder(string dir, Action<string> log)
    {
        if (Directory.Exists(dir) || File.Exists(dir))
        {
            // Renaming works on the link itself.
            var attributes = File.GetAttributes(dir);
            string? reason = (attributes & FileAttributes.ReparsePoint) != 0 ? "it is a link"
                : (attributes & FileAttributes.Directory) == 0 ? "it is a file"
                : !OwnedByAdmins(dir) ? "it was not created by an administrator"
                : UntrustedEntry(dir) is { } entry ? $"{entry} is a link or was not created by an administrator"
                : null;
            if (reason is not null)
            {
                var aside = $"{dir}.winsolve-{DateTime.Now:yyyyMMdd-HHmmss}";
                Directory.Move(dir, aside);
                log($"{dir} was moved to {aside} before the installer runs, because {reason}.");
            }
        }
        var info = new DirectoryInfo(dir);
        if (!info.Exists) info.Create(ProtectedSecurity(usersCanRead: true));
        else info.SetAccessControl(ProtectedSecurity(usersCanRead: true)); // drops the write access inherited from C:\
    }

    /// <summary>The first file or folder inside <paramref name="dir"/> that is a link or not owned by an administrator.</summary>
    private static string? UntrustedEntry(string dir)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        var pending = new Stack<string>();
        pending.Push(dir);
        var current = dir;
        try
        {
            while (pending.Count > 0)
            {
                current = pending.Pop();
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", options))
                {
                    current = entry.FullName;
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || !OwnedByAdmins(entry.FullName)) return entry.FullName;
                    if (entry is DirectoryInfo) pending.Push(entry.FullName);
                }
            }
            return null;
        }
        catch
        {
            return current; // couldn't be checked: treated as untrusted
        }
    }

    /// <summary>
    /// Creates a fresh, randomly named folder under the trusted data folder that only
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
