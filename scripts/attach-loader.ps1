<#
Attach the universal loader package zip to the loader repository's GitHub release that Jay downloads it from.
Jay only accepts the zip pinned in build/pins.json (id jay-loader: asset name, size, SHA-256), so this script
first checks the zip against that pin and refuses anything else. See docs/universal-loader-release.md.

Usage:
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\attach-loader.ps1 -Zip <path\to\amber-ul-<version>.zip> -Check
      only check the zip against the pin and its licence notices; nothing is uploaded
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\attach-loader.ps1 -Zip <path\to\amber-ul-<version>.zip>
      check, then upload with gh (creates the pre-release for the pinned tag when it does not exist yet); asks first

Needs the GitHub CLI (gh) signed in with write access to the repository for the upload step.
#>
param(
    [Parameter(Mandatory = $true)][string]$Zip,
    [switch]$Check,
    [switch]$Replace
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$pinFile = Join-Path $root "build\pins.json"
$entry = (Get-Content -Raw -Encoding UTF8 $pinFile | ConvertFrom-Json).pins | Where-Object { $_.id -eq "jay-loader" } | Select-Object -First 1
if (-not $entry) { throw "No jay-loader pin in $pinFile" }
$r = [regex]::Match([string]$entry.urls[0], '^https://github\.com/(?<repo>[^/]+/[^/]+)/releases/download/(?<tag>[^/]+)/(?<asset>[^/]+)$')
if (-not $r.Success) { throw "The jay-loader URL in $pinFile is not a GitHub release asset" }
$Repo = $r.Groups["repo"].Value
if ([string]$entry.sha256 -notmatch '^[0-9a-f]{64}$' -or -not $entry.tag -or $r.Groups["tag"].Value -ne $entry.tag -or $r.Groups["asset"].Value -ne $entry.file) {
    Write-Host "The jay-loader pin in $pinFile is incomplete or its URL does not match its tag and file. Fill in the loader version, tag, file, URL, SHA-256 and size first." -ForegroundColor Red
    exit 1
}
$pin = [pscustomobject]@{
    Version = [string]$entry.version
    Tag     = [string]$entry.tag
    Asset   = [string]$entry.file
    Sha256  = [string]$entry.sha256
    Size    = [long]$entry.size
}
Write-Host "Pinned: $Repo, loader $($pin.Version), release tag $($pin.Tag), asset $($pin.Asset), $($pin.Size) bytes, sha256 $($pin.Sha256)"

$zipPath = (Resolve-Path $Zip).Path
$file = Get-Item $zipPath
$problems = @()
if ($file.Name -ne $pin.Asset) { $problems += "file name is $($file.Name), the pin expects $($pin.Asset)" }
if ($file.Length -ne $pin.Size) { $problems += "size is $($file.Length) bytes, the pin expects $($pin.Size)" }
$sha = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
if ($sha -ne $pin.Sha256) { $problems += "sha256 is $sha, the pin expects $($pin.Sha256)" }
if ($problems.Count -gt 0) {
    Write-Host "This zip is not the one Jay is pinned to:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "Either attach the pinned zip, or update the jay-loader pin in build/pins.json (and ship a Jay with it) first."
    exit 1
}

# What is inside: the manifest's version must match, and every third-party jar needs its licence notice in the zip.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = $archive.Entries | ForEach-Object { $_.FullName }
    $manifestEntry = $archive.Entries | Where-Object { $_.FullName -match '^[^/]+/manifest\.json$' } | Select-Object -First 1
    if (-not $manifestEntry) { Write-Host "No manifest.json in the zip's top folder" -ForegroundColor Red; exit 1 }
    $reader = New-Object System.IO.StreamReader($manifestEntry.Open())
    $manifest = $reader.ReadToEnd() | ConvertFrom-Json
    $reader.Dispose()
    if ($manifest.loaderVersion -ne $pin.Version) {
        Write-Host "manifest.json says loader $($manifest.loaderVersion), the pin says $($pin.Version)" -ForegroundColor Red
        exit 1
    }
    $notices = $names | Where-Object { $_ -match '(?i)(^|/)(licen[cs]es?|notice|third[-_]?party)[^/]*$' }
    if (-not $notices) {
        Write-Host "Warning: the zip has no top-level licence / third-party notice files. Do not publish it until the loader package carries them (docs/universal-loader-release.md)." -ForegroundColor Yellow
        if (-not $Check) { exit 1 }
    } else {
        Write-Host "Licence notices: $($notices -join ', ')"
    }
} finally {
    $archive.Dispose()
}

Write-Host "OK: $($file.Name) matches the pin ($($pin.Size) bytes, sha256 $sha)." -ForegroundColor Green
if ($Check) { exit 0 }

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI (gh) not found" }
$exists = $true
gh release view $pin.Tag --repo $Repo *> $null
if ($LASTEXITCODE -ne 0) { $exists = $false }
$notes = "Universal loader package $($pin.Version) for Jay's experimental bake.`n`nAsset: $($pin.Asset)`nSize: $($pin.Size) bytes`nSHA-256: $($pin.Sha256)`n`nJay downloads this file itself (Settings > Experimental > Download loader) and only accepts it with this SHA-256."
if ($exists) {
    Write-Host "Will upload $($file.Name) to the existing release $($pin.Tag) in $Repo$(if ($Replace) { ' (replacing the asset)' })."
} else {
    Write-Host "Will create the pre-release $($pin.Tag) in $Repo and attach $($file.Name)."
}
$answer = Read-Host "Type yes to continue"
if ($answer -ne "yes") { Write-Host "Nothing uploaded."; exit 1 }
if ($exists) {
    $uploadArgs = @("release", "upload", $pin.Tag, $zipPath, "--repo", $Repo)
    if ($Replace) { $uploadArgs += "--clobber" }
    gh @uploadArgs
} else {
    gh release create $pin.Tag $zipPath --repo $Repo --prerelease --title "Universal loader $($pin.Version)" --notes $notes
}
if ($LASTEXITCODE -ne 0) { throw "gh failed ($LASTEXITCODE)" }
Write-Host "Done: https://github.com/$Repo/releases/download/$($pin.Tag)/$($pin.Asset)"
