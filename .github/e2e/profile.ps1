# Applies .winsolve profiles from the command line (WinSolve.exe --apply-profile) on a real
# Windows: dry run, apply, undo, invalid profile and an unaccepted warning, checking the registry,
# the exit codes and the log.
param([Parameter(Mandatory)][string]$App)
. "$PSScriptRoot\common.ps1"
$App = (Resolve-Path $App).Path
$dir = Join-Path $env:RUNNER_TEMP 'profiles'
New-Item -ItemType Directory -Force $dir | Out-Null

$adv = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$desktop = 'HKCU:\Control Panel\Desktop'
function State { "$((Get-ItemProperty $adv -Name HideFileExt -ErrorAction SilentlyContinue).HideFileExt)|$((Get-ItemProperty $desktop -Name MenuShowDelay -ErrorAction SilentlyContinue).MenuShowDelay)" }

function Profile([string]$name, [string]$json) {
    $path = Join-Path $dir "$name.winsolve"
    Set-Content $path $json -Encoding utf8
    return $path
}

function Apply([string]$path, [string]$extra, [string]$name) {
    $out = Join-Path $OutDir "profile-$name.txt"
    $p = Start-Process $App -ArgumentList "--apply-profile `"$path`" $extra" -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    Get-Content $out, "$out.err" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    | $_" }
    return $p.ExitCode
}

# Known starting point: extensions hidden, default menu delay.
Set-ItemProperty $adv -Name HideFileExt -Value 1 -Type DWord
Set-ItemProperty $desktop -Name MenuShowDelay -Value '400'
$start = State

$on = Profile 'on' '{ "format": "winsolve-profile", "version": 1, "name": "E2E", "tweaks": { "apply": ["ui-file-extensions", "perf-menu-delay"] } }'
$off = Profile 'off' '{ "format": "winsolve-profile", "version": 1, "name": "E2E undo", "tweaks": { "revert": ["ui-file-extensions", "perf-menu-delay"] } }'

Write-Host "== Dry run"
Check ((Apply $on '--dry-run' 'dry-run') -eq 0) 'dry run exits with 0'
Check ((State) -eq $start) "dry run changed nothing ($(State))"
Check ((Get-Content (Join-Path $OutDir 'profile-dry-run.txt') -Raw) -match 'HideFileExt -> DWORD 0') 'dry run lists the registry values'

Write-Host "== Apply"
Check ((Apply $on '--silent --no-restore-point' 'apply') -eq 0) 'apply exits with 0'
Check ((State) -eq '0|50') "tweaks applied (HideFileExt|MenuShowDelay = $(State))"
$log = Get-ChildItem "$env:ProgramData\WinSolve\logs" -Filter *.log | Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content -Raw
Check ($log -match "\[profile\] Profile 'E2E' finished: 2 done, 0 failed") 'the log records the profile'

Write-Host "== Undo"
Check ((Apply $off '--silent --no-restore-point' 'undo') -eq 0) 'undo exits with 0'
Check ((State) -eq $start) "previous values are back ($(State))"

Write-Host "== Invalid and risky profiles"
$bad = Profile 'bad' '{ "format": "winsolve-profile", "version": 1, "tweaks": { "apply": ["ui-file-extensions", "no-such-tweak"] } }'
Check ((Apply $bad '--silent' 'invalid') -eq 2) 'unknown tweak: exit code 2'
Check ((State) -eq $start) 'invalid profile changed nothing'
$tz = 'HKLM:\SYSTEM\CurrentControlSet\Control\TimeZoneInformation'
$utcBefore = (Get-ItemProperty $tz -Name RealTimeIsUniversal -ErrorAction SilentlyContinue).RealTimeIsUniversal
$risky = Profile 'risky' '{ "format": "winsolve-profile", "version": 1, "tweaks": { "apply": ["adv-utc-time"] } }'
Check ((Apply $risky '--silent --no-restore-point' 'risky') -eq 5) 'tweak with a warning, not accepted: exit code 5'
Check ((Get-ItemProperty $tz -Name RealTimeIsUniversal -ErrorAction SilentlyContinue).RealTimeIsUniversal -eq $utcBefore) 'the risky tweak was not applied'
Check ((Apply (Join-Path $dir 'missing.winsolve') '--silent' 'missing') -eq 2) 'missing file: exit code 2'

Finish 'Profiles'
