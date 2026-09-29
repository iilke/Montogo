<#
  Builds MontogoSetup.exe:
    1. fetches + verifies the pinned v0.3.1 driver (fetch-driver.ps1),
    2. publishes Montogo.App and Montogo.DriverHelper self-contained (win-x64) into one
       folder so they share a single bundled .NET runtime (no .NET install needed), then
    3. compiles the Inno Setup installer.
  Output: installer\windows\dist\MontogoSetup.exe
#>
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$repo = Resolve-Path (Join-Path $root "..\..")
$app  = Join-Path $root "build\app"

Write-Host "== 1/3  Fetch + verify pinned driver =="
& (Join-Path $root "fetch-driver.ps1")

Write-Host "== 2/3  Publish app + helper (self-contained, shared runtime) =="
Remove-Item (Join-Path $root "build") -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $repo "windows\Montogo.App\Montogo.App.csproj") `
    -c Release -r win-x64 --self-contained -o $app
# Publish the helper self-contained INTO the same folder: it overwrites the framework-
# dependent copy with a self-contained apphost and shares the runtime already there.
dotnet publish (Join-Path $repo "windows\Montogo.DriverHelper\Montogo.DriverHelper.csproj") `
    -c Release -r win-x64 --self-contained -o $app

Write-Host "== 3/3  Compile installer =="
$iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { throw "Inno Setup (ISCC.exe) not found at $iscc" }
& $iscc (Join-Path $root "Montogo.iss")

Write-Host "`nDone -> $(Join-Path $root 'dist\MontogoSetup.exe')"
