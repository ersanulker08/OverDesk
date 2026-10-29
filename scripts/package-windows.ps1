# Builds a self-contained OverDesk for Windows into .\dist\OverDesk-win-x64 (no .NET install needed).
# Usage: .\scripts\package-windows.ps1 [-Rid win-x64|win-arm64]
param([string]$Rid = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist\OverDesk-$Rid"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root "src\Overdesk.App") -c Release -r $Rid --self-contained true -o $out
# Bundled tools (DevKit, Converter): each tool folder ships its manifest and bin\ next to the app.
Get-ChildItem (Join-Path $root "tools") -Directory | ForEach-Object {
    dotnet build $_.FullName -c Release
    $dest = Join-Path $out "tools\$($_.Name)"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $_.FullName "overdesk.plugin.json") $dest
    Copy-Item (Join-Path $_.FullName "bin") $dest -Recurse
}
Write-Host "Built $out (run Overdesk.App.exe)"
