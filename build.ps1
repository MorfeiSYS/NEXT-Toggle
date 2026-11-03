# Builds publish\NEXT-Toggle.exe (self-contained single file) and, if Inno Setup is installed,
# the installer in installer\Output.
# The version comes from <Version> in src\NextToggle\NEXT-Toggle.csproj (the only place to change it).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content src\NextToggle\NEXT-Toggle.csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in NEXT-Toggle.csproj" }
Write-Host "Building NEXT-Toggle $version"

dotnet publish src/NextToggle -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Write-Host "Built publish\NEXT-Toggle.exe"

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    & $iscc "/DAppVersion=$version" installer\NEXT-Toggle.iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
    Write-Host "Built installer\Output\NEXT-Toggle-Setup-$version.exe"
} else {
    Write-Host "Inno Setup not found; skipped the installer (get it from https://jrsoftware.org/isinfo.php)."
}
