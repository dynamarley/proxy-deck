param([switch]$Launch)
$ErrorActionPreference = 'Stop'
# Developer convenience only. End users open ProxyDeck-Setup.exe from Releases.
& (Join-Path $PSScriptRoot 'Build.ps1')
if ($Launch) {
    Start-Process -FilePath (Join-Path $PSScriptRoot 'build\ProxyDeck.exe') -WindowStyle Hidden
} else {
    Write-Output 'Open build\ProxyDeck.exe to install for the current Windows user.'
}
