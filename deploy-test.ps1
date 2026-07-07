# deploy-test.ps1 - Ed's test-deploy. proj\remsound\default sounds is the AUTHORITATIVE sound master.
#
# As of 2026-06-13 the shipped DEFAULT cue sounds live in a "default sounds\" folder next to the exe
# (AppConfig.SoundsDirectory) - they're part of the install, not per-user state, so an update (or a
# republish) always overwrites them and a tweaked default always lands. The user's OWN custom sounds
# are NOT here: they're explicit file paths set via the Preferences "Browse" picker, kept in the
# user's own location, which nothing here touches.
#
# Every run of THIS script force-copies the ENTIRE source "default sounds\" folder over BOTH test
# locations' copies, overwriting every file regardless of timestamp/size (/IS /IT). Ed can tweak any
# number of sounds, with any timestamps, without telling anyone which changed - the next deploy
# always takes them all. (A running app reads each WAV fresh on every play, so a sound tweak even
# takes effect live, no restart needed.) Binaries + program files go out too, skipped automatically
# if RemSound is open and has them locked.
#
# Run:  powershell -ExecutionPolicy Bypass -File deploy-test.ps1

$ErrorActionPreference = 'Stop'
$repo      = $PSScriptRoot
$proj      = Join-Path $repo 'src\RemSound.App\RemSound.App.csproj'
$srcSounds = Join-Path $repo 'default sounds'
$publish   = Join-Path $repo 'publish'

# The TWO test run-locations. Each has its own 'default sounds\' copy that must be kept current.
$runLocations = @($publish, 'D:\Dropbox\remsound')

function Invoke-Robocopy([string[]]$rcArgs) {
    & robocopy @rcArgs /NFL /NDL /NJH /NJS /NP /R:3 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $($rcArgs -join ' ')" }
}

$soundOnly = @(Get-Process RemSound -ErrorAction SilentlyContinue).Count -gt 0
if ($soundOnly) {
    Write-Host "RemSound is running - refreshing SOUNDS only (binaries are locked; close RemSound to update them)." -ForegroundColor Yellow
}
else {
    Write-Host "Publishing (Release) -> $publish ..." -ForegroundColor Cyan
    & dotnet publish $proj -c Release -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

foreach ($loc in $runLocations) {
    # Binaries: publish\ already has them from the publish above; copy them to the other location(s).
    if (-not $soundOnly -and $loc -ne $publish) {
        Write-Host "Deploying program + binaries -> $loc (preserving user settings/logs) ..." -ForegroundColor Cyan
        Invoke-Robocopy @($publish, $loc, '/E')   # no /MIR: never delete the user's settings/logs/profiles
    }
    # THE point of this script: the whole source 'default sounds' folder, force-copied over THIS
    # location's copy, every time. /IS = copy even files robocopy thinks are identical; /IT = copy
    # "tweaked" files. No /XO (don't skip on timestamp) and no /MIR (don't delete files).
    $locSounds = Join-Path $loc 'default sounds'
    Write-Host "Force-syncing 'default sounds' -> $locSounds (source wins, every time) ..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $locSounds -Force | Out-Null
    Invoke-Robocopy @($srcSounds, $locSounds, '*.wav', '/IS', '/IT')

    # One-time tidy: drop the orphaned pre-2026-06-13 program 'sounds\' folder and the defunct old
    # per-user sounds folder if they're lingering from before the move (the running app also deletes
    # the per-user one on launch). Idempotent - a no-op once gone.
    foreach ($orphan in @((Join-Path $loc 'sounds'), (Join-Path $loc 'user settings and logs\sounds'))) {
        if (Test-Path -LiteralPath $orphan) { Remove-Item -LiteralPath $orphan -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

$count = @(Get-ChildItem $srcSounds -Filter *.wav).Count
Write-Host "Done. $count default sounds force-synced to BOTH test locations. Tweak away - the next run always takes them all." -ForegroundColor Green
