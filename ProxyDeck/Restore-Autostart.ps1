$ErrorActionPreference = 'Stop'
if (@(Get-Process -Name ProxyDeck -ErrorAction SilentlyContinue).Count -gt 0) { throw 'First exit Proxy Deck using Exit and stop system.' }
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this restore script as administrator.' }
$dataDirectory = Join-Path $env:LOCALAPPDATA 'ProxyDeck'
$backupPath = Join-Path $dataDirectory 'tailscale-service-backup.json'
if (-not (Test-Path -LiteralPath $backupPath)) { throw 'No Tailscale service backup found. No settings were changed.' }
$backup = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
$mode = switch ([int]$backup.start) { 2 {'Automatic'} 3 {'Manual'} 4 {'Disabled'} default {throw 'Unsupported saved service mode.'} }
$tailscaleExecutable = Join-Path ${env:ProgramFiles} 'Tailscale\tailscale.exe'
Start-Service Tailscale
& $tailscaleExecutable set ('--unattended='+([bool]$backup.unattended).ToString().ToLowerInvariant())
if ($LASTEXITCODE -ne 0) { throw 'Unable to restore Tailscale preferences.' }
$descriptor = New-Object Security.AccessControl.RawSecurityDescriptor($backup.sddl)
& (Join-Path $env:WINDIR 'System32\sc.exe') sdset Tailscale ($descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]::Access))
if ($LASTEXITCODE -ne 0) { throw 'Unable to restore service permissions.' }
Set-Service Tailscale -StartupType $mode
$savedLink = Join-Path $dataDirectory 'startup-backup\Tailscale.lnk'
$restoreLink = Join-Path ([Environment]::GetFolderPath('CommonStartup')) 'Tailscale.lnk'
if ((Test-Path -LiteralPath $savedLink) -and -not (Test-Path -LiteralPath $restoreLink)) { Move-Item -LiteralPath $savedLink -Destination $restoreLink }
Write-Output 'Previous Tailscale startup and service permissions restored.'
