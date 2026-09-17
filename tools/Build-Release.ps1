param([string]$Version, [switch]$Publish)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$electron = Join-Path $repo "src/OGKToolBox.Electron"
$appVersion = (Get-Content -Raw (Join-Path $electron "package.json") | ConvertFrom-Json).version
if (-not $Version) { $Version = $appVersion }
if ($Version -ne $appVersion) { throw "Update the app and compatible controller bundle before releasing $Version." }
if (-not (Test-Path (Join-Path $repo "tools/vgmstream/vgmstream-cli.exe"))) {
    throw "Run tools/Get-Vgmstream.ps1 before packaging."
}
Push-Location $repo
try {
    dotnet test OGKToolBox.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw "Application tests failed." }
} finally { Pop-Location }
Push-Location $electron
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
    npm run verify:controller
    if ($LASTEXITCODE -ne 0) { throw "Controller artifact verification failed." }
    npm test
    if ($LASTEXITCODE -ne 0) { throw "Electron tests failed." }
    npm run typecheck
    if ($LASTEXITCODE -ne 0) { throw "Electron typecheck failed." }
    npm run pack:win
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
} finally { Pop-Location }
$resources = Join-Path $repo "artifacts/electron/win-unpacked/resources"
foreach ($name in @("OGKToolBox.Api.exe", "coreclr.dll", "hostfxr.dll", "e_sqlite3.dll")) {
    if (-not (Test-Path (Join-Path $resources "api/$name"))) { throw "Missing packaged API file: $name" }
}
foreach ($name in @("OGKToolBox.ControllerHost.exe", "module.json", "artifact.json", "LICENSE.txt")) {
    $source = Join-Path $electron "resources/controller/$name"
    $target = Join-Path $resources "controller/$name"
    if ((Get-FileHash $source -Algorithm SHA256).Hash -ne (Get-FileHash $target -Algorithm SHA256).Hash) {
        throw "Packaged controller file does not match: $name"
    }
}
Write-Host "Verified packaged API runtime and precompiled controller $Version."
if ($Publish) { & (Join-Path $repo "tools/Publish-PublicRelease.ps1") -Version $Version }
else { Write-Host "Installer written to artifacts/electron. Nothing was uploaded." }
