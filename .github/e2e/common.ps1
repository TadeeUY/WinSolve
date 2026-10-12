# Helpers shared by the end-to-end scripts.
$ErrorActionPreference = 'Stop'
$script:Failures = [System.Collections.Generic.List[string]]::new()
$OutDir = Join-Path $PSScriptRoot '..\..\e2e' | Resolve-Path

function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:Failures.Add($what) }
}

function Finish([string]$name) {
    if ($script:Failures.Count -gt 0) {
        Write-Host "`n$name - $($script:Failures.Count) check(s) failed:" -ForegroundColor Red
        $script:Failures | ForEach-Object { Write-Host "  - $_" }
        exit 1
    }
    Write-Host "`n$name - all checks passed." -ForegroundColor Green
}

function Screenshot([string]$name) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = [System.Drawing.Bitmap]::new($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size); $bmp.Save((Join-Path $OutDir "$name.png")) }
    catch { Write-Host "  (no screenshot: $($_.Exception.Message))" }
    finally { $g.Dispose(); $bmp.Dispose() }
}

function Run-Setup([string]$exe, [string]$arguments, [string]$log) {
    $p = Start-Process $exe -ArgumentList $arguments -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $OutDir "$log.txt") -RedirectStandardError (Join-Path $OutDir "$log-errors.txt")
    Get-Content (Join-Path $OutDir "$log.txt"), (Join-Path $OutDir "$log-errors.txt") | ForEach-Object { Write-Host "    | $_" }
    return $p.ExitCode
}

function Log-Text { Get-ChildItem "$env:ProgramData\WinSolve\logs" -File -ErrorAction SilentlyContinue | Sort-Object Name | Get-Content -Raw | Out-String }

# Opens WinSolve, waits for its window, takes a screenshot and closes it.
function Test-Launch([string]$exe, [string]$name) {
    $before = (Log-Text).Length
    $p = Start-Process $exe -PassThru
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0 -and $p.MainWindowTitle) { break }
        Start-Sleep -Milliseconds 500
    }
    Check (-not $p.HasExited) "$name opens and keeps running"
    Check ($p.MainWindowTitle -like 'WinSolve*') "$name shows its window (title: '$($p.MainWindowTitle)')"
    Start-Sleep -Seconds 12   # let the first page load and the background preloading run
    $p.Refresh()
    Check (-not $p.HasExited) "$name still running after loading its pages"
    Screenshot $name
    $new = (Log-Text).Substring([Math]::Min($before, (Log-Text).Length))
    $errors = $new -split "`n" | Where-Object { $_ -match 'Unhandled|crash|Unobserved task exception|Exception:' }
    Check ($errors.Count -eq 0) "$name logged no errors$(if ($errors) { ': ' + ($errors -join ' / ') })"
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(10000) | Out-Null }
}
