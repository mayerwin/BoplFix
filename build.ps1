<#
    Builds BoplFix and assembles the one-click install bundle in dist/.

    Usage:
        ./build.ps1
        ./build.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Bopl Battle"

    Requires the .NET SDK. 7-Zip is used for the bundle if present; otherwise it falls back
    to Compress-Archive (which may omit the .doorstop_version dotfile - prefer 7-Zip).
#>
param(
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\Bopl Battle",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo  = $PSScriptRoot
$proj  = Join-Path $repo "src\BoplFix\BoplFix.csproj"
$dist  = Join-Path $repo "dist"
$dll   = Join-Path $repo "src\BoplFix\bin\$Configuration\BoplFix.dll"

Write-Host "Building BoplFix ($Configuration) against: $GameDir"
dotnet build $proj -c $Configuration -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item $dll (Join-Path $dist "BoplFix.dll") -Force
Write-Host "Copied standalone plugin -> dist\BoplFix.dll"

# Assemble the one-click bundle (BepInEx loader + plugin). Provide a BepInEx zip to include it.
$bepZip = Get-ChildItem $repo -Filter "BepInEx_win_x64_*.zip" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($bepZip) {
    $stage = Join-Path $dist "_stage"
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Expand-Archive $bepZip.FullName -DestinationPath $stage -Force
    Remove-Item (Join-Path $stage "changelog.txt") -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $stage "BepInEx\plugins") | Out-Null
    Copy-Item $dll (Join-Path $stage "BepInEx\plugins\BoplFix.dll") -Force

    $bundle = Join-Path $dist "BoplFix-v1.0.3.zip"
    Remove-Item $bundle -ErrorAction SilentlyContinue
    $sevenZip = "C:\Program Files\7-Zip\7z.exe"
    if (Test-Path $sevenZip) {
        & $sevenZip a -tzip $bundle (Join-Path $stage "*") | Out-Null
    } else {
        Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $bundle -Force
    }
    Remove-Item $stage -Recurse -Force
    Write-Host "Assembled one-click bundle -> dist\BoplFix-v1.0.3.zip"
} else {
    Write-Host "No BepInEx_win_x64_*.zip found in repo root; skipped bundle. Place one there to build it."
}

# Tiny single-file installer .exe via IExpress (built into Windows). ~0.8 MB.
# It runs installer/install.ps1, which auto-detects the game, prompts, auto-elevates, and extracts the bundle.
$bundle   = Join-Path $dist "BoplFix-v1.0.3.zip"
$iexpress = Join-Path $env:WINDIR "System32\iexpress.exe"
if ((Test-Path $bundle) -and (Test-Path $iexpress)) {
    Write-Host "Building installer with IExpress..."
    $isrc = Join-Path $repo "installer\_iexpress_src"
    Remove-Item $isrc -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $isrc | Out-Null
    Copy-Item (Join-Path $repo "installer\install.ps1") (Join-Path $isrc "install.ps1") -Force
    Copy-Item $bundle (Join-Path $isrc "BoplFix-v1.0.3.zip") -Force

    $exeOut = Join-Path $dist "BoplFix-Installer.exe"
    Remove-Item $exeOut -Force -ErrorAction SilentlyContinue
    $sed = Join-Path $isrc "boplfix.sed"
    $sedLines = @(
        '[Version]', 'Class=IEXPRESS', 'SEDVersion=3',
        '[Options]', 'PackagePurpose=InstallApp', 'ShowInstallProgramWindow=0', 'HideExtractAnimation=1',
        'UseLongFileName=1', 'InsideCompressed=0', 'CAB_FixedSize=0', 'CAB_ResvCodeSigning=0', 'RebootMode=N',
        'InstallPrompt=%InstallPrompt%', 'DisplayLicense=%DisplayLicense%', 'FinishMessage=%FinishMessage%',
        'TargetName=%TargetName%', 'FriendlyName=%FriendlyName%', 'AppLaunched=%AppLaunched%',
        'PostInstallCmd=%PostInstallCmd%', 'AdminQuietInstCmd=', 'UserQuietInstCmd=', 'SourceFiles=SourceFiles',
        '[Strings]', 'InstallPrompt=', 'DisplayLicense=', 'FinishMessage=',
        "TargetName=$exeOut", 'FriendlyName=Bopl Battle Fixes Installer',
        'AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File install.ps1',
        'PostInstallCmd=<None>',
        '[SourceFiles]', "SourceFiles0=$isrc\", '[SourceFiles0]', 'install.ps1=', 'BoplFix-v1.0.3.zip='
    )
    $sedLines | Set-Content -Path $sed -Encoding ascii   # Set-Content writes CRLF (required by IExpress)
    Start-Process $iexpress -ArgumentList "/N", "/Q", $sed -Wait
    Remove-Item $isrc -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $exeOut) { Write-Host ("Built installer -> dist\BoplFix-Installer.exe ({0} KB)" -f [math]::Round((Get-Item $exeOut).Length/1KB)) }
    else { Write-Host "IExpress did not produce the installer." }
} else {
    Write-Host "Skipped installer (need the bundle zip in dist and IExpress on Windows)."
}

Write-Host "Done. Release artifacts are in dist\ : BoplFix-Installer.exe, BoplFix-v1.0.3.zip, BoplFix.dll"
