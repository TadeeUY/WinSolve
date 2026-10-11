namespace WinSolve.Services;

/// <summary>One change shown in "What's new", in English and Spanish.</summary>
public sealed record ChangeItem(string Glyph, string English, string Spanish);

public sealed record ChangelogEntry(Version Version, IReadOnlyList<ChangeItem> Items);

/// <summary>What each version brings, shown once after WinSolve updates itself.</summary>
public static class Changelog
{
    public static IReadOnlyList<ChangelogEntry> Entries { get; } =
    [
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
