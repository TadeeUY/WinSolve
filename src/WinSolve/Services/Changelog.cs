namespace WinSolve.Services;

/// <summary>One change shown in "What's new", in English and Spanish.</summary>
public sealed record ChangeItem(string Glyph, string English, string Spanish);

public sealed record ChangelogEntry(Version Version, IReadOnlyList<ChangeItem> Items);

/// <summary>What each version brings, shown once after WinSolve updates itself.</summary>
public static class Changelog
{
    public static IReadOnlyList<ChangelogEntry> Entries { get; } =
    [
        new(new Version(0, 17, 5),
        [
            new("\uE701", "Wi-Fi QR code for PCs on the cable: the new \"Read it from my router\" button opens your router signed in, and when you show its Wi-Fi settings page WinSolve reads the network name and the password from it, so you don't have to type them. It works with any router brand whose page shows them in boxes.",
                "Código QR del Wi-Fi para PCs conectadas por cable: el nuevo botón \"Leerlo desde mi router\" abre tu router con la sesión iniciada y, cuando le muestras su página de configuración Wi-Fi, WinSolve lee de ahí el nombre de la red y la contraseña, para que no tengas que escribirlos. Sirve con cualquier marca de router cuya página los muestre en casillas."),
        ]),
        new(new Version(0, 17, 4),
        [
            new("\uE945", "Smoother and snappier animations: they now follow the screen refresh instead of a slow timer, they are shorter, and the menu and the page next to it no longer flicker.",
                "Animaciones más fluidas y ágiles: ahora siguen el refresco de la pantalla en vez de un temporizador lento, son más cortas, y el menú y la página de al lado ya no parpadean."),
        ]),
        new(new Version(0, 17, 3),
        [
            new("\uE701", "The Wi-Fi QR code now finds your network by itself, even when the PC is on the cable: it recognizes your router's Wi-Fi among the networks around, and fills in the name and the password Windows saved. No need to type anything.",
                "El código QR del Wi-Fi ahora encuentra tu red solo, incluso con la PC conectada por cable: reconoce el Wi-Fi de tu router entre las redes de alrededor y completa el nombre y la contraseña que Windows guardó. No hace falta escribir nada."),
            new("\uE774", "New (optional): an alert when a device never seen before joins your network, and a history of your connection that shows when it dropped, with a report you can read to your Internet provider. Both are off until you turn them on in Internet.",
                "Nuevo (opcional): un aviso cuando un dispositivo que nunca se vio entra a tu red, y un historial de tu conexión que muestra cuándo se cayó, con un informe que puedes leerle a tu proveedor de Internet. Los dos están apagados hasta que los actives en Internet."),
            new("\uE76B", "Smoother animation when the side menu collapses or opens.",
                "Animación más fluida al contraer o abrir el menú lateral."),
        ]),
        new(new Version(0, 17, 2),
        [
            new("\uE701", "New: Wi-Fi QR code. Pick a saved network or type one, and WinSolve draws the code your visitors scan with their phone to join the Wi-Fi without typing the password. Copy it or save it as an image. Find it in Internet and in the Toolbox.",
                "Nuevo: código QR del Wi-Fi. Elige una red guardada o escribe una, y WinSolve dibuja el código que tus visitas escanean con el celular para conectarse al Wi-Fi sin escribir la clave. Cópialo o guárdalo como imagen. Está en Internet y en la Caja de herramientas."),
            new("\uE774", "Devices on your network now show their names: each device is asked the way it answers (iPhone, Mac and printers; Windows PCs; smart TVs and consoles). Double-click any name to give the device your own, and WinSolve remembers it.",
                "Los dispositivos de tu red ahora muestran su nombre: se le pregunta a cada uno como sabe responder (iPhone, Mac e impresoras; PCs con Windows; televisores y consolas). Haz doble clic en un nombre para ponerle el tuyo, y WinSolve lo recuerda."),
        ]),
        new(new Version(0, 17, 1),
        [
            new("\uE76B", "Collapsible menu: the arrow at the top of the side menu shrinks it to just the icons (hover an icon to see its name) and opens it again. WinSolve remembers your choice.",
                "Menú plegable: la flecha de arriba del menú lateral lo reduce a solo los iconos (pasa el mouse por un icono para ver su nombre) y lo vuelve a abrir. WinSolve recuerda tu elección."),
            new("\uE701", "Wi-Fi channels now work while you are on the cable: the Wi-Fi card only has to be on, not connected. If Wi-Fi is off or the PC has no Wi-Fi card, it says so and what to do.",
                "Los canales Wi-Fi ahora funcionan estando conectado por cable: la tarjeta Wi-Fi solo tiene que estar encendida, no conectada. Si el Wi-Fi está apagado o la PC no tiene tarjeta Wi-Fi, lo dice y explica qué hacer."),
            new("\uE80F", "Fixed: the last entries of the menu (Activation) were cut off on windows that are not very tall. The menu now scrolls with the mouse wheel.",
                "Corregido: las últimas opciones del menú (Activación) quedaban cortadas en ventanas poco altas. Ahora el menú se desplaza con la rueda del mouse."),
        ]),
        new(new Version(0, 17, 0),
        [
            new("\uE774", "New: Internet. Checks the router, the Internet and the DNS and says where the problem is, recognizes your router's brand and model by itself, lists every device connected to your network, and opens your router's own settings page already signed in (the password is saved encrypted for your Windows user), so you can change the Wi-Fi password or remove and block devices.",
                "Nuevo: Internet. Revisa el router, Internet y el DNS y dice dónde está el problema, reconoce solo la marca y el modelo de tu router, lista todos los dispositivos conectados a tu red y abre la página de configuración de tu router ya con la sesión iniciada (la contraseña se guarda cifrada para tu usuario de Windows), para que cambies la clave del Wi-Fi o quites y bloquees dispositivos."),
            new("\uE701", "Wi-Fi channels: shows how crowded each channel is with your neighbors' networks and recommends the least crowded one for your router, on 2.4 and 5 GHz.",
                "Canales Wi-Fi: muestra qué tan saturado está cada canal por las redes de tus vecinos y recomienda el menos saturado para tu router, en 2,4 y 5 GHz."),
            new("\uE945", "DNS speed test: compares your DNS with Cloudflare, Google, Quad9, AdGuard and OpenDNS and tells you if a faster one is worth setting.",
                "Prueba de velocidad del DNS: compara tu DNS con Cloudflare, Google, Quad9, AdGuard y OpenDNS y te dice si vale la pena poner uno más rápido."),
            new("\uE713", "Start with Windows: when it can't be turned on, Settings now says where WinSolve is running from and shows the reason Windows gave.",
                "Iniciar con Windows: cuando no se puede activar, Configuración ahora dice desde dónde se ejecuta WinSolve y muestra el motivo que dio Windows."),
        ]),
        new(new Version(0, 16, 2),
        [
            new("\uE823", "New: Screen time. See how long you use the PC today, in the last 7 days and on each day, and set lock hours (for example 23:00 to 07:00): the PC locks with a one-minute warning, and if you unlock it, it locks again every 10 minutes. Time used during lock hours is shown apart.",
                "Nuevo: Tiempo en pantalla. Mira cuánto usas la PC hoy, en los últimos 7 días y cada día, y pon un horario de bloqueo (por ejemplo de 23:00 a 07:00): la PC se bloquea con un aviso un minuto antes, y si la desbloqueas se vuelve a bloquear cada 10 minutos. El tiempo usado en horario de bloqueo se muestra aparte."),
            new("\uE895", "New versions show up within 2 minutes of being published, with a notification and an \"Update now\" button. If a task is running, the update waits and installs by itself as soon as it finishes.",
                "Las versiones nuevas aparecen a los 2 minutos de publicarse, con una notificación y un botón \"Actualizar ahora\". Si hay una tarea en curso, la actualización espera y se instala sola apenas termina."),
        ]),
        new(new Version(0, 16, 1),
        [
            new("\uE88E", "Portable mode: the portable WinSolve keeps its settings, logs and Undo backups next to the .exe (also on FAT32/exFAT USB sticks) and leaves no trace on the PC when you close it.",
                "Modo portable: el WinSolve portable guarda su configuración, registros y copias para deshacer junto al .exe (también en memorias USB FAT32/exFAT) y no deja rastros en la PC al cerrarlo."),
            new("\uE8E5", "Profiles: Settings > Profiles saves your tweaks, DNS and optimization choices to a .winsolve file and applies them on another PC. They also apply unattended: WinSolve.exe --apply-profile file.winsolve [--silent] [--dry-run].",
                "Perfiles: Configuración > Perfiles guarda tus ajustes, DNS y opciones de optimización en un archivo .winsolve y los aplica en otra PC. También se aplican sin intervención: WinSolve.exe --apply-profile archivo.winsolve [--silent] [--dry-run]."),
            new("\uE946", "\"What does this change?\": click the (i) next to a tweak to see every registry value, service and command it changes, what it becomes and how Undo puts it back.",
                "\"¿Qué modifica esto?\": haz clic en la (i) junto a un ajuste para ver cada valor del registro, servicio y comando que cambia, cómo queda y cómo lo vuelve atrás Deshacer."),
            new("\uE7BA", "Hardware > Events reads blue screen dumps: date, stop code, the driver that probably caused it and what to do.",
                "Hardware > Eventos lee los volcados de los pantallazos azules: fecha, código de error, el driver que probablemente lo causó y qué hacer."),
            new("\uE9D9", "More accurate CPU temperature: it uses LibreHardwareMonitor or OpenHardwareMonitor when one is running, and no longer shows the fixed value some PCs report.",
                "Temperatura de CPU más precisa: usa LibreHardwareMonitor u OpenHardwareMonitor si hay uno abierto, y ya no muestra el valor fijo que reportan algunas PCs."),
            new("\uE945", "Faster disk space analysis: folders are read in parallel, and the Windows folder is analyzed faster.",
                "Análisis de espacio en disco más rápido: las carpetas se leen en paralelo y la carpeta de Windows se analiza más rápido."),
        ]),
        new(new Version(0, 15, 5),
        [
            new("\uE895", "Automatic updates never interrupt you: they wait until nothing is running or open in WinSolve and no game, video or presentation is full screen. From the notification area they install silently, without opening any window; with WinSolve open, they wait until you've been away for 10 minutes.",
                "Las actualizaciones automáticas nunca te interrumpen: esperan a que no haya nada en marcha ni abierto en WinSolve y a que no haya un juego, video o presentación a pantalla completa. Desde el área de notificaciones se instalan en silencio, sin abrir ventanas; con WinSolve abierto, esperan a que no uses la PC por 10 minutos."),
        ]),
        new(new Version(0, 15, 4),
        [
            new("\uEDA2", "Hardware > Disks: 'Last check result' shows what the scan at startup found (it often runs overnight). WinSolve warns if a drive was checked in the last week, won't schedule the same scan twice and lets you cancel a scheduled check.",
                "Hardware > Discos: 'Último resultado' muestra lo que encontró la revisión al iniciar (que suele correr de noche). WinSolve avisa si una unidad se revisó en la última semana, no programa dos veces la misma revisión y permite cancelar una revisión programada."),
        ]),
        new(new Version(0, 15, 3),
        [
            new("\uEDA2", "Disk repair: when Windows won't hand over a drive (\"Access denied\" because a program or driver is using it), the check or bad sector scan is scheduled for the next restart, where it runs before anything else uses the drive.",
                "Reparación de discos: cuando Windows no libera una unidad (\"Acceso denegado\" porque un programa o driver la está usando), la revisión o la búsqueda de sectores dañados se programa para el próximo reinicio, donde corre antes de que nada más use la unidad."),
        ]),
        new(new Version(0, 15, 2),
        [
            new("\uE895", "New versions show up sooner: WinSolve checks every time it starts and every hour, instead of every 4 hours.",
                "Las versiones nuevas aparecen antes: WinSolve busca cada vez que se abre y cada hora, en vez de cada 4 horas."),
        ]),
        new(new Version(0, 15, 1),
        [
            new("\uEA18", "New in Toolbox: Safe Mode. Restarts Windows in Safe Mode (with or without internet) for one start only, so the PC can never get stuck in it. From Safe Mode, one button restarts normally.",
                "Nuevo en Herramientas: Modo seguro. Reinicia Windows en modo seguro (con o sin internet) solo para un arranque, así la PC nunca queda atrapada en él. Desde el modo seguro, un botón reinicia normalmente."),
            new("\uEDA2", "Disk repair explains \"Access denied\": it checks first whether the drive is locked by BitLocker, write-protected or held by another program, and no longer says \"Finished\" when chkdsk could not run.",
                "La reparación de discos explica el \"Acceso denegado\": primero revisa si la unidad está bloqueada con BitLocker, protegida contra escritura o tomada por otro programa, y ya no dice \"Terminado\" cuando chkdsk no pudo ejecutarse."),
        ]),
        new(new Version(0, 15, 0),
        [
            new("\uE72E", "Safer when someone uses another account's administrator password: file pickers, unsigned installers and risky uninstallers are blocked, and WinSolve's own folders are better protected.",
                "Más seguro cuando alguien usa la contraseña de administrador de otra cuenta: se bloquean las ventanas para elegir archivos, los instaladores sin firma y los desinstaladores riesgosos, y las carpetas de WinSolve quedan mejor protegidas."),
            new("\uE895", "Updates wait until running tasks finish, and closing WinSolve asks first while something is in progress.",
                "Las actualizaciones esperan a que terminen las tareas en curso, y cerrar WinSolve pregunta antes si hay algo en marcha."),
            new("\uE945", "Smoother: moving many files to the Recycle Bin and the Hardware > Devices tab no longer freeze the window.",
                "Más fluido: mandar muchos archivos a la Papelera y la pestaña Hardware > Dispositivos ya no congelan la ventana."),
            new("\uE8C1", "Spanish everywhere: about 90 more messages translated, including the step-by-step logs.",
                "Español en todos lados: unos 90 mensajes más traducidos, incluidos los registros paso a paso."),
        ]),
        new(new Version(0, 14, 1),
        [
            new("\uE9D9", "The PC report has a simpler, tidier design: the score, a card for each part and the recommendations on the first page, the specifications on their own pages. It comes out in the app's language.",
                "El informe del PC tiene un diseño más simple y ordenado: la nota, una tarjeta por componente y las recomendaciones en la primera página, y la ficha técnica en páginas aparte. Sale en el idioma de la app."),
        ]),
        new(new Version(0, 14, 0),
        [
            new("\uE9D9", "The PC report now says how good your PC is: a score out of 100, a bar for the processor, memory, storage and graphics, what it is good for and which upgrade would help most.",
                "El informe del PC ahora dice qué tan buena es tu PC: una nota sobre 100, una barra para procesador, memoria, almacenamiento y gráficos, para qué sirve y qué mejora conviene más."),
        ]),
        new(new Version(0, 13, 4),
        [
            new("\uE74D", "Safer cleanup: temporary files are only removed from real temp folders and only when they are over a day old; your personal and Windows folders can never be emptied or deleted.",
                "Limpieza más segura: los temporales solo se borran de carpetas temporales reales y si tienen más de un día; tus carpetas personales y las de Windows nunca se pueden vaciar ni borrar."),
            new("\uE7F8", "Drivers: a clean install saves your current driver first and puts it back if anything goes wrong; repairing devices asks first and never removes network, disk, keyboard or mouse devices; old drivers are kept until after a restart.",
                "Drivers: la instalación limpia guarda tu driver actual y lo vuelve a poner si algo sale mal; reparar dispositivos pregunta antes y nunca quita los de red, disco, teclado o mouse; los drivers viejos se guardan hasta después de reiniciar."),
            new("\uE895", "Apps: parts Windows needs (Store, codecs, runtimes) can't be removed by mistake, and after a forced uninstall you choose which leftover folders go (to the Recycle Bin).",
                "Apps: lo que Windows necesita (Store, códecs, componentes) no se puede quitar por error, y después de una desinstalación forzada eliges qué carpetas sobrantes se borran (a la Papelera)."),
            new("\uE713", "Undo now puts back the exact settings you had, and DNS, Windows Update, network reset and Windows features ask before changing anything.",
                "Deshacer ahora vuelve exactamente a lo que tenías, y DNS, Windows Update, el reinicio de red y las características de Windows preguntan antes de cambiar algo."),
            new("\uE7E8", "Startup apps: removed entries can be restored, and Windows Security can't be turned off from here.",
                "Apps de inicio: las entradas quitadas se pueden restaurar y la Seguridad de Windows no se puede desactivar desde aquí."),
        ]),
        new(new Version(0, 13, 3),
        [
            new("\uE74D", "Uninstalling WinSolve now also removes its scheduled maintenance task.",
                "Al desinstalar WinSolve ahora también se borra su tarea de mantenimiento programado."),
            new("\uE9F9", "Cleaner log: no more PowerShell XML noise in the maintenance entries.",
                "Registro más limpio: ya no aparece texto XML de PowerShell en el mantenimiento."),
            new("\uE896", "The installer can install silently (--install --quiet), handy for installing on many PCs.",
                "El instalador puede instalar en silencio (--install --quiet), útil para instalar en varias PCs."),
        ]),
        new(new Version(0, 13, 2),
        [
            new("\uE713", "Tweaks have three levels: Minimal, Standard (selected by default) and Maximum. You can still tick tweaks one by one.",
                "Los tweaks tienen tres niveles: Mínimo, Estándar (elegido por defecto) y Máximo. También puedes marcarlos uno por uno."),
            new("\uE74E", "Nothing changes until you select Save: WinSolve shows the list of changes and asks if you're sure before applying them.",
                "No se cambia nada hasta que pulses Guardar: WinSolve muestra la lista de cambios y te pregunta si estás seguro antes de aplicarlos."),
        ]),
        new(new Version(0, 13, 1),
        [
            new("\uE945", "Much faster: switching pages is now instant (it could take 10-30 seconds before), and pages load in the background while WinSolve is idle.",
                "Mucho más rápido: cambiar de página ahora es instantáneo (antes podía tardar de 10 a 30 segundos) y las páginas se cargan en segundo plano."),
            new("\uE895", "Updates show a simpler window: just the progress.",
                "Las actualizaciones muestran una ventana más simple: solo el progreso."),
        ]),
        new(new Version(0, 13, 0),
        [
            new("", "Weekly summary on Home: problems detected, space freed and drive health for the past week (Settings > Background).",
                "Resumen semanal en Inicio: problemas detectados, espacio liberado y salud de los discos de la última semana (Configuración > Segundo plano)."),
            new("", "Old drivers: after a driver update WinSolve offers to remove the old copies Windows keeps. The drivers in use are never touched.",
                "Drivers viejos: después de actualizar drivers, WinSolve ofrece borrar las copias viejas que guarda Windows. Los drivers en uso nunca se tocan."),
            new("", "Large & forgotten files: big files you haven't opened in a long time and empty folders, in Toolbox.",
                "Archivos grandes y olvidados: archivos grandes que no abres hace mucho y carpetas vacías, en Herramientas."),
            new("", "Accessibility: everything works with the keyboard (Tab, Enter, Space), Narrator reads the controls, and high contrast mode is supported.",
                "Accesibilidad: todo se puede usar con el teclado (Tab, Enter, Espacio), el Narrador lee los controles y se respeta el modo de alto contraste."),
            new("", "This window: after each update, a short list of what's new in your language.",
                "Esta ventana: después de cada actualización, una lista corta de las novedades en tu idioma."),
        ]),
        new(new Version(0, 12, 0),
        [
            new("", "Security: WinSolve only works on the signed-in user's files, apps and settings, never on other users of the PC.",
                "Seguridad: WinSolve solo trabaja sobre los archivos, apps y configuración del usuario que inició sesión, nunca sobre otros usuarios del PC."),
            new("", "New logo.", "Logo nuevo."),
        ]),
        new(new Version(0, 11, 0),
        [
            new("", "Light theme, a welcome tour, drive health alerts, battery report and the PC report as PDF.",
                "Tema claro, pantalla de bienvenida, avisos de salud del disco, informe de batería e informe del PC en PDF."),
        ]),
    ];

    /// <summary>Changes newer than the last version the user saw, newest first (at most three versions).</summary>
    public static List<ChangelogEntry> Since(Version? lastSeen, Version current) => Entries
        .Where(e => e.Version <= current && (lastSeen is null ? e.Version.Major == current.Major && e.Version.Minor == current.Minor : e.Version > lastSeen))
        .OrderByDescending(e => e.Version)
        .Take(3)
        .ToList();
}
