param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build.ps1')
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$buildDirectory = Join-Path $PSScriptRoot 'build'
$setup = Join-Path $OutputDirectory 'ProxyDeck-Setup.exe'
Copy-Item -LiteralPath (Join-Path $buildDirectory 'ProxyDeck.exe') -Destination $setup -Force
Copy-Item -LiteralPath (Join-Path $buildDirectory 'ProxyDeck.exe.config') -Destination ($setup+'.config') -Force
$bundle = Join-Path $OutputDirectory 'ProxyDeck-Windows-x64.zip'
Compress-Archive -LiteralPath @($setup,($setup+'.config'),(Join-Path (Split-Path $PSScriptRoot -Parent) 'README.md'),(Join-Path (Split-Path $PSScriptRoot -Parent) 'THIRD-PARTY-NOTICES.md'),(Join-Path $PSScriptRoot 'Restore-Autostart.ps1')) -DestinationPath $bundle -Force
$hashes = @($setup,$bundle) | ForEach-Object { $hash=Get-FileHash -LiteralPath $_ -Algorithm SHA256; $hash.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_) }
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'),$hashes,(New-Object Text.UTF8Encoding($false)))
Write-Output ('Release files: '+$OutputDirectory)
