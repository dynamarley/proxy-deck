param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build.ps1')
$OutputDirectory = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$buildDirectory = Join-Path $PSScriptRoot 'build'
$setup = Join-Path $OutputDirectory 'ProxyDeck-Setup.exe'
Copy-Item -LiteralPath (Join-Path $buildDirectory 'ProxyDeck.exe') -Destination $setup -Force
Copy-Item -LiteralPath (Join-Path $buildDirectory 'ProxyDeck.exe.config') -Destination ($setup+'.config') -Force
$bundle = Join-Path $OutputDirectory 'ProxyDeck-Windows-x64.zip'
$files = @($setup,($setup+'.config'),(Join-Path (Split-Path $PSScriptRoot -Parent) 'README.md'),(Join-Path (Split-Path $PSScriptRoot -Parent) 'LICENSE'),(Join-Path (Split-Path $PSScriptRoot -Parent) 'THIRD-PARTY-NOTICES.md'),(Join-Path $PSScriptRoot 'Restore-Autostart.ps1'))
# Compress-Archive opens source files exclusively and fails while another reader
# (for example a scanner) has them open. ZipFileExtensions allows shared reads.
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$pendingBundle = $bundle + '.pending'
if (Test-Path -LiteralPath $pendingBundle) { Remove-Item -LiteralPath $pendingBundle -Force }
$archive = [IO.Compression.ZipFile]::Open($pendingBundle, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, [IO.Path]::GetFileName($file), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Move-Item -LiteralPath $pendingBundle -Destination $bundle -Force
$hashes = @($setup,($setup+'.config'),$bundle) | ForEach-Object { $hash=Get-FileHash -LiteralPath $_ -Algorithm SHA256; $hash.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_) }
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'),$hashes,(New-Object Text.UTF8Encoding($false)))
Write-Output ('Release files: '+$OutputDirectory)
