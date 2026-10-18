# Runs the portable edition from an NTFS folder, from a FAT32 "USB stick" (a virtual disk) and an
# installed-style build with a WinSolve.portable marker, and checks it keeps its data next to the
# .exe and leaves nothing in %ProgramData% (or the .NET extraction folder).
param([Parameter(Mandatory)][string]$Portable, [Parameter(Mandatory)][string]$App)
. "$PSScriptRoot\common.ps1"

$Portable = (Resolve-Path $Portable).Path
$App = (Resolve-Path $App).Path
$installed = "$env:ProgramData\WinSolve"
$fallback = "$env:ProgramData\WinSolve-Portable"

function Snapshot([string]$dir) {
    if (-not (Test-Path $dir)) { return '' }
    (Get-ChildItem $dir -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join "`n"
}

function Run-Portable([string]$exe, [string]$name) {
    $p = Start-Process $exe -PassThru
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0 -and $p.MainWindowTitle) { break }
        Start-Sleep -Milliseconds 500
    }
    Check (-not $p.HasExited) "$name opens and keeps running"
    Start-Sleep -Seconds 12
    Screenshot $name
    # Closing the window exits (CloseToTray is off in the seeded settings), which runs the exit cleanup.
    $p.CloseMainWindow() | Out-Null
    Check ($p.WaitForExit(30000)) "$name closes when its window is closed"
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(10000) | Out-Null }
}

function Test-Portable([string]$exe, [string]$name) {
    $dir = Split-Path $exe
    Remove-Item $fallback -Recurse -Force -ErrorAction SilentlyContinue
    $before = Snapshot $installed
    $data = Join-Path $dir 'WinSolveData'
    # Settings of a portable copy that was already used: no welcome screen, closing the window exits.
    New-Item -ItemType Directory -Force $data | Out-Null
    '{ "CloseToTray": false, "ScanOnStartup": false, "LastSeenVersion": "999.0.0", "Language": "en" }' | Set-Content (Join-Path $data 'settings.json') -Encoding utf8
    Run-Portable $exe $name
    $logs = Get-ChildItem (Join-Path $data 'logs') -Filter *.log -ErrorAction SilentlyContinue
    Check ($null -ne $logs) "$name keeps its logs next to the .exe ($data\logs)"
    if ($logs) {
        $text = $logs | Get-Content -Raw
        Check ($text -match 'Portable mode') "$name logged its portable mode"
        $bad = $text -split "`n" | Where-Object { $_ -match 'Unhandled|crash|Unobserved task exception|Exception:' }
        Check ($bad.Count -eq 0) "$name logged no errors$(if ($bad) { ': ' + ($bad -join ' / ') })"
        Write-Host ($text -split "`n" | Select-String 'Portable' | Out-String)
    }
    Check ((Snapshot $installed) -eq $before) "$name didn't touch $installed"
    Check (-not (Test-Path $fallback)) "$name created no $fallback (nothing to undo)"
}

Write-Host "== Portable build in an NTFS folder"
$ntfs = Join-Path $env:RUNNER_TEMP 'portable-ntfs'
New-Item -ItemType Directory -Force $ntfs | Out-Null
Copy-Item $Portable (Join-Path $ntfs 'WinSolve-Portable.exe')
Test-Portable (Join-Path $ntfs 'WinSolve-Portable.exe') 'portable-ntfs'
$extract = Get-ChildItem "$env:TEMP\.net" -Directory -ErrorAction SilentlyContinue | Where-Object Name -like 'WinSolve*'
Check ($null -eq $extract) "nothing extracted to $env:TEMP\.net$(if ($extract) { ': ' + ($extract.FullName -join ', ') })"

Write-Host "== Installed build with a WinSolve.portable marker"
$marker = Join-Path $env:RUNNER_TEMP 'portable-marker'
New-Item -ItemType Directory -Force $marker | Out-Null
Copy-Item $App (Join-Path $marker 'WinSolve.exe')
New-Item -ItemType File (Join-Path $marker 'WinSolve.portable') -Force | Out-Null
Test-Portable (Join-Path $marker 'WinSolve.exe') 'portable-marker'

Write-Host "== Portable build on a FAT32 USB stick (virtual disk)"
$vhd = Join-Path $env:RUNNER_TEMP 'stick.vhdx'
"create vdisk file=`"$vhd`" maximum=600 type=expandable`nselect vdisk file=`"$vhd`"`nattach vdisk`ncreate partition primary`nformat fs=fat32 quick label=STICK`nassign letter=U" |
    Set-Content (Join-Path $env:RUNNER_TEMP 'stick.txt') -Encoding ascii
diskpart /s (Join-Path $env:RUNNER_TEMP 'stick.txt') | Out-Null
Check ((Get-Volume -DriveLetter U -ErrorAction SilentlyContinue).FileSystem -eq 'FAT32') 'FAT32 stick mounted as U:'
Copy-Item $Portable 'U:\WinSolve-Portable.exe'
Test-Portable 'U:\WinSolve-Portable.exe' 'portable-fat32'
"select vdisk file=`"$vhd`"`ndetach vdisk" | Set-Content (Join-Path $env:RUNNER_TEMP 'stick-off.txt') -Encoding ascii
diskpart /s (Join-Path $env:RUNNER_TEMP 'stick-off.txt') | Out-Null

Finish 'Portable mode'
