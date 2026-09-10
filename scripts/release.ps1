<#
.SYNOPSIS
    Build VSaver and package a first-time-install zip (VSaver.exe + credentials.json + README.md).

.DESCRIPTION
    Produces two assets, both safe to publish publicly:

      1. The bare VSaver.exe -- the auto-updater target. Named exactly VSaver.exe, which the
         updater looks for.

      2. dist\VSaver-v<version>.zip  -- the first-time-install bundle
         (VSaver.exe + credentials.json + settings.json + README.md). The bundled
         credentials.json is the PLACEHOLDER template from credentials.json.example (no real
         secrets) and settings.json ships a blank Drive folder id -- users fill in the real
         credentials.json and folder id themselves per README.md. Nothing secret is baked in,
         so both assets attach to the GitHub Release.

    The version comes from <Version> in the .csproj; the release tag is v<version>.

.EXAMPLE
    pwsh scripts\release.ps1
        Build the exe + the install zip. No GitHub release (dry run).

.EXAMPLE
    pwsh scripts\release.ps1 -Publish -Notes "Fix credentials.json lookup"
        Build, then publish BOTH the bare VSaver.exe and the install zip to the GitHub Release.
#>
[CmdletBinding()]
param(
    # Create the GitHub Release and upload assets (requires the `gh` CLI, authenticated).
    [switch]$Publish,

    # Release notes body. Ignored unless -Publish is set.
    [string]$Notes = ""
)

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path $PSScriptRoot -Parent
$appProj    = Join-Path $repoRoot "src\ValheimSync.App\ValheimSync.App.csproj"
$publishDir = Join-Path $repoRoot "src\ValheimSync.App\bin\Release\net8.0\win-x64\publish"
$distDir    = Join-Path $repoRoot "dist"
$setupDoc   = Join-Path $repoRoot "dist\README.md"
$settingsSeed = Join-Path $repoRoot "src\ValheimSync.App\settings.json.example"

# --- Read <Version> from the app csproj ------------------------------------------------
[xml]$csproj = Get-Content $appProj
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $appProj." }
$tag = "v$version"
Write-Host "Building VSaver $tag" -ForegroundColor Cyan

# --- Publish the single-file exe -------------------------------------------------------
& dotnet publish $appProj -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $publishDir "VSaver.exe"
if (-not (Test-Path $exe)) { throw "Expected build output not found: $exe" }

# --- Locate the placeholder credentials.json to bundle ---------------------------------
# The zip ships the TEMPLATE credentials.json (the placeholder fields from
# credentials.json.example) so it carries no real secrets and is safe to publish. Users
# swap in the real credentials.json for their group per README.md.
$creds = Join-Path $repoRoot "src\ValheimSync.App\credentials.json.example"
if (-not (Test-Path $creds)) {
    throw "credentials.json.example not found at $creds - needed to seed the zip's placeholder credentials.json."
}

# --- Stage + zip -----------------------------------------------------------------------
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }
$staging = Join-Path $distDir ".stage"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

Copy-Item $exe   (Join-Path $staging "VSaver.exe")
# Ship the PLACEHOLDER credentials.json (from credentials.json.example) so users can see the
# exact shape of the file; they replace it with their group's real one per README.md.
Copy-Item $creds (Join-Path $staging "credentials.json")
# Ship a blank-folder-id settings.json. The Drive folder id is entered in the app and stored
# only in the user's local settings.json -- it is never baked into the build. The app rewrites
# this file with per-user fields (name, selected worlds) on first run.
if (Test-Path $settingsSeed) {
    Copy-Item $settingsSeed (Join-Path $staging "settings.json")
} else {
    Write-Warning "settings.json.example not found - the zip will omit a seeded settings.json."
}
if (Test-Path $setupDoc) {
    Copy-Item $setupDoc (Join-Path $staging "README.md")
} else {
    Write-Warning "dist\README.md not found - the zip will omit the setup guide."
}

$zip = Join-Path $distDir "VSaver-$tag.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip
Remove-Item $staging -Recurse -Force
Write-Host "Packaged $zip (VSaver.exe + placeholder credentials.json + settings.json + README.md)" -ForegroundColor Green

# --- Optionally cut the GitHub Release -------------------------------------------------
# Both assets are safe to publish: the bare VSaver.exe (auto-updater target) and the
# first-time-install zip (placeholder credentials.json + blank folder id, no real secrets).
if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "The GitHub CLI (gh) is not installed or not on PATH - cannot -Publish."
    }
    Write-Host "Creating GitHub Release $tag (VSaver.exe + install zip)..." -ForegroundColor Cyan
    # Build the arg list so an empty -Notes doesn't leave a dangling --notes flag; fall back
    # to GitHub's auto-generated notes when none are supplied.
    $ghArgs = @("release", "create", $tag, $exe, $zip, "--title", $tag)
    if ($Notes) { $ghArgs += @("--notes", $Notes) } else { $ghArgs += "--generate-notes" }
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
    Write-Host "Released $tag with both the bare exe and the first-time-install zip." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Dry run complete. To publish (uploads the exe + install zip), run:" -ForegroundColor Yellow
    Write-Host "  gh release create $tag `"$exe`" `"$zip`" --title `"$tag`" --notes `"...`"" -ForegroundColor Yellow
}
