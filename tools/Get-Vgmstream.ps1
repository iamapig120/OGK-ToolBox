param(
    [string]$ArchiveUrl = "https://github.com/vgmstream/vgmstream/releases/download/r2117/vgmstream-win64.zip",
    [string]$ExpectedSha256 = "6C4A8A3813864FEFED081BBD337DBC0AD93BF88E0B92F5DB98D7AB258B22DC6C"
)

$ErrorActionPreference = "Stop"
$target = Join-Path $PSScriptRoot "vgmstream"
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) "ogktoolbox-vgmstream"
$archive = "$temporary.zip"
Invoke-WebRequest -Uri $ArchiveUrl -OutFile $archive
if ($ExpectedSha256) {
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actual -ne $ExpectedSha256) { throw "vgmstream SHA-256 mismatch: $actual" }
}
if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
Expand-Archive -LiteralPath $archive -DestinationPath $temporary -Force
$executable = Get-ChildItem -Path $temporary -Recurse -Filter "vgmstream-cli.exe" | Select-Object -First 1
if (-not $executable) { throw "Archive does not contain vgmstream-cli.exe" }
Get-ChildItem -LiteralPath $executable.DirectoryName -File | Where-Object Name -ne "README.md" | Copy-Item -Destination $target -Force
Write-Host "vgmstream prepared at $target"
