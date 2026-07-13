# make-tester-zip.ps1 - build a clean, portable RemSound copy to hand to external testers.
#
# What it produces
# ----------------
# A zip that MIRRORS the publish folder (every current code fix, the program files, pdb symbols,
# all runtimes, default sounds, readme, Tolk, install scripts) but with NONE of your personal state -
# no profiles, config, logs or recordings. A tester unzips it, runs RemSound.exe, and it builds its own
# fresh config on first launch.
#
# How it stays clean
# ------------------
#   * It ALWAYS publishes into a fresh, empty folder the app has never run in, so there is nothing
#     personal to leak (same principle as build-release.ps1).
#   * It then SCANS the staged files AND the finished zip and ABORTS (deletes the zip, exits non-zero)
#     if anything that could carry personal data is present.
# Unlike build-release.ps1 this is NOT a public release: no tag, no version/notes checks, no GitHub -
# it's a private portable copy for a couple of testers. Nothing is stripped, so it matches exactly what
# you test with locally.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File make-tester-zip.ps1                 # -> D:\Dropbox\remsound.zip, runs the test gate first
#   powershell -ExecutionPolicy Bypass -File make-tester-zip.ps1 -Output C:\path\to\build.zip
#   powershell -ExecutionPolicy Bypass -File make-tester-zip.ps1 -SkipGate       # skip the build-and-test gate (faster; use when you just ran it)

param(
    [string]$Output = 'D:\Dropbox\remsound.zip',
    [switch]$SkipGate
)

$ErrorActionPreference = 'Stop'
$repo    = $PSScriptRoot
$proj    = Join-Path $repo 'src\RemSound.App\RemSound.App.csproj'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("remsound-testers-" + [guid]::NewGuid().ToString('N'))

# 0. Don't hand testers a broken build - run the build-and-test gate first (unless told to skip). The
#    gate publishes + tests its OWN throwaway copy, so it doesn't touch the clean staging folder below.
$gate = Join-Path $repo 'run-tests.ps1'
if (-not $SkipGate -and (Test-Path $gate)) {
    Write-Host "Running the build-and-test gate first..." -ForegroundColor Cyan
    & $gate
    if ($LASTEXITCODE -ne 0) { throw "build-and-test gate FAILED - not making a tester zip. Fix the failures, or re-run with -SkipGate to override." }
}

# 1. Fresh Release publish = exactly what deploy-test puts in the publish folder (program files, pdb,
#    all runtimes, default sounds, readme, Tolk, install scripts). Fresh = never run = no user state.
Write-Host "Publishing Release to a fresh folder: $staging" -ForegroundColor Cyan
& dotnet publish $proj -c Release -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# 2. SAFETY SCAN - the same rule build-release.ps1 uses. Nothing that could carry personal data may ship.
$forbiddenFolders = @('logs','profiles','recordings','config','user settings and logs')
function Test-Forbidden([string]$path) {
    $p = $path.Replace('\','/')
    foreach ($f in $forbiddenFolders) { if ($p -match "(^|/)$f/") { return $true } }
    if ($p -match '\.log$') { return $true }
    if ($p -match '(^|/)(remsound\.config\.json|global config\.json)$') { return $true }
    return $false
}
$bad = @()
Get-ChildItem -Path $staging -Recurse -Force | ForEach-Object {
    $rel = $_.FullName.Substring($staging.Length).TrimStart('\','/')
    if (Test-Forbidden $rel) { $bad += $rel }
}
if ($bad.Count -gt 0) {
    Remove-Item -LiteralPath $staging -Recurse -Force
    throw "ABORT - staged copy contains files that must not ship: $($bad -join ', ')"
}
Write-Host "Scan clean: no profiles / config / logs / recordings present." -ForegroundColor Green

# 3. Zip it.
$outDir = Split-Path -Parent $Output
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $Output -CompressionLevel Optimal -Force

# 4. SAFETY SCAN again, on the finished zip - belt and braces.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($Output)
try {
    $leaked = @($zip.Entries | Where-Object { Test-Forbidden $_.FullName })
    $entryCount = $zip.Entries.Count
} finally { $zip.Dispose() }
Remove-Item -LiteralPath $staging -Recurse -Force
if ($leaked.Count -gt 0) {
    Remove-Item -LiteralPath $Output -Force
    throw "ABORT - finished zip contains forbidden entries: $($leaked.FullName -join ', ')"
}

$mb = [math]::Round((Get-Item -LiteralPath $Output).Length / 1MB, 2)
Write-Host ""
Write-Host "OK - tester zip ready: $Output ($mb MB, $entryCount entries)." -ForegroundColor Green
Write-Host "     Mirrors the publish folder; verified no profiles / config / logs / recordings." -ForegroundColor Green
