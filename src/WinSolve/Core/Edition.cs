namespace WinSolve.Core;

/// <summary>
/// Which build this is. The portable build (WinSolve-Portable.exe) carries its own .NET runtime,
/// runs from a USB stick without installing and leaves nothing on the PC: no Start with Windows,
/// no scheduled maintenance, no self-installing updates, and its settings, logs and backups stay
/// next to the .exe (see <see cref="DataLocation"/>). Any build runs portable when a
/// <c>WinSolve.portable</c> file sits next to its .exe.
/// </summary>
public static class Edition
{
#if PORTABLE
    public const bool IsPortableBuild = true;
#else
    public const bool IsPortableBuild = false;
#endif

    public static readonly bool IsPortable = IsPortableBuild || HasPortableMarker(DataLocation.ExeDirectory);

    internal static bool HasPortableMarker(string exeDirectory)
    {
        try { return File.Exists(Path.Combine(exeDirectory, DataLocation.PortableMarker)); }
        catch { return false; }
    }
}
