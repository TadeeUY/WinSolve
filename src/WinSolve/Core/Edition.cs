namespace WinSolve.Core;

/// <summary>
/// Which build this is. The portable build (WinSolve-Portable.exe) carries its own .NET runtime,
/// runs from a USB stick without installing and leaves nothing scheduled on the PC: no Start
/// with Windows, no scheduled maintenance and no self-installing updates.
/// </summary>
public static class Edition
{
#if PORTABLE
    public static readonly bool IsPortable = true;
#else
    public static readonly bool IsPortable = false;
#endif
}
