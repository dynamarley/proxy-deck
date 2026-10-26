$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$answer = [Windows.Forms.MessageBox]::Show('Uninstall Proxy Deck? The app and its managed proxy will stop. Your accounts, keys, CLIProxy and Tailscale files will be kept.','Proxy Deck','YesNo','Question')
if ($answer -ne 'Yes') { exit 0 }
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ProxyDeck'))
$scriptRoot = [IO.Path]::GetFullPath($PSScriptRoot)
if ($scriptRoot -ne $installRoot) { throw 'Unexpected uninstaller location.' }
$executable = Join-Path $installRoot 'ProxyDeck.exe'
Start-Process -FilePath $executable -ArgumentList '--shutdown' -WindowStyle Hidden -Wait
$remaining = @()
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    $remaining = @(Get-Process -Name ProxyDeck -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $executable } catch { $false } })
    if ($remaining.Count -eq 0) { break }
    Start-Sleep -Milliseconds 500
}
if ($remaining.Count -gt 0) {
    [Windows.Forms.MessageBox]::Show('Could not close Proxy Deck. Select Exit and stop system from the tray menu, then try again.','Proxy Deck') | Out-Null
    exit 1
}
# Delete only these explicit application files. Never recursively remove account data.
foreach ($name in @('ProxyDeck.exe','ProxyDeck.exe.config','ProxyDeck.ico','ProxyDeck.exe.previous')) {
    $target = [IO.Path]::GetFullPath((Join-Path $installRoot $name))
    if (-not $target.StartsWith(($installRoot+'\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected uninstall target.' }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
}
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Proxy Deck.lnk'
if (Test-Path -LiteralPath $shortcutPath) {
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcutPath).TargetPath -eq $executable) { Remove-Item -LiteralPath $shortcutPath }
}
if (Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProxyDeck') { Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProxyDeck' }
[Windows.Forms.MessageBox]::Show('Proxy Deck was uninstalled. Your accounts, settings and independent components were kept. Use Restore-Autostart.ps1 to restore previous Tailscale service settings.','Proxy Deck') | Out-Null
