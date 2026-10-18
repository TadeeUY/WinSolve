namespace WinSolve.Core;

/// <summary>How the portable edition stores its data on this run.</summary>
public enum DataMode
{
    /// <summary>Installed: everything in %ProgramData%\WinSolve.</summary>
    Installed,

    /// <summary>Portable on NTFS/ReFS: everything in an administrators-only WinSolveData folder next to the .exe.</summary>
    BesideExe,

    /// <summary>
    /// Portable on a drive without permissions (FAT32/exFAT USB stick): settings and logs next to
    /// the .exe; the backups WinSolve puts back as administrator (DNS, services, startup entries,
    /// drivers) and the installers it runs elevated go to an administrators-only folder that is
    /// removed on exit when nothing is left to undo.
    /// </summary>
    Split,

    /// <summary>Portable where the .exe folder can't be used (read-only, network share): everything in the fallback folder.</summary>
    Fallback,
}

/// <summary>
/// Where WinSolve keeps its own files. Two kinds:
/// <list type="bullet">
/// <item><b>Trusted</b>: data WinSolve acts on elevated (Undo backups, installers it runs). Always
/// in a folder only Administrators and SYSTEM can change, or a standard user could plant a backup
/// that WinSolve would then apply as administrator.</item>
/// <item><b>User</b>: settings, logs, activity and history, which are only displayed or decide
/// options the user still confirms.</item>
/// </list>
/// The installed edition keeps both in %ProgramData%\WinSolve. The portable edition keeps them
/// next to the .exe when that is safe, so it leaves nothing on the PC.
/// </summary>
public static class DataLocation
{
    /// <summary>A file with this name next to WinSolve.exe makes any build run in portable mode.</summary>
    public const string PortableMarker = "WinSolve.portable";

    /// <summary>Folder next to the portable .exe that holds its data.</summary>
    public const string PortableFolderName = "WinSolveData";

    internal static string ExeDirectory => Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>The installed edition's folder.</summary>
    public static string InstalledFolder => Path.Combine(ProgramData, "WinSolve");

    /// <summary>
    /// Trusted data of the portable edition when it can't live next to the .exe. A separate name, so
    /// a portable copy never reads, changes or removes an installed WinSolve's data on the same PC.
    /// </summary>
    public static string PortableFallbackFolder => Path.Combine(ProgramData, "WinSolve-Portable");

    public static string BesideExeFolder => Path.Combine(ExeDirectory, PortableFolderName);

    /// <summary>File systems without permissions, links or hard links (nothing can be redirected there).</summary>
    internal static bool IsLinklessFileSystem(string? fileSystem)
        => fileSystem is not null && (fileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase)
                                      || fileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The storage mode for a run. <paramref name="besideExeUsable"/> says whether the folder next
    /// to the .exe could be created (and, on a file system with permissions, secured for administrators).
    /// </summary>
    internal static DataMode Decide(bool portable, string? fileSystem, bool besideExeUsable)
    {
        if (!portable) return DataMode.Installed;
        if (!besideExeUsable) return DataMode.Fallback;
        return IsLinklessFileSystem(fileSystem) ? DataMode.Split : DataMode.BesideExe;
    }

    /// <summary>The trusted and the user folder for a mode.</summary>
    internal static (string Trusted, string User) Folders(DataMode mode, string installed, string besideExe, string fallback) => mode switch
    {
        DataMode.Installed => (installed, installed),
        DataMode.BesideExe => (besideExe, besideExe),
        DataMode.Split => (fallback, besideExe),
        _ => (fallback, fallback),
    };

    private static readonly Lazy<DataMode> ModeLazy = new(ResolveMode);

    /// <summary>The mode of this run (decided once, on first use).</summary>
    public static DataMode Mode => ModeLazy.Value;

    private static DataMode ResolveMode()
    {
        if (!Edition.IsPortable) return DataMode.Installed;
        var fileSystem = FileSystemOf(ExeDirectory);
        var usable = false;
        try
        {
            if (IsLinklessFileSystem(fileSystem))
            {
                Directory.CreateDirectory(BesideExeFolder);
                usable = true;
            }
            else if (fileSystem is not null)
            {
                // Same protection as %ProgramData%\WinSolve: owned by Administrators, no links.
                if (Admin.IsElevated) SafePath.MakeTrusted(BesideExeFolder);
                else Directory.CreateDirectory(BesideExeFolder);
                usable = true;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteEarly($"Portable: {BesideExeFolder} can't be used ({ex.Message}); using {PortableFallbackFolder}.");
        }
        var mode = Decide(portable: true, fileSystem, usable);
        Logger.WriteEarly($"Portable mode: {mode} (file system {fileSystem ?? "unknown"}).");
        return mode;
    }

    /// <summary>"NTFS", "FAT32", "exFAT"... of the drive holding <paramref name="path"/>; null for network paths or when unknown.</summary>
    private static string? FileSystemOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return new DriveInfo(root).DriveFormat;
        }
        catch { return null; }
    }

    private static (string Trusted, string User) Current => Folders(Mode, InstalledFolder, BesideExeFolder, PortableFallbackFolder);

    /// <summary>Path of the trusted folder, without creating it (use <see cref="SafePath.DataFolder"/> to write).</summary>
    public static string TrustedPath => Current.Trusted;

    /// <summary>Settings, logs, activity and history.</summary>
    public static string UserFolder => UserFolderLazy.Value;

    private static readonly Lazy<string> UserFolderLazy = new(() =>
    {
        var (trusted, user) = Current;
        if (user == trusted) return SafePath.DataFolder;
        try { Directory.CreateDirectory(user); }
        catch (Exception ex) { Logger.WriteEarly($"Could not create {user}: {ex.Message}"); }
        return user;
    });

    /// <summary>Every folder that holds WinSolve's own data on this PC (the uninstaller check uses it).</summary>
    public static IEnumerable<string> AllKnownFolders()
    {
        yield return InstalledFolder;
        yield return PortableFallbackFolder;
        if (Edition.IsPortable) yield return BesideExeFolder;
    }

    /// <summary>Working folders that only matter while WinSolve runs (installers, reports).</summary>
    internal static readonly string[] TemporaryFolders = ["Updates", "Drivers", "Report", "Battery", "Tasks"];

    /// <summary>
    /// Portable edition, on exit: removes the working folders and, when the fallback folder holds
    /// nothing left to undo, the fallback folder itself, so the PC keeps no trace of the stick.
    /// </summary>
    public static void CleanUpOnExit()
    {
        if (Mode == DataMode.Installed) return;
        RemoveExtractedRuntimeAfterExit();
        try
        {
            CleanUp(TrustedPath, removeWhenEmpty: Mode != DataMode.BesideExe);
        }
        catch (Exception ex)
        {
            Logger.Write($"Portable cleanup: {ex.Message}");
        }
    }

    /// <summary>
    /// The self-contained portable .exe unpacks a few runtime files into %TEMP%\.net\WinSolve-Portable
    /// on start. They are in use until the process ends, so a hidden cmd removes them right after.
    /// </summary>
    public static void RemoveExtractedRuntimeAfterExit()
    {
        if (!Edition.IsPortableBuild || Environment.ProcessPath is not { } exe) return;
        try
        {
            var baseDir = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } custom
                ? custom : Path.Combine(Path.GetTempPath(), ".net");
            var dir = new DirectoryInfo(Path.Combine(baseDir, Path.GetFileNameWithoutExtension(exe)));
            // Never follow a link someone put there.
            if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0 || dir.FullName.Contains('"')) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ProcessRunner.Resolve("cmd.exe"),
                $"/d /c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{dir.FullName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Write($"Portable: could not schedule removing the unpacked runtime: {ex.Message}");
        }
    }

    internal static void CleanUp(string root, bool removeWhenEmpty)
    {
        var info = new DirectoryInfo(root);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return;
        foreach (var name in TemporaryFolders)
        {
            var dir = new DirectoryInfo(Path.Combine(root, name));
            if (!dir.Exists) continue;
            // A link is removed itself, never followed.
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) dir.Delete();
            else dir.Delete(recursive: true);
        }
        if (removeWhenEmpty && !info.EnumerateFiles("*", SafePath.NoLinks(recursive: true)).Any())
        {
            info.Delete(recursive: true);
            Logger.Write($"Portable: nothing left to undo, removed {root}.");
        }
    }
}
