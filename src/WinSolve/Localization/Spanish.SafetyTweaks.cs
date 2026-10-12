namespace WinSolve.Localization;

public static partial class Spanish
{
    /// <summary>Spanish strings for the tweaks and system settings safety fixes.</summary>
    private static readonly Dictionary<string, string> SafetyTweaks = new(StringComparer.Ordinal)
    {
        // ── Tweak descriptions and warnings ──
        ["Turns off the Windows Copilot sidebar for your account. The newer Copilot app is a regular app this setting doesn't remove: uninstall it from Apps if you don't want it."] = "Desactiva la barra lateral de Windows Copilot en tu cuenta. La app de Copilot más nueva es una app normal que este ajuste no quita: desinstálala desde Aplicaciones si no la quieres.",
        ["Shows technical parameters on blue screens. The PC still restarts automatically afterwards (the details are also saved in the crash dump)."] = "Muestra datos técnicos en los pantallazos azules. El PC se sigue reiniciando solo después (los detalles también se guardan en el volcado de memoria).",
        ["Turns off Edge's sidebar, shopping assistant, startup boost, first-run screens, recommendations and diagnostic data (Edge stays installed). These are policies, so Edge will show \"Managed by your organization\"."] = "Desactiva la barra lateral, el asistente de compras, el arranque acelerado, las pantallas de bienvenida, las recomendaciones y los datos de diagnóstico de Edge (Edge sigue instalado). Son directivas, así que Edge mostrará \"Administrado por tu organización\".",
        ["Turns IPv6 off on every adapter. Only useful for specific network problems. Requires a restart."] = "Apaga IPv6 en todos los adaptadores. Solo sirve para problemas de red puntuales. Requiere reiniciar.",
        ["Microsoft doesn't support disabling IPv6, and parts of Windows expect it. It breaks DirectAccess, Always On VPN and IP-HTTPS, can leave you without internet on providers that use DS-Lite, 464XLAT or IPv6-only connections, and breaks Xbox features that use Teredo. Requires a restart."] = "Microsoft no admite desactivar IPv6 y partes de Windows cuentan con él. Rompe DirectAccess, Always On VPN e IP-HTTPS, puede dejarte sin internet con proveedores que usan DS-Lite, 464XLAT o conexiones solo IPv6, y rompe funciones de Xbox que usan Teredo. Requiere reiniciar.",
        ["Only for PCs that dual-boot with Linux set to keep the hardware clock in UTC. On any other PC the clock will be off by your time zone's difference from UTC."] = "Solo para PCs con arranque dual con un Linux que guarda el reloj del hardware en UTC. En cualquier otro PC la hora quedará desfasada tanto como la diferencia de tu zona horaria con UTC.",

        // ── Services to Manual ──
        ["Some services could not be changed:"] = "Algunos servicios no se pudieron cambiar:",
        ["Some services could not be restored:"] = "Algunos servicios no se pudieron restaurar:",
        ["The saved startup types of these services (services-backup.json in WinSolve's data folder) are missing, so Undo can't restore them exactly. If you need one of them, set it back to Automatic in Services (services.msc)."] = "Falta la copia del tipo de inicio original de estos servicios (services-backup.json en la carpeta de datos de WinSolve), así que Deshacer no puede restaurarlos exactamente. Si necesitas alguno, vuelve a ponerlo en Automático en Servicios (services.msc).",
        ["The saved startup types of these services (services-backup.json in WinSolve's data folder) are damaged, so Undo can't restore them. If you need one of them, set it back to Automatic in Services (services.msc)."] = "La copia del tipo de inicio original de estos servicios (services-backup.json en la carpeta de datos de WinSolve) está dañada, así que Deshacer no puede restaurarlos. Si necesitas alguno, vuelve a ponerlo en Automático en Servicios (services.msc).",

        // ── OneDrive ──
        ["OneDrive isn't installed for this account, so there is nothing to remove."] = "OneDrive no está instalado en esta cuenta, así que no hay nada que quitar.",
        ["OneDrive is backing up these folders: {0}. They now live inside the OneDrive folder, so WinSolve won't remove OneDrive. First stop backing them up in OneDrive (Settings > Sync and backup > Manage back up), then try again."] = "OneDrive está haciendo copia de seguridad de estas carpetas: {0}. Ahora están dentro de la carpeta de OneDrive, así que WinSolve no quitará OneDrive. Primero deja de hacer su copia en OneDrive (Configuración > Sincronización y copia de seguridad > Administrar copia de seguridad) y vuelve a intentarlo.",
        ["OneDrive's uninstaller (OneDriveSetup.exe) wasn't found, so nothing was changed. Uninstall OneDrive from Settings > Apps > Installed apps instead."] = "No se encontró el desinstalador de OneDrive (OneDriveSetup.exe), así que no se cambió nada. Desinstala OneDrive desde Configuración > Aplicaciones > Aplicaciones instaladas.",
        ["OneDrive is still installed: its uninstaller didn't remove it. Uninstall it from Settings > Apps > Installed apps instead."] = "OneDrive sigue instalado: su desinstalador no lo quitó. Desinstálalo desde Configuración > Aplicaciones > Aplicaciones instaladas.",
        ["Desktop"] = "Escritorio",
        ["Documents"] = "Documentos",
        ["Pictures"] = "Imágenes",
        ["Music"] = "Música",
        ["Videos"] = "Vídeos",

        // ── Organization-managed PCs ──
        ["This PC is joined to an organization's domain."] = "Este PC está unido al dominio de una organización.",
        ["This PC is managed by an organization (device management)."] = "Este PC lo administra una organización (administración de dispositivos).",
        ["These tweaks change policy settings, which the organization can overwrite or which can conflict with its own settings:"] = "Estos ajustes cambian directivas, que la organización puede sobrescribir o que pueden chocar con su propia configuración:",

        // ── Restore points ──
        ["System Protection is off on {0}; turning it on to create the restore point."] = "La Protección del sistema está desactivada en {0}; se activa para crear el punto de restauración.",
        ["System Protection was turned on for {0}."] = "Se activó la Protección del sistema en {0}.",
        ["System Protection was turned off on {0}. WinSolve turned it on to create the restore point; restore points use some disk space. You can turn it off again in System Properties > System Protection."] = "La Protección del sistema estaba desactivada en {0}. WinSolve la activó para crear el punto de restauración; los puntos de restauración ocupan algo de espacio en disco. Puedes volver a desactivarla en Propiedades del sistema > Protección del sistema.",
        ["Stopped: nothing was changed because no restore point could be created."] = "Detenido: no se cambió nada porque no se pudo crear un punto de restauración.",
        ["Continuing without a restore point."] = "Continuando sin punto de restauración.",
        ["• A System Restore point is created first, so settings changes can be undone. Deleted files (temporary files, caches) can't be brought back."] = "• Primero se crea un punto de restauración, así los cambios de configuración se pueden deshacer. Los archivos borrados (temporales, cachés) no se pueden recuperar.",

        // ── Explorer ──
        ["Explorer notified of the changes."] = "Se avisó al Explorador de los cambios.",
        ["Could not notify Explorer: {0}"] = "No se pudo avisar al Explorador: {0}",
        ["Some changes appear after restarting Explorer (open folder windows will close). Restart Explorer now?"] = "Algunos cambios se ven tras reiniciar el Explorador (se cerrarán las ventanas de carpetas abiertas). ¿Reiniciar el Explorador ahora?",

        // ── One-click profile ──
        ["Desktop: Ultimate Performance power plan. A battery was found, so hibernation and Fast Startup stay on."] = "Escritorio: plan Máximo rendimiento. Se encontró una batería, así que la hibernación y el inicio rápido siguen activos.",

        // ── DNS ──
        ["Changes the DNS servers of the connected network adapters (never on a work domain network). The current settings are saved first; choose Default (DHCP) to put them back. Cloudflare and Google are fast; Quad9 and the filtering options block malicious sites."] = "Cambia los servidores DNS de los adaptadores de red conectados (nunca en una red de dominio del trabajo). Antes se guarda la configuración actual; elige Predeterminado (DHCP) para volver a ella. Cloudflare y Google son rápidos; Quad9 y las opciones con filtro bloquean sitios maliciosos.",
        ["Could not read the network adapters."] = "No se pudieron leer los adaptadores de red.",
        ["Not changed, because they are on a network managed by your organization (domain): {0}"] = "No se cambian porque están en una red administrada por tu organización (dominio): {0}",
        ["There is no connected network adapter whose DNS WinSolve can change."] = "No hay ningún adaptador de red conectado cuyo DNS pueda cambiar WinSolve.",
        ["none"] = "ninguno",
        ["{0}: back to its previous DNS servers ({1})"] = "{0}: vuelve a sus servidores DNS anteriores ({1})",
        ["{0}: DNS servers from the router (DHCP)"] = "{0}: servidores DNS del router (DHCP)",
        ["{0}: kept as it is ({1}). It has a static IP address, so without these servers it would have no DNS."] = "{0}: se queda como está ({1}). Tiene una IP fija, así que sin estos servidores se quedaría sin DNS.",
        ["Not changed, because they have no IP settings of their own (for example, used by a Hyper-V virtual switch): {0}"] = "No se cambian porque no tienen configuración IP propia (por ejemplo, los usa un conmutador virtual de Hyper-V): {0}",
        ["powercfg /setactive {0} failed ({1})."] = "powercfg /setactive {0} falló ({1}).",
        ["powercfg /setactive failed ({0})."] = "powercfg /setactive falló ({0}).",
        ["The power mode could not be restored ({0})."] = "No se pudo restaurar el modo de energía ({0}).",
        ["Could not remove {0} ({1})."] = "No se pudo eliminar {0} ({1}).",
        ["Nothing to change:"] ="No hay nada que cambiar:",
        ["Restore the DNS settings of these adapters?"] = "¿Restaurar la configuración DNS de estos adaptadores?",
        ["Change the DNS servers to {0} on these adapters?"] = "¿Cambiar los servidores DNS a {0} en estos adaptadores?",
        ["Their current settings are saved first: choose Default (DHCP) later to put them back."] = "Antes se guarda su configuración actual: elige Predeterminado (DHCP) más tarde para volver a ella.",
        ["Previous DNS settings saved."] = "Configuración DNS anterior guardada.",
        ["DNS set on {0}"] = "DNS cambiado en {0}",
        ["Previous DNS restored on {0}"] = "DNS anterior restaurado en {0}",
        ["DNS from DHCP on {0}"] = "DNS por DHCP en {0}",
        ["{0}: static IP address, its DNS servers were kept (resetting would leave it without DNS)."] = "{0}: IP fija, se mantuvieron sus servidores DNS (restablecerlos lo dejaría sin DNS).",
        ["The DNS could not be changed on every adapter (see the errors above)."] = "No se pudo cambiar el DNS en todos los adaptadores (mira los errores de arriba).",
        ["Could not clear the DNS cache: {0}"] = "No se pudo vaciar la caché DNS: {0}",
        ["ERROR {0}: {1}"] = "ERROR {0}: {1}",

        // ── Windows Update ──
        ["Windows Home ignores the update delay policies: on this PC only the driver and automatic restart settings take effect."] = "Windows Home ignora las directivas para retrasar actualizaciones: en este PC solo se aplican los ajustes de drivers y de reinicio automático.",
        ["Apply the Windows Update security settings?"] = "¿Aplicar la configuración de seguridad de Windows Update?",
        ["Feature updates are delayed by a year and security updates by 4 days, drivers aren't installed through Windows Update and the PC doesn't restart by itself while someone is signed in."] = "Las actualizaciones de funciones se retrasan un año y las de seguridad 4 días, los drivers no se instalan desde Windows Update y el PC no se reinicia solo mientras alguien tenga la sesión iniciada.",
        ["Go back to the default Windows Update settings?"] = "¿Volver a la configuración predeterminada de Windows Update?",
        ["Only the settings that 'Security settings' changed are put back as they were before."] = "Solo se devuelven a como estaban los ajustes que cambió 'Configuración de seguridad'.",

        // ── Windows features ──
        ["Turn off these Windows features?"] = "¿Desactivar estas características de Windows?",
        ["Turn on:"] = "Activar:",
        ["A restart is needed to finish."] = "Hace falta reiniciar para terminar.",
        ["Older programs and games that need .NET Framework 3.5 won't start."] = "Los programas y juegos antiguos que necesitan .NET Framework 3.5 no arrancarán.",
        ["Your WSL Linux distributions stop working (their files stay, but can't be opened until it's turned back on). Docker Desktop, Android apps and other apps that use the Virtual Machine Platform stop working too."] = "Tus distribuciones Linux de WSL dejan de funcionar (sus archivos se quedan, pero no se pueden abrir hasta volver a activarlo). Docker Desktop, las apps de Android y otras apps que usan la Plataforma de máquina virtual también dejan de funcionar.",
        ["Existing Hyper-V virtual machines can't start, and Docker Desktop in Hyper-V mode stops working."] = "Las máquinas virtuales de Hyper-V que tengas no podrán arrancar, y Docker Desktop en modo Hyper-V deja de funcionar.",
        ["Windows Sandbox is no longer available."] = "Windows Sandbox deja de estar disponible.",
        ["Old games that need DirectPlay stop working."] = "Los juegos antiguos que necesitan DirectPlay dejan de funcionar.",

        // ── Power plans ──
        ["Restore previous plan"] = "Restaurar plan anterior",
        ["Restoring the previous power plan"] = "Restaurando el plan de energía anterior",
        ["There is no saved power plan: WinSolve hasn't switched plans on this PC (or the previous one is already back)."] = "No hay ningún plan de energía guardado: WinSolve no ha cambiado de plan en este PC (o el anterior ya está de vuelta).",
        ["No saved power plan: WinSolve hasn't switched plans on this PC."] = "No hay plan de energía guardado: WinSolve no ha cambiado de plan en este PC.",
        ["Current power plan saved ({0})."] = "Plan de energía actual guardado ({0}).",
        ["Could not save the current power plan: {0}"] = "No se pudo guardar el plan de energía actual: {0}",
        ["Previous power plan restored ({0})."] = "Plan de energía anterior restaurado ({0}).",
        ["Balanced plan active."] = "Plan Equilibrado activo.",
        ["Removed {0}"] = "Eliminado {0}",
    };
}
