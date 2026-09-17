[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$electronRoot = Join-Path $projectRoot "src\OGKToolBox.Electron"
$apiProject = Join-Path $projectRoot "src\OGKToolBox.Api\OGKToolBox.Api.csproj"
$userPackageCache = Join-Path $env:USERPROFILE ".nuget\packages"

function Require-Command([string]$name, [string]$installHint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "$name was not found. $installHint"
    }
}

function Find-AvailableDevPort {
    foreach ($port in 5173..5192) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
        try {
            $listener.Start()
            return $port
        }
        catch [System.Net.Sockets.SocketException] {
            continue
        }
        finally {
            $listener.Stop()
        }
    }

    throw "No available local development port was found between 5173 and 5192."
}

Require-Command "dotnet" "Install the .NET 10 SDK, then try again."
Require-Command "npm" "Install Node.js 24 (including npm), then try again."

if (-not (Test-Path (Join-Path $electronRoot "node_modules"))) {
    Write-Host "Installing frontend dependencies (needed only on first launch)..." -ForegroundColor Yellow
    Push-Location $electronRoot
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    finally { Pop-Location }
}

if (Test-Path $userPackageCache) {
    # The desktop sandbox can point NUGET_PACKAGES at an empty cache even when the
    # user's normal NuGet cache already contains every project dependency.
    $env:NUGET_PACKAGES = $userPackageCache
}

Write-Host "Building API..." -ForegroundColor Cyan
& dotnet restore $apiProject --ignore-failed-sources --nologo -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet build $apiProject --configuration Debug --no-restore --nologo -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Starting the Electron frontend and local API. Close Electron or press Ctrl+C to stop." -ForegroundColor Green
$devPort = Find-AvailableDevPort
$devUrl = "http://127.0.0.1:$devPort"
$env:OGK_DEV_URL = $devUrl
Write-Host "Using development server $devUrl" -ForegroundColor Cyan
Push-Location $electronRoot
try {
    npm run verify:controller
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    npm run build:main
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # Electron starts the API and the bundled controller executable automatically.
    npx concurrently -k "vite --host=127.0.0.1 --port=$devPort --strictPort" "wait-on $devUrl && electron ."
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
