param([string]$DotNet = 'D:\VSYS\.dotnet-sdk\dotnet.exe', [switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath $DotNet)) { $DotNet = (Get-Command dotnet -ErrorAction Stop).Source }
& $DotNet build (Join-Path $projectRoot 'PhotoTone.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$reports = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$testReport = Join-Path $reports 'self-tests.json'
& $DotNet (Join-Path $projectRoot 'bin\Release\net8.0-windows\PhotoTone.dll') --self-test $testReport
if ($LASTEXITCODE -ne 0) { throw "Self-tests failed. See $testReport" }
if ($Publish) {
    & $DotNet publish (Join-Path $projectRoot 'PhotoTone.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $projectRoot 'dist\PhotoTone') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
