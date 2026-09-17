param([Parameter(Mandatory)][string]$Version, [string]$Repo = "lynshp/OGKToolBox-releases")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $root "artifacts/electron"
$sourceName = "OGK ToolBox Setup $Version.exe"
$name = "OGK-ToolBox-Setup-$Version.exe"
foreach ($suffix in @("", ".blockmap")) {
    $source = Join-Path $dir ($sourceName + $suffix)
    $target = Join-Path $dir ($name + $suffix)
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $target -Force }
    if (-not (Test-Path -LiteralPath $target)) { throw "Missing release file: $target" }
}
$metadataPath = Join-Path $dir "latest.yml"
$metadata = (Get-Content -LiteralPath $metadataPath -Raw).Replace($sourceName, $name)
$escapedVersion = [regex]::Escape($Version)
if ($metadata -notmatch "(?m)^version: $escapedVersion\s*$" -or -not $metadata.Contains($name)) {
    throw "latest.yml version or filename mismatch."
}
$algorithm = [System.Security.Cryptography.SHA512]::Create()
$stream = [System.IO.File]::OpenRead((Join-Path $dir $name))
try { $hash = [Convert]::ToBase64String($algorithm.ComputeHash($stream)) }
finally { $stream.Dispose(); $algorithm.Dispose() }
if (-not $metadata.Contains("sha512: $hash")) { throw "latest.yml checksum mismatch." }
[System.IO.File]::WriteAllText($metadataPath, $metadata, [System.Text.UTF8Encoding]::new($false))
$gh = (Get-Command gh -ErrorAction Stop).Source
& $gh release create "v$Version" --repo $Repo --title "OGKToolBox v$Version" --latest --notes "Windows x64 installer with the matching precompiled controller module." (Join-Path $dir $name) (Join-Path $dir "$name.blockmap") $metadataPath
if ($LASTEXITCODE -ne 0) { throw "Release creation failed; existing releases are never deleted or overwritten by this script." }
