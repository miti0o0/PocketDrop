# Builds everything into .\dist:
#   PocketDrop-Setup-<version>.exe   Windows installer
#   PocketDrop-<version>-portable.zip  Windows app without installer
#   PocketDrop-<version>.apk         Android app
#
# Requirements: .NET 8 SDK, Inno Setup 6, JDK 17, Android SDK (ANDROID_HOME or android\local.properties).
# Usage: powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 [-Version 1.0.0] [-SkipAndroid]
param(
    [string]$Version = "1.0.0",
    [string]$UpdateRepo = "",
    [switch]$SkipAndroid,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

function Invoke-Checked([string]$what, [scriptblock]$cmd) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

# ---- Windows -----------------------------------------------------------------------------
$desktop = Join-Path $root 'desktop'
if (-not $SkipTests) {
    Invoke-Checked 'Desktop tests' { dotnet test (Join-Path $desktop 'PocketDrop.Tests') -c Release }
}
$publish = Join-Path $desktop 'publish'

# The in-app updater checks GitHub Releases of this repository (taken from the git remote "origin").
if (-not $UpdateRepo) {
    $remote = try { & cmd /c "git -C `"$root`" remote get-url origin 2>nul" } catch { $null }
    if ($remote -match 'github\.com[:/]([^/]+/[^/.]+?)(\.git)?$') { $UpdateRepo = $Matches[1] }
}
if ($UpdateRepo) { Write-Host "Updates from: github.com/$UpdateRepo" }
else { Write-Warning 'No GitHub remote found: this build will not check for updates (use -UpdateRepo owner/repo).' }

Invoke-Checked 'Publish desktop app' {
    dotnet publish (Join-Path $desktop 'PocketDrop.Desktop') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:Version=$Version "-p:UpdateRepo=$UpdateRepo" -o $publish
}

$zip = Join-Path $dist "PocketDrop-$Version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Confirm:$false }
Compress-Archive -Path (Join-Path $publish 'PocketDrop.exe') -DestinationPath $zip

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found.' }
Invoke-Checked 'Build installer' { & $iscc /Q "/DAppVersion=$Version" (Join-Path $desktop 'installer\PocketDrop.iss') }

# ---- Android -----------------------------------------------------------------------------
if (-not $SkipAndroid) {
    $android = Join-Path $root 'android'
    Push-Location $android
    try {
        $tasks = @('assembleRelease')
        if (-not $SkipTests) { $tasks = @('testDebugUnitTest') + $tasks }
        Invoke-Checked 'Build Android app' { .\gradlew.bat @tasks --console=plain }
    } finally { Pop-Location }
    Copy-Item (Join-Path $android 'app\build\outputs\apk\release\app-release.apk') (Join-Path $dist "PocketDrop-$Version.apk") -Force
}

Write-Host "`nDone:" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
