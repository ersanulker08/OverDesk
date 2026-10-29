# Builds a self-contained OverDesk for Windows into .\dist\OverDesk-win-x64 (no .NET install needed).
# Usage: .\scripts\package-windows.ps1 [-Rid win-x64|win-arm64]
param([string]$Rid = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist\OverDesk-$Rid"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root "src\Overdesk.App") -c Release -r $Rid --self-contained true -o $out
Write-Host "Built $out (run Overdesk.App.exe)"
