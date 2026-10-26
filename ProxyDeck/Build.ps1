param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $OutputDirectory 'ProxyDeck.exe'
$references = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Net.Http.dll','/r:System.ServiceProcess.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:Microsoft.CSharp.dll')
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
$options = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/out:' + $executable))
$options += '/resource:' + (Join-Path $PSScriptRoot 'Uninstall.ps1') + ',ProxyDeck.Uninstall.ps1'
$options += '/resource:' + (Join-Path $PSScriptRoot 'Restore-Autostart.ps1') + ',ProxyDeck.Restore-Autostart.ps1'
$options += '/resource:' + (Join-Path (Split-Path $PSScriptRoot -Parent) 'README.md') + ',ProxyDeck.README.md'
$options += '/resource:' + (Join-Path (Split-Path $PSScriptRoot -Parent) 'THIRD-PARTY-NOTICES.md') + ',ProxyDeck.THIRD-PARTY-NOTICES.md'
$options += '/resource:' + (Join-Path (Split-Path $PSScriptRoot -Parent) 'LICENSE') + ',ProxyDeck.LICENSE'
& $compiler @options @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Proxy Deck compilation failed.' }
$iconPath = Join-Path $OutputDirectory 'ProxyDeck.ico'
$iconProcess = Start-Process -FilePath $executable -ArgumentList @('--make-icon', ('"' + $iconPath + '"')) -WindowStyle Hidden -PassThru -Wait
if ($iconProcess.ExitCode -ne 0) { throw 'Icon generation failed.' }
& $compiler @options ('/win32icon:' + $iconPath) @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Proxy Deck icon build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination ($executable + '.config') -Force
Write-Output $executable
