param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'PhotoTone.csproj') -Raw)).Project.PropertyGroup.Version }
$publishFolder = Join-Path $projectRoot 'dist\PhotoTone'
$executable = Join-Path $publishFolder 'PhotoTone.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run build.ps1 -Publish first.' }
if ((Get-Item -LiteralPath $executable).VersionInfo.ProductVersion.Split('+')[0] -ne $Version) { throw 'Published EXE version does not match package version.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $publishFolder 'README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $publishFolder 'LICENSE')
$archive = Join-Path $projectRoot "dist\PhotoTone-$Version-win-x64.zip"
if (Test-Path -LiteralPath $archive) { throw "Package already exists: $archive" }
Compress-Archive -LiteralPath $executable,(Join-Path $publishFolder 'README.md'),(Join-Path $publishFolder 'LICENSE') -DestinationPath $archive -CompressionLevel Optimal
$checksums = @($executable, $archive) | ForEach-Object { $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256; '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_) }
$checksums | Set-Content -LiteralPath (Join-Path $projectRoot 'dist\SHA256SUMS.txt') -Encoding utf8
Get-Item -LiteralPath $archive | Select-Object FullName,Length
