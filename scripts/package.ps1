param([string]$Version = '1.0.1')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishFolder = Join-Path $projectRoot 'dist\PhotoTone'
$executable = Join-Path $publishFolder 'PhotoTone.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run build.ps1 -Publish first.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $publishFolder 'README.md')
$archive = Join-Path $projectRoot "dist\PhotoTone-$Version-win-x64.zip"
if (Test-Path -LiteralPath $archive) { throw "Package already exists: $archive" }
Compress-Archive -LiteralPath $executable,(Join-Path $publishFolder 'README.md') -DestinationPath $archive -CompressionLevel Optimal
$checksums = @($executable, $archive) | ForEach-Object { $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256; '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_) }
$checksums | Set-Content -LiteralPath (Join-Path $projectRoot 'dist\SHA256SUMS.txt') -Encoding utf8
Get-Item -LiteralPath $archive | Select-Object FullName,Length
