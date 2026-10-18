namespace WinSolve.Localization;

public static partial class Spanish
{
    /// <summary>Spanish strings for "What does this change?" and the portable data folder.</summary>
    private static readonly Dictionary<string, string> TransparencyStrings = new(StringComparer.Ordinal)
    {
        ["What does this change?"] = "¿Qué modifica esto?",
        ["What it changes"] = "Qué modifica",
        ["...and {0} more"] = "...y {0} más",
        ["Click the (i) for the full list."] = "Toca la (i) para ver la lista completa.",
        ["Registry value"] = "Valor del registro",
        ["Registry key"] = "Clave del registro",
        ["Service"] = "Servicio",
        ["What"] = "Qué",
        ["Becomes"] = "Queda en",
        ["Undo"] = "Deshacer",
        ["Copy"] = "Copiar",
        ["Copied."] = "Copiado.",
        ["Applies to your account only."] = "Se aplica solo a tu cuenta.",
        ["Puts back the previous value (or removes it)"] = "Vuelve a poner el valor anterior (o lo quita)",
        ["Puts back the previous value (or {0})"] = "Vuelve a poner el valor anterior (o {0})",
        ["Removes the value (Windows default)"] = "Quita el valor (lo predeterminado de Windows)",
        ["Runs once"] = "Se ejecuta una vez",
        ["Nothing to undo (files are deleted)"] = "No hay nada que deshacer (los archivos se borran)",
        ["Startup: Disabled"] = "Inicio: deshabilitado",
        ["Startup: Manual"] = "Inicio: manual",
        ["Puts back the saved startup type"] = "Vuelve a poner el tipo de inicio guardado",
        ["Created, (Default) = empty"] = "Se crea, (Predeterminado) = vacío",
        ["Deletes the key"] = "Borra la clave",
        ["Off (current session)"] = "Desactivado (sesión actual)",
        ["Back on if the restored settings have animations"] = "Vuelve a activarse si la configuración restaurada tiene animaciones",
        ["Deletes hiberfil.sys and sets HibernateEnabled = 0"] = "Borra hiberfil.sys y pone HibernateEnabled = 0",
        ["Uninstalls the OneDrive app for this account"] = "Desinstala OneDrive para esta cuenta",
        ["Reinstalls OneDrive"] = "Reinstala OneDrive",
        ["Bits {0} set (DWORD; other bits kept)"] = "Bits {0} activados (DWORD; el resto se mantiene)",
        ["Clears bits {0} (the value is removed when it reaches 0)"] = "Desactiva los bits {0} (el valor se quita si queda en 0)",
        [@"Deletes the contents of %TEMP% and %WINDIR%\Temp (files over a day old, not in use)"] = @"Borra el contenido de %TEMP% y %WINDIR%\Temp (archivos de más de un día que no estén en uso)",
    };
}
