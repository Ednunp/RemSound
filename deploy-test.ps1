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
# Add -SkipGate to skip the build-and-test gate (e.g. when you just ran it) - by default a binary
# deploy runs run-tests.ps1 first and refuses to deploy a build that didn't pass.

param([switch]$SkipGate)

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

# Only a RemSound process running FROM the publish folder locks its binaries. The send-only Windows
# service runs from its own copy under ProgramData (...\service\bin) and an installed app runs from
# %LocalAppData%\Programs\RemSound - neither locks publish, so neither should force a sounds-only deploy.
# (This is the bug that silently skipped binary deploys while the service was running - 2026-07-17.)
$publishFull = (Resolve-Path -LiteralPath $publish -ErrorAction SilentlyContinue).Path
$locking = @(Get-Process RemSound -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $publishFull -and $_.Path.StartsWith($publishFull, [System.StringComparison]::OrdinalIgnoreCase) }
    catch { $false }   # .Path throws for the SYSTEM service (access denied) - it's not in publish, so ignore it
})
$soundOnly = $locking.Count -gt 0
if ($soundOnly) {
    Write-Host "A RemSound is running FROM the publish folder - refreshing SOUNDS only (its binaries are locked; close that copy to update them)." -ForegroundColor Yellow
}
else {
    # Never deploy a build to test that hasn't passed the tests. The gate publishes + tests its own
    # throwaway copy, so it doesn't touch the publish\ folder below. -SkipGate overrides (e.g. when the
    # gate was just run in this session). Sound-only refreshes above skip it - they change no code.
    if (-not $SkipGate) {
        $gate = Join-Path $repo 'run-tests.ps1'
        if (Test-Path -LiteralPath $gate) {
            Write-Host "Running the build-and-test gate before deploying..." -ForegroundColor Cyan
            & $gate
            if ($LASTEXITCODE -ne 0) { throw "build-and-test gate FAILED - not deploying. Fix the failures above, or re-run with -SkipGate to override." }
        }
        else { Write-Host "WARNING: run-tests.ps1 not found - deploying WITHOUT the test gate." -ForegroundColor Yellow }
    }

    Write-Host "Publishing (Release) -> $publish ..." -ForegroundColor Cyan
    & dotnet publish $proj -c Release -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

foreach ($loc in $runLocations) {
    # Binaries: publish\ already has them from the publish above; copy them to the other location(s).
    if (-not $soundOnly -and $loc -ne $publish) {
        Write-Host "Deploying program + binaries -> $loc (preserving user settings/logs) ..." -ForegroundColor Cyan
        # /E copies the program + binaries; NO /MIR so we never DELETE the user's data. The /XD
        # excludes make the copy incapable of OVERWRITING it either: if publish\ ever accumulated a
        # user-data folder (a stray run from there), it must never land on Ed's real Dropbox profiles/
        # config/logs. The binary sync only ever carries program files, never user state, both ways.
        Invoke-Robocopy @($publish, $loc, '/E',
            '/XD', 'user settings and logs', 'recordings', 'logs', 'profiles', 'config',
            '/XF', 'global config.json', 'remsound.config.json')
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
