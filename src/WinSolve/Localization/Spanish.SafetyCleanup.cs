namespace WinSolve.Localization;

public static partial class Spanish
{
    /// <summary>Spanish strings for the cleanup and deletion safety fixes.</summary>
    private static readonly Dictionary<string, string> SafetyCleanup = new(StringComparer.Ordinal)
    {
        // ── Temporary files ──
        ["Deletes the contents of %TEMP% older than a day. Files in use are skipped."] = "Borra el contenido de %TEMP% con más de un día. Los archivos en uso se omiten.",
        [@"Deletes the contents of C:\Windows\Temp older than a day."] = @"Borra el contenido de C:\Windows\Temp con más de un día.",
        ["%TEMP% is {0}, which is not a temporary folder in your profile; it is left alone."] = "%TEMP% es {0}, que no es una carpeta temporal de tu perfil; no se toca.",
        ["Cleaning the default temporary folder instead: {0}"] = "En su lugar se limpia la carpeta temporal predeterminada: {0}",
        ["Skipped: no temporary folder that is safe to empty was found."] = "Omitido: no se encontró ninguna carpeta temporal que se pueda vaciar de forma segura.",
        ["Skipped {0}: it is a system or personal folder, which is never emptied."] = "Se omitió {0}: es una carpeta del sistema o personal, y esas nunca se vacían.",

        // ── Windows Update cache ──
        ["Skipped the Windows Update cache: {0}."] = "Se omitió la caché de Windows Update: {0}.",
        ["Windows is waiting for a restart to finish installing updates"] = "Windows espera un reinicio para terminar de instalar actualizaciones",
        ["Windows Update is waiting for a restart"] = "Windows Update espera un reinicio",
        ["Windows is installing updates right now"] = "Windows está instalando actualizaciones en este momento",
        ["Windows Update is installing updates right now"] = "Windows Update está instalando actualizaciones en este momento",
        ["{0} of the selected files can't be moved to the Recycle Bin: {1}."] = "{0} de los archivos seleccionados no se pueden mover a la papelera: {1}.",
        ["Delete them permanently? They can't be restored afterwards. (No skips them.)"] = "¿Eliminarlos para siempre? Después no se podrán recuperar. (No los omite.)",
        ["Skipped {0}: it is in a protected folder (a drive's top level, the Users folder or another key Windows folder), not in your own folders."] =
            "Se omitió {0}: está en una carpeta protegida (la raíz de una unidad, la carpeta Users u otra carpeta clave de Windows), no en tus propias carpetas.",

        // ── Recycle Bin ──
        ["'{0}' can't be moved to the Recycle Bin: {1}."] = "'{0}' no se puede mover a la papelera: {1}.",
        ["Delete it permanently? It can't be restored afterwards."] = "¿Eliminarlo para siempre? Después no se podrá recuperar.",
        ["Delete permanently"] = "Eliminar para siempre",
        ["it is on a network location"] = "está en una ubicación de red",
        ["it is on a network drive"] = "está en una unidad de red",
        ["it is on a removable drive (such as a USB stick or memory card)"] = "está en una unidad extraíble (como un pendrive o una tarjeta de memoria)",
        ["this drive has no Recycle Bin"] = "esta unidad no tiene papelera",
        ["the Recycle Bin is turned off on this PC"] = "la papelera está desactivada en este PC",
        ["the Recycle Bin is turned off for this drive"] = "la papelera está desactivada para esta unidad",
        ["it is larger than the Recycle Bin of this drive"] = "es más grande que la papelera de esta unidad",

        // ── Space page ──
        ["WinSolve won't delete this: it is part of Windows or of an installed program (it is in Windows, Program Files, ProgramData or a Windows system folder), and deleting it can stop Windows or your apps from working."] =
            "WinSolve no va a borrar esto: es parte de Windows o de un programa instalado (está en Windows, Program Files, ProgramData o una carpeta del sistema de Windows), y borrarlo puede hacer que Windows o tus aplicaciones dejen de funcionar.",
        ["To remove a program, use the Apps page. To free space safely, use the Cleanup page."] = "Para quitar un programa, usa la página Aplicaciones. Para liberar espacio de forma segura, usa la página Limpieza.",
        ["WinSolve won't delete this: it is a Windows system file, and deleting it can stop Windows from starting or working."] =
            "WinSolve no va a borrar esto: es un archivo del sistema de Windows, y borrarlo puede impedir que Windows arranque o funcione.",
        ["WinSolve won't delete this folder: it is a Windows folder, a user's profile or personal folder (such as Documents, Desktop, Downloads or AppData), or it contains one. Deleting it would remove files that Windows, your apps or a person need."] =
            "WinSolve no va a borrar esta carpeta: es una carpeta de Windows, el perfil o una carpeta personal de un usuario (como Documentos, Escritorio, Descargas o AppData), o contiene una. Borrarla eliminaría archivos que necesitan Windows, tus aplicaciones o una persona.",
        ["Open it and delete only what you don't need inside."] = "Ábrela y borra solo lo que no necesites de adentro.",

        // ── Duplicates ──
        ["{0} set(s) were not selected automatically: their copies are not all in your personal folders (Desktop, Documents, Downloads, Pictures, Music, Videos, OneDrive), so a program may need each one. Check them yourself."] =
            "{0} grupo(s) no se seleccionaron automáticamente: sus copias no están todas en tus carpetas personales (Escritorio, Documentos, Descargas, Imágenes, Música, Videos, OneDrive), así que un programa podría necesitar cada una. Revísalos tú.",

        // ── Shredder ──
        ["Kept {0}: {1}."] = "Se conservó {0}: {1}.",
        ["its contents could not be read"] = "no se pudo leer su contenido",
        ["it is a link or a cloud (OneDrive) file; shredding it could change the original or delete the cloud copy"] = "es un vínculo o un archivo en la nube (OneDrive); triturarlo podría cambiar el original o borrar la copia en la nube",
        ["it is nested too deeply"] = "está anidado a demasiada profundidad",
        ["it has other names (hard links), so overwriting it would also change the file under its other names"] = "tiene otros nombres (vínculos físicos), así que sobrescribirlo también cambiaría el archivo con sus otros nombres",
        ["it is inside a link or a cloud (OneDrive) folder"] = "está dentro de un vínculo o de una carpeta en la nube (OneDrive)",
        ["it is reached through a link"] ="se llega a él a través de un vínculo",
        ["some items in it were not shredded"] = "algunos elementos de adentro no se trituraron",
        ["{0} item(s) were kept and not shredded (links, cloud files, files with several names or unreadable folders)."] = "{0} elemento(s) se conservaron sin triturar (vínculos, archivos en la nube, archivos con varios nombres o carpetas ilegibles).",

        // ── Large & forgotten files ──
        ["Last changed or opened"] = "Último cambio o apertura",
        ["Not changed or opened in 6 months"] = "Sin cambios ni aperturas en 6 meses",
        ["Not changed or opened in 1 year"] = "Sin cambios ni aperturas en 1 año",
        ["Not changed or opened in 2 years"] = "Sin cambios ni aperturas en 2 años",
        ["Files over 100 MB that haven't been changed or opened for a long time (Windows doesn't always record when a file is opened), and folders with nothing inside. Only your own folders are searched (not AppData)."] =
            "Archivos de más de 100 MB que no se cambian ni se abren hace mucho (Windows no siempre registra cuándo se abre un archivo) y carpetas sin nada adentro. Solo se buscan tus propias carpetas (no AppData).",
    };
}
