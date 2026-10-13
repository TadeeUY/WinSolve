# Firma automática de WinSolve con SignPath (gratis)

Cuando esté configurado, **cada versión que se publica sale firmada sola**: GitHub compila,
manda los `.exe` a SignPath, los recibe firmados y publica. No hay que hacer nada en cada versión.

Mientras no estén cargados los datos de SignPath, las versiones se siguen publicando sin firmar,
como hasta ahora.

## Lo que hay que hacer una sola vez

### 1. Repositorio público con licencia
SignPath firma gratis solo proyectos de código abierto:
- GitHub → el repositorio → **Settings → General → Danger Zone → Change visibility → Public**.
- Tiene que haber un archivo `LICENSE` (por ejemplo MIT) en la raíz.

### 2. Pedir el alta en SignPath Foundation
- Entra en <https://signpath.org> → **Apply** (programa para proyectos open source).
- Datos que piden: nombre del proyecto (WinSolve), URL del repositorio, descripción, licencia y
  quién publica las versiones.
- Descripción sugerida:
  > WinSolve is a free, open-source Windows maintenance tool (cleanup, repair, drivers, tweaks)
  > built with .NET 8. Releases are built and published by GitHub Actions from this repository.
- La revisión puede tardar de unos días a algunas semanas.

### 3. Configurar el proyecto en SignPath (cuando te aprueben)
En el panel de SignPath:
- **Project** con el slug `WinSolve`, vinculado al repositorio de GitHub.
- **Signing policy** con el slug `release-signing`.
- **Artifact configuration** (la que viene por defecto) con este contenido, que firma todos los
  `.exe` que llegan:

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="*.exe">
      <authenticode-sign/>
    </pe-file>
  </zip-file>
</artifact-configuration>
```

- En **Trusted build systems**, agrega GitHub.com (así solo acepta firmar lo que compila GitHub).

### 4. Cargar los 2 datos en GitHub
GitHub → el repositorio → **Settings → Secrets and variables → Actions → New repository secret**:

| Nombre | De dónde sale |
|---|---|
| `SIGNPATH_API_TOKEN` | SignPath → tu usuario → *API token* (crea uno con permiso de *submitter*) |
| `SIGNPATH_ORGANIZATION_ID` | SignPath → *Organization* → el ID (formato `xxxxxxxx-xxxx-...`) |

Listo: la próxima versión que se publique sale firmada. En el registro de GitHub Actions
(paso "Use the signed installer") se ve el estado de la firma; si no es válida, la versión no se
publica.

## Qué cambia para quien descarga
- Windows muestra un editor verificado (el certificado lo emite SignPath Foundation) en lugar de
  "Editor desconocido".
- El aviso azul de SmartScreen desaparece mucho antes, porque la reputación se acumula para el
  certificado y no se reinicia en cada versión.
