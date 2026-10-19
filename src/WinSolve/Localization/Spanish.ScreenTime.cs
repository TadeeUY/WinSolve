namespace WinSolve.Localization;

public static partial class Spanish
{
    /// <summary>Spanish strings for Screen time and the lock hours.</summary>
    private static readonly Dictionary<string, string> ScreenTimeStrings = new(StringComparer.Ordinal)
    {
        ["Screen time"] = "Tiempo en pantalla",
        ["How long you use the PC each day, and lock hours to take a break from it."] = "Cuánto usas la PC cada día, y un horario de bloqueo para tomarte un descanso.",
        ["Today"] = "Hoy",
        ["Last 7 days"] = "Últimos 7 días",
        ["During lock hours (7 days)"] = "En horario de bloqueo (7 días)",
        ["Each day"] = "Cada día",
        ["Time with the screen on and the PC unlocked. The part used during lock hours is shown in another color."] = "Tiempo con la pantalla encendida y la PC desbloqueada. La parte usada en horario de bloqueo se muestra en otro color.",
        ["Lock hours"] = "Horario de bloqueo",
        ["The PC locks itself during these hours. You can unlock it, but it locks again every few minutes."] = "La PC se bloquea sola en este horario. Puedes desbloquearla, pero se vuelve a bloquear cada pocos minutos.",
        ["Lock the PC during these hours"] = "Bloquear la PC en este horario",
        ["You get a warning one minute before. Unlocking during lock hours counts above."] = "Te avisa un minuto antes. Desbloquearla en horario de bloqueo suma arriba.",
        ["From"] = "Desde",
        ["To"] = "Hasta",
        ["If it is earlier than the start, the lock hours end the next morning."] = "Si es antes que el inicio, el horario termina a la mañana siguiente.",
        ["Days"] = "Días",
        ["Lock again after unlocking"] = "Volver a bloquear después de desbloquear",
        ["{0} minutes"] = "{0} minutos",
        ["Lock now"] = "Bloquear ahora",
        ["Saved."] = "Guardado.",
        ["Screen time isn't available in this session."] = "El tiempo en pantalla no está disponible en esta sesión.",
        ["The start and the end can't be the same time."] = "El inicio y el fin no pueden ser la misma hora.",
        ["Pick at least one day."] = "Elige al menos un día.",
        ["The portable version only counts and locks while it is open."] = "La versión portable solo cuenta y bloquea mientras está abierta.",
        ["Screen time is only counted, and the PC only locks, while WinSolve is running. Turn on 'Start WinSolve with Windows' in Settings."] = "El tiempo en pantalla solo se cuenta, y la PC solo se bloquea, mientras WinSolve está abierto. Activa 'Iniciar WinSolve con Windows' en Configuración.",
        ["Locks again at {0}"] = "Se vuelve a bloquear a las {0}",
        ["Lock hours: {0} to {1}"] = "Horario de bloqueo: de {0} a {1}",
        ["No lock hours set"] = "Sin horario de bloqueo",
        ["{0} a day on average"] = "{0} por día en promedio",
        ["{0}% more than the 7 days before"] = "{0}% más que los 7 días anteriores",
        ["{0}% less than the 7 days before"] = "{0}% menos que los 7 días anteriores",
        ["Unlocked {0} time"] = "Desbloqueada {0} vez",
        ["Unlocked {0} times"] = "Desbloqueada {0} veces",
        ["The PC locks in 1 minute"] = "La PC se bloquea en 1 minuto",
        ["Lock hours until {0}. You can unlock it, but it locks again every {1} minutes."] = "Horario de bloqueo hasta las {0}. Puedes desbloquearla, pero se vuelve a bloquear cada {1} minutos.",
        ["Unlocked during lock hours"] = "Desbloqueada en horario de bloqueo",
        ["It counts in Screen time. The PC locks again at {0}."] = "Suma en Tiempo en pantalla. La PC se vuelve a bloquear a las {0}.",
        ["Lock the PC now"] = "Bloquear la PC ahora",
        ["Locks Windows; your apps stay open."] = "Bloquea Windows; tus apps quedan abiertas.",
    };
}
