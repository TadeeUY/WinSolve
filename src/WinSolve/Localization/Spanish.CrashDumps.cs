namespace WinSolve.Localization;

public static partial class Spanish
{
    /// <summary>Spanish strings for the blue screen dumps on the Hardware page.</summary>
    private static readonly Dictionary<string, string> CrashDumpStrings = new(StringComparer.Ordinal)
    {
        ["CPU sensor (LibreHardwareMonitor)"] = "Sensor de la CPU (LibreHardwareMonitor)",
        ["CPU sensor (OpenHardwareMonitor)"] = "Sensor de la CPU (OpenHardwareMonitor)",
        ["ACPI thermal zone (approximate)"] = "Zona térmica ACPI (aproximada)",
        ["Stop code"] = "Código de error",
        ["Probable cause"] = "Causa probable",
        ["What to do"] = "Qué hacer",
        ["Not in the dump"] = "El volcado no lo indica",
        ["Windows kernel ({0})"] = "Núcleo de Windows ({0})",
        ["The crash happened inside {0}. Update or reinstall the driver or program it belongs to."] = "El fallo ocurrió dentro de {0}. Actualiza o reinstala el driver o programa al que pertenece.",
        ["The graphics driver stopped responding. Do a clean install of the graphics driver (Drivers page) and check GPU temperatures."] = "El driver de video dejó de responder. Haz una instalación limpia del driver de video (página Drivers) y revisa las temperaturas de la GPU.",
        ["The processor or motherboard reported a hardware error. Check temperatures, remove overclocking and update the BIOS."] = "El procesador o la placa madre informaron un error de hardware. Revisa las temperaturas, quita el overclock y actualiza la BIOS.",
        ["Often bad RAM or a faulty driver. Run the Windows Memory Diagnostic (Cleanup & repair) and update drivers."] = "Suele ser memoria RAM dañada o un driver con fallas. Ejecuta el Diagnóstico de memoria de Windows (Limpieza y reparación) y actualiza los drivers.",
        ["Problem reading the disk. Check the disk's health and run a file system repair (Hardware > Disks)."] = "Problema al leer el disco. Revisa la salud del disco y repara el sistema de archivos (Hardware > Discos).",
        ["A device didn't handle sleep or power changes. Update chipset, network and graphics drivers."] = "Un dispositivo no manejó bien la suspensión o los cambios de energía. Actualiza los drivers del chipset, de red y de video.",
        ["Something kept a processor busy too long, usually a storage or chipset driver, or the BIOS. Update them."] = "Algo mantuvo ocupado un procesador demasiado tiempo, normalmente un driver de almacenamiento o del chipset, o la BIOS. Actualízalos.",
        ["A critical Windows process stopped. Repair the system files (Cleanup & repair > SFC and DISM)."] = "Se detuvo un proceso crítico de Windows. Repara los archivos del sistema (Limpieza y reparación > SFC y DISM).",
        ["Update drivers and Windows. If it keeps happening, the crash dump can be opened with WinDbg for a full analysis."] = "Actualiza los drivers y Windows. Si sigue pasando, el volcado se puede abrir con WinDbg para un análisis completo.",
    };
}
