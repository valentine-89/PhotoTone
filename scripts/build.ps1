param([string]$DotNet = 'D:\VSYS\.dotnet-sdk\dotnet.exe', [switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'PhotoTone.csproj') -Raw)).Project.PropertyGroup.Version
if (-not (Test-Path -LiteralPath $DotNet)) { $DotNet = (Get-Command dotnet -ErrorAction Stop).Source }
& $DotNet build (Join-Path $projectRoot 'PhotoTone.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$reports = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$testReport = Join-Path $reports 'self-tests.json'
& $DotNet (Join-Path $projectRoot 'bin\Release\net8.0-windows\PhotoTone.dll') --self-test $testReport
if ($LASTEXITCODE -ne 0) { throw "Self-tests failed. See $testReport" }
$smokeReport = Join-Path $reports 'ui-smoke.png'
& $DotNet (Join-Path $projectRoot 'bin\Release\net8.0-windows\PhotoTone.dll') --smoke-test $smokeReport
if ($LASTEXITCODE -ne 0) { throw "UI smoke failed. See $smokeReport.error.txt" }
if ($Publish) {
    & $DotNet publish (Join-Path $projectRoot 'PhotoTone.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $projectRoot 'dist\PhotoTone') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $publishedExe = Join-Path $projectRoot 'dist\PhotoTone\PhotoTone.exe'
    $publishedTests = Join-Path $reports 'published-self-tests.json'
    $publishedSmoke = Join-Path $reports "published-ui-$version.png"
    $testProcess = Start-Process -FilePath $publishedExe -ArgumentList @('--self-test', ('"' + $publishedTests + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($testProcess.ExitCode -ne 0) { throw 'Published self-tests failed.' }
    $smokeProcess = Start-Process -FilePath $publishedExe -ArgumentList @('--smoke-test', ('"' + $publishedSmoke + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($smokeProcess.ExitCode -ne 0) { throw 'Published UI smoke failed.' }
    $updateReport = Join-Path $reports 'published-update-smoke.json'
    $updateProcess = Start-Process -FilePath $publishedExe -ArgumentList @('--update-smoke', ('"' + $updateReport + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($updateProcess.ExitCode -ne 0) { throw "Published update smoke failed. See $updateReport.error.txt" }
}
