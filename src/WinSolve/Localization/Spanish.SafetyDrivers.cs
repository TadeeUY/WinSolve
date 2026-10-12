namespace WinSolve.Localization;

public static partial class Spanish
{
    /// <summary>Spanish strings for the drivers, disks and network repair safety fixes.</summary>
    private static readonly Dictionary<string, string> SafetyDrivers = new(StringComparer.Ordinal)
    {
        // ── Graphics driver removal and clean install ──
        ["Windows didn't list the installed graphics drivers. Nothing was removed."] = "Windows no mostró los drivers gráficos instalados. No se quitó nada.",
        ["Backing up the current graphics driver to {0}"] = "Guardando una copia del driver gráfico actual en {0}",
        ["Unexpected driver name {0}. Nothing was removed."] = "Nombre de driver inesperado: {0}. No se quitó nada.",
        ["Could not back up the driver {0} ({1} {2}). Nothing was removed."] = "No se pudo guardar una copia del driver {0} ({1} {2}). No se quitó nada.",
        ["Backup complete: {0} driver package(s) saved in {1}"] = "Copia completa: {0} paquete(s) de drivers guardados en {1}",
        ["Reinstalling the backed-up graphics driver..."] = "Reinstalando el driver gráfico guardado...",
        ["A restore point could not be created (System Protection may be turned off)."] = "No se pudo crear un punto de restauración (puede que la Protección del sistema esté desactivada).",
        ["Continue without a restore point?"] = "¿Continuar sin punto de restauración?",
        ["Stopped: no restore point could be created. Nothing was changed."] = "Detenido: no se pudo crear un punto de restauración. No se cambió nada.",
        ["Continuing without a restore point, as you chose."] = "Continuando sin punto de restauración, como elegiste.",
        ["Removing {0} - {1} {2}"] = "Quitando {0} - {1} {2}",
        ["The graphics driver was removed. A copy is saved in {0}"] = "Se quitó el driver gráfico. Hay una copia guardada en {0}",
        ["To get it back: run the vendor's driver installer, or in Device Manager choose the display adapter > Update driver > Browse my computer, and pick that folder."] =
            "Para recuperarlo: ejecuta el instalador del fabricante o, en el Administrador de dispositivos, elige el adaptador de pantalla > Actualizar controlador > Buscar en mi equipo, y selecciona esa carpeta.",
        ["To get it back: run the vendor's driver installer, or in Device Manager choose the display adapter > Update driver > Browse my computer, and pick the backup folder shown in the log."] =
            "Para recuperarlo: ejecuta el instalador del fabricante o, en el Administrador de dispositivos, elige el adaptador de pantalla > Actualizar controlador > Buscar en mi equipo, y selecciona la carpeta de la copia que aparece en el registro.",
        ["'{0}' is not a full {1} graphics driver package ({2}). Chipset drivers, Driver & Support Assistant, auto-detect tools and web installers can't be used here: download the complete graphics driver installer. Nothing was changed."] =
            "'{0}' no es un paquete completo de drivers gráficos de {1} ({2}). Aquí no sirven los drivers de chipset, Driver & Support Assistant, las herramientas de autodetección ni los instaladores web: descarga el instalador completo del driver gráfico. No se cambió nada.",
        ["'{0}' doesn't look like a {1} graphics driver installer ({2}). Nothing was changed."] = "'{0}' no parece un instalador de drivers gráficos de {1} ({2}). No se cambió nada.",
        ["This PC has more than one graphics adapter ({0}). The {1} one is the integrated GPU: on laptops it usually drives the built-in screen, which can go black or drop to a low resolution until a driver is installed again. Keep an external monitor connected, or know how to start Windows in Safe Mode, before you continue."] =
            "Este PC tiene más de un adaptador gráfico ({0}). El de {1} es la GPU integrada: en portátiles suele manejar la pantalla interna, que puede quedarse en negro o en baja resolución hasta que vuelvas a instalar un driver. Antes de continuar, conecta un monitor externo o asegúrate de saber iniciar Windows en modo seguro.",
        ["WARNING:"] = "ADVERTENCIA:",
        ["The installation was stopped after the old driver was removed."] = "La instalación se detuvo después de quitar el driver anterior.",
        ["The installation failed after the old driver was removed: {0}"] = "La instalación falló después de quitar el driver anterior: {0}",
        ["The new driver is not active, so the previous one is put back."] = "El driver nuevo no está activo, así que se vuelve a poner el anterior.",
        ["The new {0} driver did not install, so WinSolve put the previous driver back. Run the installer again or try another version. Backup: {1}"] =
            "El driver nuevo de {0} no se instaló, así que WinSolve volvió a poner el anterior. Ejecuta de nuevo el instalador o prueba otra versión. Copia: {1}",
        ["The new {0} driver did not install and the previous driver could not be put back automatically. In Device Manager choose the display adapter > Update driver > Browse my computer and pick {1}, or run the installer again."] =
            "El driver nuevo de {0} no se instaló y no se pudo volver a poner el anterior automáticamente. En el Administrador de dispositivos elige el adaptador de pantalla > Actualizar controlador > Buscar en mi equipo y selecciona {1}, o ejecuta de nuevo el instalador.",
        ["The previous graphics driver is active again."] = "El driver gráfico anterior vuelve a estar activo.",
        ["The previous graphics driver was reinstalled; restart the PC to load it."] = "Se reinstaló el driver gráfico anterior; reinicia el PC para cargarlo.",
        ["Could not reinstall the previous graphics driver automatically. Its backup is in {0}"] = "No se pudo reinstalar automáticamente el driver gráfico anterior. Su copia está en {0}",
        ["Could not reinstall the previous graphics driver automatically ({0}). Its backup is in {1}"] = "No se pudo reinstalar automáticamente el driver gráfico anterior ({0}). Su copia está en {1}",
        ["Remove every {0} graphics driver package?"] = "¿Quitar todos los paquetes de drivers gráficos de {0}?",
        [@"2. A copy of the driver is saved in ProgramData\WinSolve\DriverBackup"] = @"2. Se guarda una copia del driver en ProgramData\WinSolve\DriverBackup",
        ["3. The driver is removed: the screen falls back to the basic Microsoft driver (low resolution, no games) until you install one again"] =
            "3. Se quita el driver: la pantalla pasa al driver básico de Microsoft (baja resolución, sin juegos) hasta que vuelvas a instalar uno",
        ["3. Every installed {0} display driver is removed, then the installer opens. The screen will flicker and may change resolution until the new driver is installed."] =
            "3. Se quitan todos los drivers de pantalla de {0} instalados y se abre el instalador. La pantalla parpadeará y puede cambiar de resolución hasta que se instale el driver nuevo.",
        ["If the new driver doesn't end up active, WinSolve puts the saved driver back."] = "Si el driver nuevo no queda activo, WinSolve vuelve a poner el driver guardado.",

        // ── Windows Update drivers ──
        ["Install driver updates"] = "Instalar actualizaciones de drivers",
        ["Driver updates"] = "Actualizaciones de drivers",
        ["Choose the driver updates to install. Optional updates are not checked: install them only if you have a problem they fix. A restore point is created first."] =
            "Elige las actualizaciones de drivers que quieres instalar. Las opcionales no vienen marcadas: instálalas solo si tienes un problema que solucionen. Antes se crea un punto de restauración.",
        ["Driver update"] = "Actualización de driver",
        ["Install"] = "Instalar",
        ["Optional"] = "Opcional",
        ["Install selected"] = "Instalar seleccionadas",
        ["Select at least one driver update."] = "Selecciona al menos una actualización de driver.",
        ["{0} driver update(s) available."] = "{0} actualización(es) de drivers disponibles.",
        ["Windows Update could not list the driver updates."] = "Windows Update no pudo mostrar las actualizaciones de drivers.",
        ["No driver updates selected."] = "No hay actualizaciones de drivers seleccionadas.",
        ["The selected driver updates are no longer offered."] = "Las actualizaciones seleccionadas ya no se ofrecen.",
        ["All selected driver updates installed."] = "Se instalaron todas las actualizaciones seleccionadas.",
        ["Some driver updates did not install. See the details above."] = "Algunas actualizaciones de drivers no se instalaron. Mira los detalles arriba.",

        // ── Devices with errors ──
        ["No devices with problems that a restart or reinstall can fix."] = "No hay dispositivos con problemas que se arreglen reiniciándolos o reinstalándolos.",
        ["Not touched (a restart or reinstall can't fix these):"] = "No se tocan (reiniciarlos o reinstalarlos no los arregla):",
        ["These devices will be repaired:"] = "Se repararán estos dispositivos:",
        ["• {0} (code {1}): restart; if it still fails, remove it so Windows reinstalls it"] = "• {0} (código {1}): reiniciar; si sigue fallando, quitarlo para que Windows lo reinstale",
        ["• {0} (code {1}): restart only"] = "• {0} (código {1}): solo reiniciar",
        ["> Restarting {0} (code {1})"] = "> Reiniciando {0} (código {1})",
        ["> Still failing, not reinstalled (only restarted, to keep this kind of device safe): {0}"] = "> Sigue fallando, no se reinstala (solo se reinicia, para no arriesgar este tipo de dispositivo): {0}",
        ["All the repaired devices are working."] = "Todos los dispositivos reparados funcionan.",
        ["This device has no error any more."] = "Este dispositivo ya no tiene errores.",
        ["WinSolve can't fix this automatically (code {0}): {1}\n\nOpen Device Manager to check the device."] =
            "WinSolve no puede arreglarlo automáticamente (código {0}): {1}\n\nAbre el Administrador de dispositivos para revisar el dispositivo.",

        // ── Disk repair ──
        ["{0} will be checked and repaired on the next restart."] = "{0} se comprobará y reparará en el próximo reinicio.",
        ["The repair of {0} could not be scheduled. Nothing will run at restart."] = "No se pudo programar la reparación de {0}. No se ejecutará nada al reiniciar.",
        ["A surface scan of {0} was already scheduled."] = "Ya había un examen de superficie de {0} programado.",
        ["Could not schedule the scan: {0}"] = "No se pudo programar el examen: {0}",
        ["The surface scan of {0} will run on the next restart (it can take hours, do not turn the PC off)."] = "El examen de superficie de {0} se ejecutará en el próximo reinicio (puede tardar horas, no apagues el PC).",
        ["The surface scan of {0} could not be scheduled. Nothing will run at restart."] = "No se pudo programar el examen de superficie de {0}. No se ejecutará nada al reiniciar.",
        ["{0} is in use by Windows (page file, user profiles or programs): the repair will run on the next restart."] = "Windows está usando {0} (archivo de paginación, perfiles de usuario o programas): la reparación se ejecutará en el próximo reinicio.",
        ["{0} is in use by Windows (page file, user profiles or programs): the surface scan will run on the next restart."] = "Windows está usando {0} (archivo de paginación, perfiles de usuario o programas): el examen de superficie se ejecutará en el próximo reinicio.",
        ["Checks the drive's health, then schedules chkdsk /f (file system repair) for the next boot. Can take a while on large drives."] =
            "Comprueba la salud de la unidad y programa chkdsk /f (reparación del sistema de archivos) para el próximo arranque. Puede tardar en unidades grandes.",
        ["Could not read the drive's health: {0}"] = "No se pudo leer la salud de la unidad: {0}",
        ["The drive's health (S.M.A.R.T.) could not be read. Back up important files before restarting."] = "No se pudo leer la salud de la unidad (S.M.A.R.T.). Haz una copia de tus archivos importantes antes de reiniciar.",
        ["Not scheduled. {0}"] = "No se programó. {0}",
        ["Back up your files first. The Hardware page shows the drive's details and its repair options."] = "Primero haz una copia de tus archivos. La página Hardware muestra los detalles de la unidad y sus opciones de reparación.",
        ["WARNING: {0} Back up important files before restarting."] = "ADVERTENCIA: {0} Haz una copia de tus archivos importantes antes de reiniciar.",

        // ── Network reset ──
        ["Every manual network setting is lost: static IP addresses, gateways and DNS servers go back to automatic (DHCP). VPN clients, firewalls and security software that hook into the network may need to be reinstalled. The current settings are saved first in ProgramData\\WinSolve\\NetworkBackup."] =
            "Se pierde toda la configuración de red manual: las IP estáticas, puertas de enlace y servidores DNS vuelven a automático (DHCP). Puede que tengas que reinstalar clientes VPN, firewalls y programas de seguridad que se integran en la red. Antes se guarda la configuración actual en ProgramData\\WinSolve\\NetworkBackup.",
        ["This PC uses a static IP address. Write these settings down, you will have to enter them again after the reset:"] =
            "Este PC usa una dirección IP estática. Anota estos datos, tendrás que volver a introducirlos después del restablecimiento:",
        ["> Saving the current network settings..."] = "> Guardando la configuración de red actual...",
        ["netsh could not export the network configuration."] = "netsh no pudo exportar la configuración de red.",
        ["Static IP settings that the reset will remove: {0}"] = "Configuración de IP estática que el restablecimiento quitará: {0}",
        ["Network settings saved in {0}"] = "Configuración de red guardada en {0}",
        ["Could not save the network settings: {0}"] = "No se pudo guardar la configuración de red: {0}",
        ["The network settings could not be saved, so nothing was reset."] = "No se pudo guardar la configuración de red, así que no se restableció nada.",

        // ── Windows Update reset and alerts ──
        ["Updates already downloaded are downloaded again and the update history in Settings starts empty. Don't run it while Windows is installing updates."] =
            "Las actualizaciones ya descargadas se vuelven a descargar y el historial de actualizaciones de Configuración empieza vacío. No lo ejecutes mientras Windows instala actualizaciones.",
        ["Windows usually retries it by itself. If it keeps failing, WinSolve will suggest resetting Windows Update."] =
            "Windows suele reintentarlo por sí solo. Si sigue fallando, WinSolve te sugerirá restablecer Windows Update.",
        ["Windows updates keep failing to install"] = "Las actualizaciones de Windows siguen fallando al instalarse",
        ["Skipped: this task needs your confirmation. Run it from Cleanup & repair."] = "Omitida: esta tarea necesita tu confirmación. Ejecútala desde Limpieza y reparación.",

        // ── Review fixes ──
        ["Waiting for the installer to close before going on (it is not stopped mid-install)..."] = "Esperando a que el instalador se cierre antes de seguir (no se detiene a mitad de la instalación)...",
        ["The installer is still running, so the previous driver is not put back automatically. If the screen stays on the basic driver, in Device Manager choose the display adapter > Update driver > Browse my computer and pick {0}"] =
            "El instalador sigue en marcha, así que el driver anterior no se vuelve a poner automáticamente. Si la pantalla se queda con el driver básico, en el Administrador de dispositivos elige el adaptador de pantalla > Actualizar controlador > Buscar en mi equipo y selecciona {0}",
        ["A {0} driver is active, so the backup is not reinstalled. Backup: {1}"] = "Hay un driver de {0} activo, así que no se reinstala la copia. Copia: {1}",
        ["Windows' boot check list has an unexpected format, so WinSolve doesn't change it."] = "La lista de comprobaciones de arranque de Windows tiene un formato inesperado, así que WinSolve no la cambia.",
        ["Another check of {0} is already scheduled for the next restart. Restart once to let it run, then schedule the surface scan again."] =
            "Ya hay otra comprobación de {0} programada para el próximo reinicio. Reinicia una vez para que se ejecute y luego vuelve a programar el examen de superficie.",
    };
}
