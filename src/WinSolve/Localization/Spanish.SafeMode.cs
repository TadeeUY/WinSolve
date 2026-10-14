namespace WinSolve.Localization;

public static partial class Spanish
{
    /// <summary>Spanish strings for the Safe Mode tool.</summary>
    private static readonly Dictionary<string, string> SafeModeStrings = new(StringComparer.Ordinal)
    {
        ["Safe Mode"] = "Modo seguro",
        ["Restarts Windows in Safe Mode once, to fix problems that don't let it work normally."] = "Reinicia Windows en modo seguro una sola vez, para arreglar problemas que no lo dejan funcionar normal.",
        ["Windows starts with only the basics, so you can remove a driver, program or virus that stops it from working normally."] = "Windows arranca solo con lo básico, para que puedas quitar un driver, programa o virus que no lo deja funcionar normal.",
        ["Only the basic drivers and services. No internet."] = "Solo los drivers y servicios básicos. Sin internet.",
        ["Safe Mode with Networking"] = "Modo seguro con funciones de red",
        ["The same, plus internet (to download a driver or run an antivirus)."] = "Lo mismo, más internet (para bajar un driver o pasar un antivirus).",
        ["• Only the next start is in Safe Mode. The one after that is normal again, even if you don't open WinSolve."] = "• Solo el próximo arranque es en modo seguro. El siguiente ya vuelve a ser normal, aunque no abras WinSolve.",
        ["• Save your work first: the PC restarts right away."] = "• Guarda lo que estés haciendo antes: la PC se reinicia enseguida.",
        ["• If your PIN or fingerprint doesn't work there, sign in with your account password (for a Microsoft account, its email password)."] = "• Si ahí no funciona el PIN o la huella, entra con la contraseña de tu cuenta (si es cuenta Microsoft, la contraseña de su correo).",
        ["• If BitLocker is on, it is paused for that one restart so Windows doesn't ask for the recovery key."] = "• Si BitLocker está activado, se pausa solo para ese reinicio, así Windows no te pide la clave de recuperación.",
        ["Restart in Safe Mode"] = "Reiniciar en modo seguro",
        ["Advanced startup"] = "Inicio avanzado",
        ["Restart normally"] = "Reiniciar normalmente",
        ["Windows is running in Safe Mode right now."] = "Windows está funcionando en modo seguro ahora mismo.",
        ["When you're done, restart normally. WinSolve also turns off Safe Mode if it was turned on with System Configuration (msconfig), so the PC doesn't keep starting in it."] = "Cuando termines, reinicia normalmente. WinSolve también apaga el modo seguro si se activó con Configuración del sistema (msconfig), para que la PC no siga arrancando así.",
        ["The PC will restart normally now. Save your work and close your programs first.\n\nRestart now?"] = "La PC se va a reiniciar normalmente ahora. Guarda lo que estés haciendo y cierra tus programas antes.\n\n¿Reiniciar ahora?",
        ["The PC will restart in Safe Mode now. Save your work and close your programs first.\n\nRestart now?"] = "La PC se va a reiniciar en modo seguro ahora. Guarda lo que estés haciendo y cierra tus programas antes.\n\n¿Reiniciar ahora?",
        ["The PC will restart into the blue Windows recovery menu (Advanced startup). From there you can choose Troubleshoot > Advanced options > Startup Settings, System Restore or Startup Repair.\n\nSave your work first. Restart now?"] = "La PC se va a reiniciar en el menú azul de recuperación de Windows (Inicio avanzado). Desde ahí puedes elegir Solucionar problemas > Opciones avanzadas > Configuración de inicio, Restaurar sistema o Reparación de inicio.\n\nGuarda lo que estés haciendo antes. ¿Reiniciar ahora?",
        ["Preparing a one-time Safe Mode start..."] = "Preparando un arranque en modo seguro por única vez...",
        ["Restarting into Safe Mode..."] = "Reiniciando en modo seguro...",
        ["Restarting normally..."] = "Reiniciando normalmente...",
        ["Removed the temporary Safe Mode boot entry."] = "Se quitó la entrada temporal de arranque en modo seguro.",
        ["BitLocker paused for this one restart (it turns back on by itself)."] = "BitLocker pausado solo para este reinicio (se vuelve a activar solo).",
        ["Windows is already running in Safe Mode."] = "Windows ya está funcionando en modo seguro.",
        ["Windows could not prepare the Safe Mode start (bcdedit). Nothing was changed."] = "Windows no pudo preparar el arranque en modo seguro (bcdedit). No se cambió nada.",
        ["Windows could not prepare the Safe Mode start (bcdedit). The boot menu was left as it was."] = "Windows no pudo preparar el arranque en modo seguro (bcdedit). El menú de arranque quedó como estaba.",
        ["BitLocker could not be paused for the restart, so Windows might ask for the recovery key. Nothing was changed."] = "No se pudo pausar BitLocker para el reinicio, así que Windows podría pedir la clave de recuperación. No se cambió nada.",
        ["Windows did not accept the restart."] = "Windows no aceptó el reinicio.",
    };
}
