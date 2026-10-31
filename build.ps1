# Builds dist\MacWin-Setup.exe — a single self-contained EXE (no .NET install needed on the target PC).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'MacWin\MacWin.csproj'
$dist = Join-Path $root 'dist'

if (-not (Test-Path (Join-Path $root 'MacWin\Assets\MacWin.ico'))) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\make-icon.ps1')
}

$out = Join-Path $root 'MacWin\bin\publish'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish $proj -c Release -r win-x64 -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $out 'MacWin.exe') (Join-Path $dist 'MacWin-Setup.exe') -Force
$size = [Math]::Round((Get-Item (Join-Path $dist 'MacWin-Setup.exe')).Length / 1MB, 1)
Write-Host "Done: dist\MacWin-Setup.exe ($size MB)"
