<#
  Downloads the pinned virtual-display-rs v0.3.1 driver asset and verifies its SHA-256
  before extracting it. Refuses to proceed on any mismatch (supply-chain protection).
  See driver-pin.md for the pinned version + hash of record.
#>
$ErrorActionPreference = "Stop"

$dir      = Join-Path $PSScriptRoot "driver"
$url      = "https://github.com/MolotovCherry/virtual-display-rs/releases/download/v0.3.1/virtual-desktop-driver-installer-x64.zip"
$expected = "B3E3A5AB9B49BD56A7E753120CDDB1D913479BC5C0DFA781C3D43392DDE2FB75"
$zip      = Join-Path $dir "virtual-desktop-driver-installer-x64.zip"

New-Item -ItemType Directory -Force -Path $dir | Out-Null

if (-not (Test-Path $zip)) {
    Write-Host "Downloading pinned driver v0.3.1..."
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
}

$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($actual -ne $expected) {
    Remove-Item $zip -Force
    throw "SHA-256 mismatch for the driver asset!`n  expected $expected`n  got      $actual`nRefusing to use this file."
}

Remove-Item (Join-Path $dir "extracted") -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $zip -DestinationPath (Join-Path $dir "extracted") -Force
Write-Host "Driver asset verified (SHA-256 OK) and extracted to driver\extracted."
