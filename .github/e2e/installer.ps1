# Installs WinSolve silently, checks everything the installer promises, opens the app,
# reinstalls over a running copy, and uninstalls checking that nothing is left behind.
param([Parameter(Mandatory)][string]$Setup, [ValidateSet('user', 'all')][string]$Scope = 'user')
. "$PSScriptRoot\common.ps1"

$Setup = (Resolve-Path $Setup).Path
$all = $Scope -eq 'all'
$dir = if ($all) { "$env:ProgramFiles\WinSolve" } else { "$env:LOCALAPPDATA\Programs\WinSolve" }
$hive = if ($all) { 'HKLM:' } else { 'HKCU:' }
$uninstallKey = "$hive\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSolve"
$startMenu = if ($all) { [Environment]::GetFolderPath('CommonPrograms') } else { [Environment]::GetFolderPath('Programs') }
$desktop = if ($all) { [Environment]::GetFolderPath('CommonDesktopDirectory') } else { [Environment]::GetFolderPath('DesktopDirectory') }
$version = (Get-Item $Setup).VersionInfo.ProductVersion -replace '\+.*', ''
$flag = if ($all) { ' --allusers' } else { '' }

Write-Host "== Install ($Scope) to $dir"
$code = Run-Setup $Setup "--install --quiet --desktop$flag" "setup-$Scope"
Check ($code -eq 0) "installer exits with 0 (got $code)"
$exe = Join-Path $dir 'WinSolve.exe'
Check (Test-Path $exe) 'WinSolve.exe is installed'
Check (Test-Path (Join-Path $dir 'Uninstall WinSolve.exe')) 'uninstaller is installed'
Check ((Get-Item $exe).VersionInfo.ProductVersion -like "$version*") "installed version is $version ($((Get-Item $exe).VersionInfo.ProductVersion))"
Check (Test-Path "$startMenu\WinSolve.lnk") 'Start menu shortcut'
Check (Test-Path "$desktop\WinSolve.lnk") 'desktop shortcut'
if (Test-Path "$startMenu\WinSolve.lnk") {
    $target = (New-Object -ComObject WScript.Shell).CreateShortcut("$startMenu\WinSolve.lnk").TargetPath
    Check ($target -eq $exe) "shortcut points at the installed exe ($target)"
}
$reg = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
Check ($null -ne $reg) "listed in Apps & features ($uninstallKey)"
if ($reg) {
    Check ($reg.DisplayVersion -eq $version) "Apps & features shows version $version ($($reg.DisplayVersion))"
    Check ($reg.InstallLocation -eq $dir) 'install location registered'
    Check ($reg.QuietUninstallString -like '*--uninstall --quiet*') 'quiet uninstall command registered'
}
if ($all) {
    $acl = (Get-Acl $dir).Access | Where-Object { $_.IdentityReference -match 'Users|Everyone|Authenticated' -and $_.FileSystemRights -match 'Write|Modify|FullControl' }
    Check ($null -eq $acl) 'standard users cannot change the Program Files folder'
}

Write-Host "== Open the installed app"
Test-Launch $exe "app-$Scope"

Write-Host "== Reinstall while WinSolve is running (what an update does)"
$running = Start-Process $exe -PassThru
Start-Sleep -Seconds 8
$code = Run-Setup $Setup "--install --quiet$flag --update-dir `"$dir`"" "reinstall-$Scope"
Check ($code -eq 0) "reinstall over a running copy exits with 0 (got $code)"
Check ($running.HasExited) 'the running copy was closed for the update'
Check (Test-Path $exe) 'WinSolve.exe still there after the reinstall'
Check (-not (Test-Path "$exe.new") -and -not (Test-Path "$exe.old")) 'no leftover .new/.old files'

if ($all) {
    Write-Host "== Headless scheduled maintenance (runs as SYSTEM in real use)"
    $before = (Log-Text).Length
    $m = Start-Process $exe -ArgumentList '--maintenance' -PassThru
    if (-not $m.WaitForExit(600000)) { Stop-Process -Id $m.Id -Force }
    $log = (Log-Text).Substring([Math]::Min($before, (Log-Text).Length))
    $log -split "`n" | Where-Object { $_ -match 'maintenance' } | ForEach-Object { Write-Host "    | $_" }
    Check ($log -match 'Scheduled maintenance finished') 'maintenance run finishes'
    Check ($log -notmatch '\] .* failed:') 'no maintenance task failed'

    # What WinSolve's settings create when the user turns on "Start with Windows" and scheduled
    # maintenance: the uninstaller must remove both tasks.
    schtasks /create /f /tn "WinSolve" /tr "`"$exe`" --tray" /sc onlogon /rl highest | Out-Null
    schtasks /create /f /tn "WinSolve Maintenance" /tr "`"$exe`" --maintenance" /sc weekly /ru SYSTEM | Out-Null
}

Copy-Item "$env:ProgramData\WinSolve\logs\*" (Join-Path $OutDir "winsolve-$Scope.log") -ErrorAction SilentlyContinue

Write-Host "== Uninstall"
$code = Run-Setup (Join-Path $dir 'Uninstall WinSolve.exe') "--uninstall --quiet$flag" "uninstall-$Scope"
Check ($code -eq 0) "uninstaller exits with 0 (got $code)"
$deadline = (Get-Date).AddSeconds(30)
while ((Test-Path $dir) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
Check (-not (Test-Path $dir)) 'install folder removed'
Check (-not (Test-Path "$startMenu\WinSolve.lnk")) 'Start menu shortcut removed'
Check (-not (Test-Path "$desktop\WinSolve.lnk")) 'desktop shortcut removed'
Check (-not (Test-Path $uninstallKey)) 'removed from Apps & features'
if ($all) {
    schtasks /query /tn "WinSolve" 2>$null | Out-Null
    Check ($LASTEXITCODE -ne 0) '"Start with Windows" task removed'
    schtasks /query /tn "WinSolve Maintenance" 2>$null | Out-Null
    Check ($LASTEXITCODE -ne 0) 'scheduled maintenance task removed'
    schtasks /delete /f /tn "WinSolve Maintenance" 2>$null | Out-Null
    Check (-not (Test-Path "$env:ProgramData\WinSolve")) 'settings and logs removed (ProgramData)'
}
$global:LASTEXITCODE = 0
Finish "Installer ($Scope)"
