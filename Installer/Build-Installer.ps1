$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
dotnet publish (Join-Path $projectRoot 'AutoInput.csproj') -p:PublishProfile=Windows -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
$tool = Join-Path $projectRoot 'tools/wix.exe'
if (-not (Test-Path -LiteralPath $tool)) {
    dotnet tool install wix --version 5.0.2 --tool-path (Join-Path $projectRoot 'tools')
    if ($LASTEXITCODE -ne 0) { throw 'WiX installation failed.' }
}
New-Item -ItemType Directory -Path (Join-Path $projectRoot 'dist') -Force | Out-Null
& $tool build (Join-Path $PSScriptRoot 'Package.wxs') -arch x64 -d "SourceDir=$projectRoot" -o (Join-Path $projectRoot 'dist/AutoInput-Setup-1.1.0-x64.msi')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
