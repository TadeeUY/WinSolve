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

    private static readonly Lazy<List<string>> Protected = new(() =>
    {
        var list = new List<string>();
        void Add(string p) { if (!string.IsNullOrEmpty(p)) list.Add(Path.GetFullPath(p).TrimEnd('\\')); }

        foreach (Environment.SpecialFolder f in Enum.GetValues<Environment.SpecialFolder>())
        {
            try { Add(Environment.GetFolderPath(f)); } catch { }
        }
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Add(Path.Combine(pd, "Microsoft"));
        Add(Path.Combine(pf, "WindowsApps"));
        Add(Path.Combine(pf, "dotnet"));
        Add(Path.Combine(pf, "Windows Defender"));
        Add(Path.Combine(pf, "Common Files"));
        if (pf86.Length > 0) Add(Path.Combine(pf86, "Common Files"));
        Add(Path.Combine(Path.GetPathRoot(win)!, "Users"));
        Add(Path.GetPathRoot(win)!);
        return list;
    });

    /// <summary>
    /// True for folders that must never be deleted recursively: Windows, Program Files,
    /// any known shell folder (Documents, Desktop, AppData...), user profile roots, and any
    /// ancestor of those.
    /// </summary>
    public static bool IsProtectedFolder(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return true; }

        if (full.Split('\\').Length < 3) return true; // drive roots and first-level folders

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (IsSameOrInside(full, win)) return true;

        var usersRoot = Path.Combine(Path.GetPathRoot(win)!, "Users");
        if (string.Equals(Path.GetDirectoryName(full), usersRoot, StringComparison.OrdinalIgnoreCase)) return true; // a profile root

        foreach (var p in Protected.Value)
        {
            // The folder is a protected folder, or contains one.
            if (IsSameOrInside(p, full)) return true;
        }

        // Anything inside a Windows/Microsoft-owned location.
        string[] neverInside =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
        ];
        return neverInside.Any(n => IsSameOrInside(full, n));
    }

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
