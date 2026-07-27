# build-release.ps1 — produce the RemSound client release zip, and PROVE it carries
# no personal data before it can ship.
#
# Why this script exists
# ----------------------
# v1.5 and v1.6 were released with the developer's logs/, profiles/ and recordings/
# folders inside the zip — because those releases were hand-zipped from a publish/
# folder the app had been run from, which had accumulated that runtime data, instead
# of using this script. This version removes the human step that went wrong:
#   * It ALWAYS publishes into a fresh, empty staging folder — never a reused dir.
#   * It then SCANS the staged files AND the finished zip, and ABORTS (deletes the
#     zip, exits non-zero) if anything that could carry personal data is present.
# Never hand-zip publish/ again. Run this. If it aborts, the release does not ship.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build-release.ps1 -Tag v1.7
#
# The -Tag value must match the GitHub release tag. The zip is named RemSound-<Tag>.zip
# because the in-app updater downloads exactly that asset name (AssetNameTemplate in
# RemSoundUpdater.cs: "RemSound-{tag}.zip").

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    # Accepts two-part (v3.0) or three-part (v3.0.1 hot-fix) tags.
    [ValidatePattern('^v[0-9]+\.[0-9]+(\.[0-9]+)?$')]
    [string]$Tag
)

$ErrorActionPreference = 'Stop'

$repo    = $PSScriptRoot
$proj    = Join-Path $repo 'src\RemSound.App\RemSound.App.csproj'
$distDir = Join-Path $repo 'dist'
$zipPath = Join-Path $distDir "RemSound-$Tag.zip"
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("remsound-release-" + [guid]::NewGuid().ToString('N'))

# 0z. The -Tag MUST match the csproj <Version>. The in-app updater downloads exactly
#     "RemSound-<tag>.zip" and compares the running version to the tag; a mismatch (e.g. tag v5.3 but
#     the binary is still 5.2 because the version bump was forgotten) ships a zip whose contents don't
#     match its name, which the updater sees as a perpetual "update available". Catch it before any work.
$csprojText    = Get-Content -LiteralPath $proj -Raw
$csprojVersion = if ($csprojText -match '<Version>([^<]+)</Version>') { $Matches[1].Trim() } else { '' }
$tagVersion    = $Tag.TrimStart('v')
if ($csprojVersion -ne $tagVersion) {
    Write-Host "RELEASE ABORTED - tag $Tag ($tagVersion) does not match the csproj <Version> ($csprojVersion)." -ForegroundColor Red
    Write-Host "Bump <Version> in RemSound.App.csproj to $tagVersion (and add the About-box changelog entry), or fix the tag, then re-run." -ForegroundColor Red
    exit 1
}
Write-Host "Version check: tag $Tag matches csproj <Version> $csprojVersion." -ForegroundColor DarkGray

# 0y. RELEASE_NOTES.md must exist AND be written for THIS tag - the final `gh release create` ships it
#     verbatim, so a copy left over from the previous release would publish the wrong notes. The notes
#     lead with "# RemSound <tag>", so requiring the tag string to appear catches a forgotten update.
$notesFile = Join-Path $repo 'RELEASE_NOTES.md'
if (-not (Test-Path -LiteralPath $notesFile)) {
    Write-Host "RELEASE ABORTED - RELEASE_NOTES.md not found. Write the notes for $Tag and re-run." -ForegroundColor Red
    exit 1
}
$notesText = Get-Content -LiteralPath $notesFile -Raw
if ($notesText -notmatch [regex]::Escape($Tag)) {
    Write-Host "RELEASE ABORTED - RELEASE_NOTES.md does not mention $Tag - it looks stale from a previous release." -ForegroundColor Red
    Write-Host "Update RELEASE_NOTES.md for $Tag (it should lead with '# RemSound $Tag') and re-run." -ForegroundColor Red
    exit 1
}
Write-Host "Release notes check: RELEASE_NOTES.md is written for $Tag." -ForegroundColor DarkGray

# 0a. SoundForge drops a .sfk peak file next to every .wav it opens. They're byproducts that must
#     never ship (the build only bundles *.wav, so they wouldn't anyway) - clear them from the
#     source 'default sounds\' folder so they don't accumulate and clutter the working tree.
$sfk = @(Get-ChildItem -LiteralPath (Join-Path $repo 'default sounds') -Filter '*.sfk' -ErrorAction SilentlyContinue)
if ($sfk.Count -gt 0) {
    Write-Host "Removing $($sfk.Count) SoundForge .sfk byproduct(s) from 'default sounds\'..." -ForegroundColor Cyan
    $sfk | Remove-Item -Force
}

# 0. Keep the GitHub-facing MANUAL.md in sync with the bundled readme.html.
#    Two copies of the manual exist on purpose: readme.html ships inside RemSound (F1
#    inside the app opens it), MANUAL.md is the Markdown rendition rendered on the
#    repo's main page. The Python sync-manual.py script regenerates MANUAL.md from
#    readme.html every time we package a release, so the GitHub page can never go out
#    of sync with the bundled help. After the regeneration we check whether MANUAL.md
#    differs from what git has committed — if so, the release is paused so the user
#    can commit the updated MANUAL.md alongside the release commit.
$syncScript = Join-Path $repo 'sync-manual.py'
if (Test-Path $syncScript) {
    Write-Host "Syncing MANUAL.md from readme.html..." -ForegroundColor Cyan
    # Look for a real Python interpreter — must actually RUN, not just exist. Windows ships
    # "Microsoft Store" execution aliases for 'py' and 'python' that fail with exit 9009 and
    # a "go install from the Store" message instead of running anything, so a plain
    # Get-Command check isn't enough. Test each candidate with 'python --version' first and
    # only treat a 0-exit result as a real install. Falls back to a couple of known user-
    # local install paths if no PATH-resolved candidate works.
    # Find a real Python interpreter. Tricky on Windows because:
    #   - The 'py' launcher passes --version queries but can refuse to run scripts when its
    #     registry-based interpreter lookup misses (seen on this dev box: 'py --version'
    #     prints 3.11.9 but 'py sync-manual.py' falls through to the Microsoft Store stub).
    #   - The 'python' command is by default an execution alias to the Microsoft Store install
    #     prompt — it accepts the call, exits with 9009, and prints "go install from the Store".
    # So we run an actual one-line script via -c with each candidate and accept the candidate
    # only if the script ran (output matches our expected sentinel). User-local Python install
    # paths come first because they're the most reliable way to skip past the Store alias.
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python311\python.exe'),
        'python3.11', 'python3.12', 'python3.13',
        'py', 'python3', 'python'
    )
    $pythonCmd = $null
    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        try {
            $sentinel = & $candidate -c "print('PYOK')" 2>&1
            if ($LASTEXITCODE -eq 0 -and ($sentinel -join '') -match 'PYOK') {
                $pythonCmd = $candidate
                break
            }
        } catch { continue }
    }
    if (-not $pythonCmd) { throw "Python not found - cannot sync MANUAL.md. Install Python 3.x and re-run." }
    Write-Host "Using Python: $pythonCmd" -ForegroundColor DarkGray
    & $pythonCmd $syncScript
    if ($LASTEXITCODE -ne 0) { throw "sync-manual.py failed (exit $LASTEXITCODE)" }

    # Refuse to ship a release when MANUAL.md is uncommitted relative to readme.html.
    # 'git diff --quiet -- MANUAL.md' exits 0 if no change, 1 if there is one.
    & git -C $repo diff --quiet -- MANUAL.md
    if ($LASTEXITCODE -eq 1) {
        Write-Host ""
        Write-Host "RELEASE PAUSED - MANUAL.md was regenerated and now differs from the committed copy." -ForegroundColor Yellow
        Write-Host "Commit the updated MANUAL.md alongside this release before re-running build-release.ps1:" -ForegroundColor Yellow
        Write-Host "    git add MANUAL.md" -ForegroundColor Yellow
        Write-Host "    git commit -m 'Refresh MANUAL.md from readme.html'" -ForegroundColor Yellow
        Write-Host "Then re-run: powershell -ExecutionPolicy Bypass -File build-release.ps1 -Tag $Tag" -ForegroundColor Yellow
        exit 1
    }
} else {
    Write-Host "Note: sync-manual.py not found - skipping MANUAL.md sync." -ForegroundColor DarkGray
}

# Gate: build RemSound and run the full test suite (run-tests.ps1) before packaging anything. A
# failing gate means no release - these are the same checks that would have caught the v3.9
# missing-cue-sounds bug. run-tests publishes and tests its OWN throwaway copy, so it never runs
# the app against (and never pollutes) the clean staging folder built below.
$gate = Join-Path $repo 'run-tests.ps1'
if (Test-Path $gate) {
    Write-Host ""
    Write-Host "Running the build-and-test gate (run-tests.ps1)..." -ForegroundColor Cyan
    & $gate
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "RELEASE ABORTED - the build-and-test gate failed. Fix the failures above and re-run." -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "WARNING: run-tests.ps1 not found - packaging WITHOUT the test gate." -ForegroundColor Yellow
}

# Anything matching these must NEVER appear in a release. Folders by name; files by
# extension / exact name. RemSound.deps.json and RemSound.runtimeconfig.json are
# legitimate app files and are deliberately NOT matched (different names).
# 'user settings and logs' added 2026-06-10: that one folder now holds global config, profiles,
# logs AND cue sounds, so forbidding it catches all the user state in one rule. The legacy 'config',
# 'profiles' and 'remsound.config.json' rules stay for any pre-migration leftovers.
$forbiddenFolders = @('logs', 'profiles', 'recordings', 'config', 'user settings and logs')
function Test-Forbidden([string]$path) {
    $p = $path -replace '\\', '/'
    foreach ($f in $forbiddenFolders) {
        if ($p -match "(^|/)$f/") { return $true }
    }
    if ($p -match '\.log$') { return $true }
    if ($p -match '(^|/)remsound\.config\.json$') { return $true }
    if ($p -match '(^|/)global config\.json$') { return $true }
    return $false
}

# 1. Fresh, empty staging folder — the whole point. The app has never run here, so
#    there is nothing to leak.
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Write-Host "Publishing $Tag to clean staging: $staging" -ForegroundColor Cyan
& dotnet publish $proj -c Release -o $staging | Out-Null
if ($LASTEXITCODE -ne 0) { Remove-Item $staging -Recurse -Force; throw "dotnet publish failed (exit $LASTEXITCODE)" }

# 2. Debug symbols are not personal data, but they don't belong in a release either.
Get-ChildItem -Path $staging -Filter *.pdb -Recurse | Remove-Item -Force

# 3. SAFETY CHECK on the staged files.
$bad = @()
Get-ChildItem -Path $staging -Recurse -Force | ForEach-Object {
    $rel = $_.FullName.Substring($staging.Length).TrimStart('\', '/')
    if (Test-Forbidden $rel) { $bad += $rel }
}
if ($bad.Count -gt 0) {
    Write-Host ""
    Write-Host "RELEASE ABORTED - staged folder contains files that must not ship:" -ForegroundColor Red
    $bad | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Remove-Item $staging -Recurse -Force
    exit 1
}

# 4. Zip it. Keep dist/ to a single artefact — drop any prior versioned zip.
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Get-ChildItem -Path $distDir -Filter 'RemSound-v*.zip' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -Path $distDir -Filter 'RemSound-v*.zip.sig' -ErrorAction SilentlyContinue | Remove-Item -Force
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force

# 5. SAFETY CHECK again, on the finished zip itself — belt and braces.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $leaked = @($zip.Entries | Where-Object { Test-Forbidden $_.FullName })
    $entryCount = $zip.Entries.Count
} finally {
    $zip.Dispose()
}
if ($leaked.Count -gt 0) {
    Write-Host ""
    Write-Host "RELEASE ABORTED - finished zip contains forbidden entries:" -ForegroundColor Red
    $leaked | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
    Remove-Item $zipPath -Force
    Remove-Item $staging -Recurse -Force
    exit 1
}

Remove-Item $staging -Recurse -Force

# 6. SIGN the zip (2026-07-27). The updater REFUSES any release without a valid signature, so an
#    unsigned zip would be rejected by every 5.6+ install - failing the pipeline here is the kind
#    failure. --sign-update signs with the private key (outside the repo) and self-checks against
#    the public key embedded in this very build, so a key/embed mismatch also stops the release.
$sigPath = "$zipPath.sig"
& (Join-Path $repo 'publish\RemSound.exe') --sign-update $zipPath | Write-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $sigPath)) {
    Write-Host "RELEASE ABORTED - could not sign the zip (see message above). Nothing published." -ForegroundColor Red
    exit 1
}
Write-Host "Signed: $sigPath" -ForegroundColor Green

$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host ""
Write-Host "OK - clean release zip verified: $zipPath ($size MB, $entryCount entries)" -ForegroundColor Green
Write-Host "     No logs / profiles / recordings / config present." -ForegroundColor Green
Write-Host ""
Write-Host "Next (the .sig asset MUST ship with the zip - updaters refuse a release without it):" -ForegroundColor Cyan
Write-Host "  gh release create $Tag `"$zipPath`" `"$sigPath`" --title `"RemSound $Tag`" --notes-file RELEASE_NOTES.md"
