# Portable publish: self-contained win-x64 folder (not MSI / not forced single-file)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $Root "publish\AutoProdScripts"

Write-Host "Publishing AutoProdScripts -> $Out" -ForegroundColor Cyan

dotnet publish (Join-Path $Root "src\AutoProdScripts\AutoProdScripts.csproj") `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o $Out

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Готово. Скопируйте папку:" -ForegroundColor Green
Write-Host "  $Out"
Write-Host "и запустите AutoProdScripts.exe"
