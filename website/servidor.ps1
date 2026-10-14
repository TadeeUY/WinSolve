# Sirve esta carpeta en http://localhost:8000 (solo para esta PC). Ctrl+C para detenerlo.
param([int]$Port = 8000)
$root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$types = @{
  '.html' = 'text/html; charset=utf-8'; '.css' = 'text/css; charset=utf-8'; '.js' = 'text/javascript; charset=utf-8'
  '.svg' = 'image/svg+xml'; '.png' = 'image/png'; '.ico' = 'image/x-icon'; '.jpg' = 'image/jpeg'; '.json' = 'application/json'
}
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
try { $listener.Start() } catch { Write-Host "No se pudo usar el puerto $Port (¿ya hay algo abierto ahí?)." -ForegroundColor Red; Read-Host 'Enter para salir'; exit 1 }
Write-Host "WinSolve web en http://localhost:$Port  (Ctrl+C para detener)" -ForegroundColor Cyan
Start-Process "http://localhost:$Port/" -ErrorAction SilentlyContinue
try {
  while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $path = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath).TrimStart('/')
    $file = [IO.Path]::GetFullPath((Join-Path $root $path))
    if ((Test-Path $file -PathType Container)) { $file = Join-Path $file 'index.html' }
    $res = $ctx.Response
    # Solo archivos dentro de esta carpeta.
    if (-not $file.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path $file -PathType Leaf)) {
      $res.StatusCode = 404; $bytes = [Text.Encoding]::UTF8.GetBytes('404 - No encontrado')
    } else {
      $ext = [IO.Path]::GetExtension($file).ToLowerInvariant()
      $res.ContentType = if ($types[$ext]) { $types[$ext] } else { 'application/octet-stream' }
      $bytes = [IO.File]::ReadAllBytes($file)
    }
    $res.ContentLength64 = $bytes.Length
    $res.OutputStream.Write($bytes, 0, $bytes.Length)
    $res.Close()
    Write-Host ("{0} {1}" -f $res.StatusCode, $ctx.Request.Url.AbsolutePath)
  }
} finally { $listener.Stop() }
