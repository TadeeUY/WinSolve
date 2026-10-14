using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>
/// Restarts into Safe Mode for one boot only. Instead of turning on Safe Mode for the normal
/// Windows entry (msconfig's way, which keeps starting in Safe Mode until someone turns it off),
/// WinSolve copies the entry, marks the copy as Safe Mode, hides it from the boot menu and boots
/// it once with "bootsequence". The restart after that is a normal one on its own, even if
/// WinSolve never runs again; the next time WinSolve starts normally it deletes the copy.
/// </summary>
public static partial class SafeMode
{
    public const string EntryDescription = "WinSolve Safe Mode";
    private const string StateKey = @"SOFTWARE\WinSolve";
    private const string StateValue = "SafeModeEntry";

    [GeneratedRegex(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}")]
    private static partial Regex GuidPattern();

    /// <summary>True when Windows was started in Safe Mode (with or without networking).</summary>
    public static bool IsRunningInSafeMode => SystemInformation.BootMode != BootMode.Normal;

    /// <summary>The entry id bcdedit prints after "/copy" (the message itself is translated by Windows).</summary>
    internal static string? ParseEntryId(string bcdeditOutput)
    {
        var m = GuidPattern().Match(bcdeditOutput);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Restarts the PC into Safe Mode (only the next start).</summary>
    public static async Task RestartIntoSafeModeAsync(bool networking, Action<string> log, CancellationToken ct)
    {
        var restarting = false;
        try
        {
            await PrepareAsync(networking, log, ct);
            log("Restarting into Safe Mode...");
            await ShutdownAsync("/r /t 5 /c \"WinSolve: restarting into Safe Mode\"", log, ct);
            restarting = true;
        }
        finally
        {
            // Anything failed before the restart: leave the boot menu exactly as it was.
            if (!restarting) await UndoAsync(log);
        }
    }

    /// <summary>Everything except the restart itself; returns the id of the one-time entry.</summary>
    internal static async Task<string> PrepareAsync(bool networking, Action<string> log, CancellationToken ct)
    {
        if (IsRunningInSafeMode) throw new InvalidOperationException("Windows is already running in Safe Mode.");
        await CleanupAsync(log); // a copy left by an earlier attempt that never restarted

        log("Preparing a one-time Safe Mode start...");
        var copy = await BcdAsync($"/copy {{current}} /d \"{EntryDescription}\"", ct);
        var id = copy.Success ? ParseEntryId(copy.Output) : null;
        if (id is null)
        {
            Logger.Write("bcdedit /copy: " + copy.Output.Trim());
            throw new InvalidOperationException("Windows could not prepare the Safe Mode start (bcdedit). Nothing was changed.");
        }
        Reg.Set(RegistryHive.LocalMachine, StateKey, StateValue, id, RegistryValueKind.String);

        await RequireAsync($"/set {id} safeboot {(networking ? "network" : "minimal")}", log, ct);
        await RequireAsync($"/displayorder {id} /remove", log, ct);
        await SuspendBitLockerOnceAsync(log, ct);
        await RequireAsync($"/bootsequence {id}", log, ct);
        return id;
    }

    /// <summary>Takes back a prepared Safe Mode start that did not restart.</summary>
    internal static async Task UndoAsync(Action<string>? log)
    {
        await BcdAsync("/deletevalue {bootmgr} bootsequence", default);
        await CleanupAsync(log);
    }

    /// <summary>
    /// Run from Safe Mode: makes sure the next start is a normal one, also when Safe Mode was
    /// turned on with msconfig (which keeps it on until it is turned off).
    /// </summary>
    public static async Task RestartNormallyAsync(Action<string> log, CancellationToken ct)
    {
        // Not set is fine (bcdedit then reports an error that is ignored).
        await BcdAsync("/deletevalue {current} safeboot", ct);
        await BcdAsync("/deletevalue {current} safebootalternateshell", ct);
        await BcdAsync("/deletevalue {default} safeboot", ct);
        await BcdAsync("/deletevalue {default} safebootalternateshell", ct);
        log("Restarting normally...");
        await ShutdownAsync("/r /t 5 /c \"WinSolve: restarting normally\"", log, ct);
    }

    /// <summary>Restarts into the blue Windows recovery menu (Advanced startup).</summary>
    public static Task AdvancedStartupAsync(Action<string> log, CancellationToken ct)
        => ShutdownAsync("/r /o /t 5 /c \"WinSolve: restarting to Advanced startup\"", log, ct);

    private static async Task ShutdownAsync(string args, Action<string> log, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync("shutdown.exe", args, log, ct);
        if (!r.Success) throw new InvalidOperationException("Windows did not accept the restart.");
    }

    /// <summary>
    /// Deletes the boot entry WinSolve created, once Windows started normally again. Only an entry
    /// that still carries WinSolve's description is deleted, never the normal Windows entry.
    /// </summary>
    public static async Task CleanupAsync(Action<string>? log = null)
    {
        try
        {
            if (Reg.Get(RegistryHive.LocalMachine, StateKey, StateValue) is not { } stored) return;
            if (stored is not string id || ParseEntryId(id) != id) { Forget(); return; }
            if (IsRunningInSafeMode) return; // possibly the entry in use right now
            var entry = await BcdAsync($"/enum {id}", default);
            // Already gone, or no longer WinSolve's copy: never delete it, just forget it.
            if (!entry.Success || !entry.Output.Contains(EntryDescription, StringComparison.Ordinal)) { Forget(); return; }
            var current = await BcdAsync("/enum {current} /v", default);
            if (!current.Success || current.Output.Contains(id, StringComparison.OrdinalIgnoreCase)) return;
            if ((await BcdAsync($"/delete {id}", default)).Success)
            {
                log?.Invoke("Removed the temporary Safe Mode boot entry.");
                Forget();
            }
        }
        catch (Exception ex)
        {
            Logger.Write($"Safe Mode cleanup: {ex.Message}");
        }
    }

    private static void Forget() => Reg.Delete(RegistryHive.LocalMachine, StateKey, StateValue);

    private static Task<ProcessResult> BcdAsync(string args, CancellationToken ct)
    {
        Logger.Write("bcdedit " + args);
        return ProcessRunner.RunAsync("bcdedit.exe", args, null, ct);
    }

    private static async Task RequireAsync(string args, Action<string> log, CancellationToken ct)
    {
        var r = await BcdAsync(args, ct);
        if (!r.Success)
        {
            log(r.Output.Trim());
            throw new InvalidOperationException("Windows could not prepare the Safe Mode start (bcdedit). The boot menu was left as it was.");
        }
    }

    /// <summary>
    /// Changing the boot settings can make BitLocker ask for the recovery key. Like Windows Update
    /// does, protection is paused for exactly one restart and turns back on by itself.
    /// </summary>
    private static async Task SuspendBitLockerOnceAsync(Action<string> log, CancellationToken ct)
    {
        const string script = """
            try {
              $v = Get-CimInstance -Namespace root\CIMV2\Security\MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -Filter "DriveLetter='$env:SystemDrive'" -ErrorAction Stop
            } catch { 'BITLOCKER:NONE'; return }
            if (-not $v) { 'BITLOCKER:NONE'; return }
            if ((Invoke-CimMethod -InputObject $v -MethodName GetProtectionStatus).ProtectionStatus -ne 1) { 'BITLOCKER:OFF'; return }
            $r = Invoke-CimMethod -InputObject $v -MethodName DisableKeyProtectors -Arguments @{ DisableCount = [uint32]1 }
            "BITLOCKER:$($r.ReturnValue)"
            """;
        var r = await ProcessRunner.PowerShellAsync(script, null, ct);
        var status = r.Output.Split('\n', StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith("BITLOCKER:", StringComparison.Ordinal)) ?? "BITLOCKER:NONE";
        switch (status)
        {
            case "BITLOCKER:NONE" or "BITLOCKER:OFF":
                return;
            case "BITLOCKER:0":
                log("BitLocker paused for this one restart (it turns back on by itself).");
                return;
            default:
                throw new InvalidOperationException("BitLocker could not be paused for the restart, so Windows might ask for the recovery key. Nothing was changed.");
        }
    }
}
