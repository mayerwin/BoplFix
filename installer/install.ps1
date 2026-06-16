<#
  Bopl Battle Fixes installer / uninstaller (runs inside the self-extracting .exe).
  - Auto-detects the full Bopl Battle AND the free Bopl Battle Demo across all Steam
    libraries, and installs into every copy it finds.
  - If already installed, offers to Reinstall/Update or Uninstall.
  - Auto-elevates (UAC) once if any target folder needs administrator rights to write
    (the Demo usually lives under Program Files).
  - Install extracts the embedded BepInEx + plugin bundle; uninstall removes the mod
    (and the BepInEx loader too, if no other mods are present).

  Uses the OS's built-in PowerShell + .NET Framework for the GUI, so the installer
  stays tiny (no bundled runtime).
#>
param([string]$Target = "", [string]$Targets = "", [switch]$NoPrompt, [switch]$Elevated, [switch]$Uninstall, [switch]$RemoveLoader)

# Automated-test hooks (silent). A sentinel file installs; an "uninstall" sentinel uninstalls.
$sentinel = Join-Path $env:TEMP 'boplfix_install_test.txt'
$sentinelU = Join-Path $env:TEMP 'boplfix_uninstall_test.txt'
if (-not $Target -and -not $Targets -and (Test-Path $sentinelU)) { $Target = (Get-Content $sentinelU -Raw).Trim(); $NoPrompt = $true; $Uninstall = $true }
elseif (-not $Target -and -not $Targets -and (Test-Path $sentinel)) { $Target = (Get-Content $sentinel -Raw).Trim(); $NoPrompt = $true }
if ($env:BOPLFIX_TEST_TARGET) { $Target = $env:BOPLFIX_TEST_TARGET; $NoPrompt = $true }

$ErrorActionPreference = 'Stop'
# Steam app IDs to look for: the full game and the free demo. Both share the same exe and structure.
$AppIds = @(1686940, 2494960)
try { Add-Type -AssemblyName System.Windows.Forms | Out-Null } catch { }

function Show-Msg($text, $caption = 'Bopl Battle Fixes', $buttons = 'OK', $icon = 'Information') {
    if ($NoPrompt) { Write-Host "[$caption] $text"; return 'OK' }
    return [System.Windows.Forms.MessageBox]::Show($text, $caption, $buttons, $icon)
}

function Test-Admin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
    } catch { return $false }
}

function Test-Writable($dir) {
    try {
        $t = Join-Path $dir (".boplfix_wtest_" + [guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText($t, 'x'); Remove-Item $t -Force
        return $true
    } catch { return $false }
}

function Get-SteamPath {
    foreach ($k in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        try {
            $p = Get-ItemProperty -Path $k -ErrorAction Stop
            if ($p.SteamPath) { return ($p.SteamPath -replace '/', '\') }
            if ($p.InstallPath) { return $p.InstallPath }
        } catch { }
    }
    return $null
}

function Get-Libraries {
    $libs = @()
    $steam = Get-SteamPath
    if ($steam) {
        $libs += $steam
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s*"([^"]+)"')) {
                $libs += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    # Case-insensitive dedup (the registry path and libraryfolders.vdf can differ only in casing).
    $seen = @{}
    foreach ($l in $libs) {
        if (-not $l) { continue }
        $k = $l.TrimEnd('\').ToLowerInvariant()
        if (-not $seen.ContainsKey($k)) { $seen[$k] = $true; $l }
    }
}

# Returns every Bopl Battle / Bopl Battle Demo folder found across all Steam libraries.
function Get-GamePaths {
    $found = @()
    foreach ($lib in Get-Libraries) {
        $sa = Join-Path $lib 'steamapps'
        foreach ($id in $AppIds) {
            $manifest = Join-Path $sa "appmanifest_$id.acf"
            if (Test-Path $manifest) {
                $m = [regex]::Match((Get-Content $manifest -Raw), '"installdir"\s*"([^"]+)"')
                if ($m.Success) {
                    $p = Join-Path $sa "common\$($m.Groups[1].Value)"
                    if (Test-Path (Join-Path $p 'BoplBattle.exe')) { $found += $p }
                }
            }
        }
        foreach ($name in @('Bopl Battle', 'Bopl Battle Demo')) {
            $p = Join-Path $sa "common\$name"
            if (Test-Path (Join-Path $p 'BoplBattle.exe')) { $found += $p }
        }
    }
    $seen = @{}
    foreach ($p in $found) {
        $k = $p.TrimEnd('\').ToLowerInvariant()
        if (-not $seen.ContainsKey($k)) { $seen[$k] = $true; $p }
    }
}

# Friendly label from the folder name (installdir is "Bopl Battle" or "Bopl Battle Demo").
function Game-Name($path) {
    if ((Split-Path $path -Leaf) -match 'Demo') { return 'Bopl Battle Demo' }
    return 'Bopl Battle'
}

function Install-Mod($game, $zip) { Expand-Archive -Path $zip -DestinationPath $game -Force }

# True if the game has any BepInEx plugin DLL other than BoplFix.dll. Checked RECURSIVELY, so mods
# installed in their own subfolder under plugins\ are detected and never wiped by accident.
function Has-OtherMods($game) {
    $plugins = Join-Path $game 'BepInEx\plugins'
    if (-not (Test-Path $plugins)) { return $false }
    $dlls = @(Get-ChildItem $plugins -Recurse -Filter '*.dll' -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'BoplFix.dll' })
    return ($dlls.Count -gt 0)
}

function Remove-BoplFixFiles($game) {
    Remove-Item (Join-Path $game 'BepInEx\plugins\BoplFix.dll') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $game 'BepInEx\config\com.boplfix.cfg') -Force -ErrorAction SilentlyContinue
}

# Removes the whole BepInEx loader (back to vanilla). NOTE: this also removes any OTHER mods, since they
# live inside the BepInEx folder - only call it when no other mods remain, or when the user opted in.
function Remove-Loader($game) {
    Remove-Item (Join-Path $game 'BepInEx') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $game 'winhttp.dll') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $game 'doorstop_config.ini') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $game '.doorstop_version') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $game 'changelog.txt') -Force -ErrorAction SilentlyContinue
}

# Locate the embedded bundle (only required for install).
$zip = Get-ChildItem -Path $PSScriptRoot -Filter 'BoplFix-*.zip' -File -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName

# Build the list of game folders to operate on.
if ($Targets) { $games = @($Targets -split ';' | Where-Object { $_ }) }
elseif ($Target) { $games = @($Target) }
else { $games = @(Get-GamePaths) }

if (-not $games -and -not $NoPrompt) {
    Show-Msg "Couldn't find Bopl Battle automatically.`nPlease pick your Bopl Battle (or Demo) folder - it contains BoplBattle.exe." | Out-Null
    $fbd = New-Object System.Windows.Forms.FolderBrowserDialog
    $fbd.Description = 'Select your Bopl Battle (or Demo) folder'
    if ($fbd.ShowDialog() -eq 'OK') { $games = @($fbd.SelectedPath) }
}

# Keep only valid, unique game folders.
$games = @($games | Where-Object { $_ -and (Test-Path (Join-Path $_ 'BoplBattle.exe')) } | Select-Object -Unique)
if (-not $games) {
    Show-Msg "BoplBattle.exe wasn't found. Cancelled." 'Bopl Battle Fixes' 'OK' 'Error' | Out-Null
    exit 1
}

$list = ($games | ForEach-Object { "  - " + (Game-Name $_) + "   ($_)" }) -join "`n"
$anyInstalled = @($games | Where-Object { Test-Path (Join-Path $_ 'BepInEx\plugins\BoplFix.dll') }).Count -gt 0
$doUninstall = [bool]$Uninstall

# Decide the action (skipped when already chosen via elevation relaunch or silent mode).
if (-not $NoPrompt -and -not $Elevated) {
    if ($anyInstalled) {
        $r = Show-Msg ("Bopl Battle Fixes was found in:`n$list`n`n" +
            "Yes  = Reinstall / update`nNo  = Uninstall (remove it)`nCancel = do nothing") 'Bopl Battle Fixes' 'YesNoCancel' 'Question'
        if ("$r" -eq 'Cancel') { exit }
        $doUninstall = ("$r" -eq 'No')
    } else {
        $r = Show-Msg ("Install Bopl Battle Fixes into:`n$list`n`nClose the game first if it's open.`n(Both players need the mod for the LAN fix.)") 'Bopl Battle Fixes' 'OKCancel' 'Question'
        if ("$r" -ne 'OK') { exit }
    }
}

# Uninstall safety: by default KEEP the BepInEx loader when other mods are present (so we never wipe
# someone else's mod). If any copy has other mods, offer a full vanilla reset instead.
$dropLoader = [bool]$RemoveLoader
if ($doUninstall) {
    $withOthers = @($games | Where-Object { Has-OtherMods $_ })
    if ($withOthers.Count -gt 0 -and -not $NoPrompt -and -not $Elevated) {
        $ol = ($withOthers | ForEach-Object { "  - " + (Game-Name $_) + "   ($_)" }) -join "`n"
        $r = Show-Msg ("Other BepInEx mods are installed alongside BoplFix in:`n$ol`n`n" +
            "BoplFix will be removed either way. Do you also want to remove the BepInEx loader?`n`n" +
            "Yes = full vanilla reset (this ALSO removes your other mods)`n" +
            "No  = keep BepInEx and your other mods (recommended)") 'Bopl Battle Fixes' 'YesNo' 'Warning'
        $dropLoader = ("$r" -eq 'Yes')
    }
}

if (-not $doUninstall -and (-not $zip -or -not (Test-Path $zip))) {
    Show-Msg "Installer payload is missing." 'Bopl Battle Fixes' 'OK' 'Error' | Out-Null
    exit 1
}

if (Get-Process -Name 'BoplBattle' -ErrorAction SilentlyContinue) {
    Show-Msg "Bopl Battle is running. Please close it, then run this again." 'Bopl Battle Fixes' 'OK' 'Warning' | Out-Null
    exit 1
}

# Auto-elevate once if ANY target folder isn't writable (e.g. the Demo under Program Files).
$needElevation = @($games | Where-Object { -not (Test-Writable $_) })
if ($needElevation.Count -gt 0) {
    if ($Elevated -or (Test-Admin)) {
        Show-Msg ("Can't write to:`n" + ($needElevation -join "`n") + "`neven with administrator rights.") 'Bopl Battle Fixes' 'OK' 'Error' | Out-Null
        exit 1
    }
    $work = Join-Path $env:TEMP ("BoplFixInstall_" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $work | Out-Null
    Copy-Item $PSCommandPath (Join-Path $work 'install.ps1') -Force
    if ($zip -and (Test-Path $zip)) { Copy-Item $zip (Join-Path $work (Split-Path $zip -Leaf)) -Force }
    $relArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
        '-File', (Join-Path $work 'install.ps1'), '-Targets', ($games -join ';'), '-Elevated')
    if ($doUninstall) { $relArgs += '-Uninstall' }
    if ($dropLoader) { $relArgs += '-RemoveLoader' }
    try {
        Start-Process powershell -Verb RunAs -Wait -ArgumentList $relArgs
    } catch {
        Show-Msg "Administrator rights are needed for one of the folders, and elevation was cancelled." 'Administrator required' 'OK' 'Warning' | Out-Null
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    exit
}

# Do the work for every detected copy, then summarize once.
$results = @()
foreach ($g in $games) {
    $name = Game-Name $g
    try {
        if ($doUninstall) {
            Remove-BoplFixFiles $g
            if (Has-OtherMods $g) {
                if ($dropLoader) {
                    Remove-Loader $g
                    $results += "$name : removed BoplFix and BepInEx (full vanilla; other mods removed too)"
                } else {
                    $results += "$name : removed BoplFix (kept BepInEx and your other mods)"
                }
            } else {
                Remove-Loader $g
                $results += "$name : removed (back to vanilla)"
            }
        } else {
            Install-Mod $g $zip
            $results += "$name : installed"
        }
    } catch {
        $results += "$name : FAILED - $($_.Exception.Message)"
    }
}
$summary = $results -join "`n"
if ($doUninstall) {
    Show-Msg ("Bopl Battle Fixes uninstall complete:`n`n$summary") 'Uninstalled' | Out-Null
} else {
    Show-Msg ("Done! Bopl Battle Fixes is installed:`n`n$summary`n`nLaunch each game once to activate it.") 'Success' | Out-Null
}
