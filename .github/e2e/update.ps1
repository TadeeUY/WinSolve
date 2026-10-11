# Installs the previous public release (0.13.1) and updates it with this build exactly the way
# WinSolve's updater does: same arguments, the automatic setup window, and the app restarting.
param([Parameter(Mandatory)][string]$Setup)
. "$PSScriptRoot\common.ps1"

$Setup = (Resolve-Path $Setup).Path
$dir = "$env:ProgramFiles\WinSolve"
$exe = Join-Path $dir 'WinSolve.exe'
$old = Join-Path $OutDir 'WinSolveSetup-0.13.1.exe'
Invoke-WebRequest 'https://github.com/TadeeUY/WinSolve/releases/download/v0.13.1/WinSolveSetup.exe' -OutFile $old

Write-Host "== Install 0.13.1 for all users"
# 0.13.1 has no silent mode: its automatic window stays open at the end, so close it once the files are in.
$p = Start-Process $old -ArgumentList '--install --auto --allusers' -PassThru
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and -not ((Test-Path $exe) -and (Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSolve' -ErrorAction SilentlyContinue))) { Start-Sleep -Seconds 1 }
Start-Sleep -Seconds 2
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
Check ((Get-Item $exe -ErrorAction SilentlyContinue).VersionInfo.ProductVersion -like '0.13.1*') '0.13.1 installed'

Write-Host "== Update to this build like the updater does"
$app = Start-Process $exe -PassThru
Start-Sleep -Seconds 10
# UpdateService.InstallAsync: "--install --auto --launch --allusers --update-dir <dir> --wait <pid>".
# The app normally exits by itself after starting setup; here it is closed a moment later.
$s = Start-Process $Setup -ArgumentList "--install --auto --launch --allusers --update-dir `"$dir`" --wait $($app.Id)" -PassThru
Start-Sleep -Seconds 3
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Check ($s.WaitForExit(120000)) 'setup finishes and closes by itself'
Screenshot 'update-finished'
$version = (Get-Item $Setup).VersionInfo.ProductVersion -replace '\+.*', ''
Check ((Get-Item $exe).VersionInfo.ProductVersion -like "$version*") "updated to $version"
Check ((Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSolve').DisplayVersion -eq $version) 'Apps & features shows the new version'
Start-Sleep -Seconds 10
$restarted = Get-Process WinSolve -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
Check ($null -ne $restarted) 'WinSolve restarted after the update'
Screenshot 'app-after-update'
$restarted | Stop-Process -Force -ErrorAction SilentlyContinue

Start-Process (Join-Path $dir 'Uninstall WinSolve.exe') -ArgumentList '--uninstall --quiet --allusers' -Wait
Finish 'Update from 0.13.1'
