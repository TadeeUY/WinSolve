namespace WinSolve.Services;

/// <summary>One change shown in "What's new", in English and Spanish.</summary>
public sealed record ChangeItem(string Glyph, string English, string Spanish);

public sealed record ChangelogEntry(Version Version, IReadOnlyList<ChangeItem> Items);

/// <summary>What each version brings, shown once after WinSolve updates itself.</summary>
public static class Changelog
{
    public static IReadOnlyList<ChangelogEntry> Entries { get; } =
    [
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
