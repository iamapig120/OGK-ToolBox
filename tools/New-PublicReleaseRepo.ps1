# Create the public installer feed repository. Source stays in lynshp/OGKToolBox.
param(
    [string]$Owner = "lynshp",
    [string]$Name = "OGKToolBox-releases"
)

$ErrorActionPreference = "Stop"
$gh = @(
    "$env:ProgramFiles\GitHub CLI\gh.exe",
    "$env:LOCALAPPDATA\GitHub CLI\gh.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $gh) {
    $cmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($cmd) { $gh = $cmd.Source }
}
if (-not $gh) { throw "GitHub CLI (gh) is not installed." }

& $gh auth status -h github.com
if ($LASTEXITCODE -ne 0) { throw "GitHub CLI is not authenticated. Run: gh auth login" }

$repo = "$Owner/$Name"
$view = & $gh repo view $repo --json url,visibility 2>$null
if ($LASTEXITCODE -eq 0 -and $view) {
    Write-Host "Repository already exists: $repo"
} else {
    & $gh repo create $repo --public --description "Public installer releases for OGK ToolBox. Source stays private." --disable-issues --disable-wiki --homepage "https://github.com/$Owner/OGKToolBox"
    if ($LASTEXITCODE -ne 0) { throw "Failed to create $repo" }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "ogk-toolbox-releases"
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null

$readme = @"
# OGK ToolBox Releases

This public repository only hosts **compiled Windows installers** for 春菜的便当盒 (OGK ToolBox).

- Source code stays in a private repository.
- Do not open pull requests that add source, game files, tokens, or user data.
- Each Published release must include the NSIS installer, ``.blockmap``, and ``latest.yml``.
- Draft releases are invisible to ``electron-updater``.

Installed apps check this repository for updates and replace the whole app (Electron UI + Sidecar). Users do not need a GitHub token.
"@
Set-Content -LiteralPath (Join-Path $work "README.md") -Value $readme -Encoding utf8

Push-Location $work
try {
    git init -b main
    git add README.md
    git commit -m "docs: describe public installer feed"
    git remote add origin "https://github.com/$repo.git"
    git fetch origin
    $hasMain = $LASTEXITCODE -eq 0
    if ($hasMain) {
        git pull --rebase origin main
    }
    git push -u origin main
    if ($LASTEXITCODE -ne 0) { throw "Failed to push README to $repo" }
}
finally { Pop-Location }

Write-Host "Public release repository ready: https://github.com/$repo"
