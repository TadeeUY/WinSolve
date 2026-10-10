using System.Collections.Generic;
using System.Globalization;

namespace WinSolve.Setup
{
    /// <summary>Installer texts in English or Spanish (chosen in the window, Windows' language by default).</summary>
    internal static class Lang
    {
        public static bool Spanish = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";

        /// <summary>"es" or "en", passed on to WinSolve so it opens in the same language.</summary>
        public static string Code => Spanish ? "es" : "en";

        public static string T(string en) => Spanish && Es.TryGetValue(en, out var es) ? es : en;

        public static string F(string en, params object[] args) => string.Format(T(en), args);

        private static readonly Dictionary<string, string> Es = new Dictionary<string, string>
        {
            ["WinSolve Setup"] = "Instalación de WinSolve",
            ["Install WinSolve"] = "Instalar WinSolve",
            ["Updating WinSolve"] = "Actualizando WinSolve",
            ["Version {0}  ·  The multitool for Windows"] = "Versión {0}  ·  La navaja suiza para Windows",
            ["Language"] = "Idioma",
            ["Install for"] = "Instalar para",
            ["Only me"] = "Solo para mí",
            ["Installs in your user folder  ·  {0}"] = "En tu carpeta de usuario  ·  {0}",
            ["All users of this PC (recommended)"] = "Todos los usuarios de este PC (recomendado)",
            ["Protected from tampering, requires administrator  ·  {0}"] = "Protegido contra cambios, requiere administrador  ·  {0}",
            ["Clean install (remove any previous version, settings and logs)"] = "Instalación limpia (borra versiones anteriores, configuración y registros)",
            ["Create a desktop shortcut"] = "Crear un acceso directo en el escritorio",
            ["Open WinSolve when setup finishes"] = "Abrir WinSolve al terminar",
            ["Ready to install."] = "Listo para instalar.",
            ["The .NET 8 Desktop Runtime will be downloaded from Microsoft (about 55 MB)."] = "Se descargará .NET 8 Desktop Runtime desde Microsoft (unos 55 MB).",
            ["Install"] = "Instalar",
            ["Cancel"] = "Cancelar",
            ["Finish"] = "Finalizar",
            ["Administrator permission is required to install for all users."] = "Para instalar para todos los usuarios se necesita permiso de administrador.",
            ["Setup failed: {0}"] = "La instalación falló: {0}",
            ["Downloading the .NET 8 Desktop Runtime from Microsoft..."] = "Descargando .NET 8 Desktop Runtime desde Microsoft...",
            ["Installing the .NET 8 Desktop Runtime..."] = "Instalando .NET 8 Desktop Runtime...",
            ["Closing WinSolve if it is running..."] = "Cerrando WinSolve si está abierto...",
            ["Removing the previous installation and settings..."] = "Quitando la instalación anterior y la configuración...",
            ["Copying files to {0}..."] = "Copiando archivos a {0}...",
            ["WinSolve was updated successfully."] = "WinSolve se actualizó correctamente.",
            ["Creating shortcuts..."] = "Creando accesos directos...",
            ["Registering the uninstaller..."] = "Registrando el desinstalador...",
            ["WinSolve was installed successfully."] = "WinSolve se instaló correctamente.",
            ["Files will be removed when the uninstaller closes."] = "Los archivos se borrarán cuando se cierre el desinstalador.",
            ["WinSolve is ready"] = "WinSolve está listo",
            ["All users (recommended)"] = "Todos los usuarios (recomendado)",
            ["Protected from changes. Needs administrator.\n{0}"] = "Protegido contra cambios. Pide administrador.\n{0}",
            ["No administrator needed. Can't start with Windows.\n{0}"] = "Sin administrador. No inicia con Windows.\n{0}",
            ["Clean install: remove the previous version and its settings"] = "Instalación limpia: borra la versión anterior y su configuración",
            ["Uninstall WinSolve"] = "Desinstalar WinSolve",
            ["Remove WinSolve from this PC?\n\nSelect Yes to also delete WinSolve's settings and logs, or No to keep them."] =
                "¿Quitar WinSolve de este PC?\n\nElige Sí para borrar también la configuración y los registros de WinSolve, o No para conservarlos.",
            ["WinSolve was removed."] = "WinSolve se desinstaló.",
        };
    }
}
