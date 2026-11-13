<#
  Builds ShadowDisk and its installer on Windows.
  Requirements: .NET 10 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup)
  Usage:  powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 [-Version 0.1.0]
#>
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== Running tests" -ForegroundColor Cyan
dotnet test tests\ShadowDisk.Core.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed - installer not built." }

Write-Host "== Publishing self-contained app (no .NET install needed by users)" -ForegroundColor Cyan
if (Test-Path artifacts\publish) { Remove-Item artifacts\publish -Recurse -Force }
dotnet publish src\ShadowDisk.App -c Release -r win-x64 --self-contained true -p:Version=$Version -o artifacts\publish
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { $iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

Write-Host "== Building installer" -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" installer\ShadowDisk.iss
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }

Write-Host "Done: artifacts\installer\ShadowDisk-Setup-$Version.exe" -ForegroundColor Green
