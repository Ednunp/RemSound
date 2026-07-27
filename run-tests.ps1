# run-tests.ps1 - the RemSound build-and-test gate.
#
# One command that BUILDS RemSound, then runs every check that can be made from outside a single
# running copy, and returns 0 only if they all pass. Pairs with the in-app self-test (RemSound.exe
# --selftest), which it invokes: the app's --selftest covers the audio path, encryption, wire format,
# settings, profiles and bundled files from the inside; this script covers the build, the CLI
# surface, the published package layout, the About-box changelog, and the client<->server wire
# contract from the outside.
#
# build-release.ps1 calls this first and refuses to package a release if it fails, so "build and
# test" is one step before every publish. Run it by hand any time: powershell -File run-tests.ps1
#
# Modelled on Andre's Sensor Readout (Build.ps1 + an in-app self-test), the pattern that inspired
# RemSound's command line in the first place.

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$proj = Join-Path $repo 'src\RemSound.App\RemSound.App.csproj'

$script:failures = @()
function Fail($m) { $script:failures += $m; Write-Host "  [FAIL] $m" -ForegroundColor Red }
function Pass($m) { Write-Host "  [PASS] $m" -ForegroundColor Green }

# ---- expected version, read from the one source of truth (the csproj) ----
$csprojText = Get-Content -LiteralPath $proj -Raw
$expectedVersion = if ($csprojText -match '<Version>([^<]+)</Version>') { $Matches[1].Trim() } else { '' }
$expectedMM = ($expectedVersion -split '\.')[0..1] -join '.'   # major.minor, e.g. 3.9

# ---- 1. BUILD: publish to a throwaway folder (the app is never run here yet, so the bundled
#         sounds\ folder stays intact for the package checks below) ----
$publishDir = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-runtests-" + [guid]::NewGuid().ToString('N'))
Write-Host "Building (publish to $publishDir) ..." -ForegroundColor Cyan
& dotnet publish $proj -c Release -o $publishDir --nologo | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "RESULT: FAIL - build/publish failed (warnings are errors)." -ForegroundColor Red
    exit 1
}
$exe = Join-Path $publishDir 'RemSound.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "RESULT: FAIL - no RemSound.exe was produced." -ForegroundColor Red
    exit 1
}

# ---- 2. PACKAGE CONTENTS ----
#  The shipped DEFAULT cues live install-side in 'default sounds\' next to the exe (the app reads
#  them from there and updates always overwrite them; they're no longer copied into the per-user
#  folder). So unlike before, a CLI launch does NOT empty this folder - the checks here are robust
#  whatever order they run in.
Write-Host "`nPackage contents:" -ForegroundColor Cyan
$soundsPath = Join-Path $publishDir 'default sounds'
$wavCount = @(Get-ChildItem -LiteralPath $soundsPath -Filter *.wav -ErrorAction SilentlyContinue).Count
if ($wavCount -ge 3) { Pass "cue sounds bundled ($wavCount .wav)" } else { Fail "cue sounds missing (found $wavCount) - this is the bug that shipped v3.9 with no sounds" }
# Cues ship as numbered variants ("connect 1.wav", ...); each required cue needs at least one.
foreach ($base in @('connect', 'disconnect', 'start up', 'send on', 'send off', 'recieve on', 'recieve off', 'minimise', 'maximise', 'check', 'uncheck', 'tab switch')) {
    $variants = @(Get-ChildItem -LiteralPath $soundsPath -Filter "$base*.wav" -ErrorAction SilentlyContinue)
    if ($variants.Count -gt 0) { Pass "'$base' cue has $($variants.Count) sound variant(s)" } else { Fail "no sound variant for the '$base' cue" }
}
# Keyboard-click + password sounds.
foreach ($extra in @('key 1.wav', 'passkey.wav')) {
    if (Test-Path -LiteralPath (Join-Path $soundsPath $extra)) { Pass "'$extra' present" } else { Fail "'$extra' missing from the published 'default sounds' folder" }
}
if (Test-Path -LiteralPath (Join-Path $publishDir 'readme.html')) { Pass "readme.html (F1 manual) bundled" } else { Fail "readme.html missing" }
if (Test-Path -LiteralPath (Join-Path $publishDir 'runtimes\win-x64\native\opus.dll')) { Pass "native opus.dll bundled" } else { Fail "native opus.dll missing (runtimes\win-x64\native\)" }
if (Test-Path -LiteralPath (Join-Path $publishDir 'coreclr.dll')) { Fail "self-contained build (coreclr.dll present) - releases must be framework-dependent" } else { Pass "framework-dependent (no coreclr.dll)" }

# built assembly version must match the csproj
try {
    $dllVer = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $publishDir 'RemSound.dll')).Version
    $dllMM = "$($dllVer.Major).$($dllVer.Minor)"
    if ($dllMM -eq $expectedMM) { Pass "built RemSound.dll version $dllVer matches csproj <Version> $expectedVersion" }
    else { Fail "RemSound.dll version $dllVer does not match csproj <Version> $expectedVersion" }
} catch { Fail "could not read RemSound.dll version: $($_.Exception.Message)" }

# ---- 3. SOURCE CHECKS (no app run needed) ----
Write-Host "`nRelease readiness:" -ForegroundColor Cyan
$about = Get-Content -LiteralPath (Join-Path $repo 'src\RemSound.App\AboutDialog.cs') -Raw
if ($about -match [regex]::Escape("RemSound v$expectedMM")) { Pass "About-box changelog has a 'RemSound v$expectedMM' entry" }
else { Fail "About-box changelog has no 'RemSound v$expectedMM' entry - add the release notes before shipping" }

# Client <-> server wire contract. The Pi relay forwards by reading the wire header only; if the
# client's header drifts from what the relay parses, the relay must be updated and re-released.
# Ideally we never touch the server - this proves we don't need to.
Write-Host "`nClient/server wire compatibility (no server change should be needed):" -ForegroundColor Cyan
$relay = Get-Content -LiteralPath (Join-Path $repo 'server\remsound-relay.py') -Raw
$packet = Get-Content -LiteralPath (Join-Path $repo 'src\RemSound.Core\RemPacket.cs') -Raw
$wireOk = $true
if ($relay -notmatch 'RMND') { Fail "relay no longer references the 'RMND' magic"; $wireOk = $false }
if ($relay -match 'V1_VERSION\s*=\s*(\d+)') { if ($Matches[1] -ne '1') { Fail "relay V1_VERSION=$($Matches[1]) but the client writes version 1"; $wireOk = $false } }
else { Fail "could not find V1_VERSION in the relay"; $wireOk = $false }
if ($packet -match 'HeaderSize\s*=\s*(\d+)') { if ($Matches[1] -ne '12') { Fail "client RemPacket.HeaderSize=$($Matches[1]) but the relay reads a 12-byte header"; $wireOk = $false } }
else { Fail "could not find RemPacket.HeaderSize"; $wireOk = $false }
if ($packet -match 'DefaultPort\s*=\s*(\d+)') { if ($Matches[1] -ne '47830') { Fail "client DefaultPort=$($Matches[1]) but the relay listens on 47830"; $wireOk = $false } }
if ($relay -notmatch '47830') { Fail "relay no longer references port 47830"; $wireOk = $false }
if ($wireOk) { Pass "relay magic / version / port still match the client header - no server change needed" }

# Relay logic unit tests (server\test_relay.py). The relay's address-proof, per-IP cap, NAT-rebind
# reset and forged-BYE rejection are pure Python guarding an internet-facing attack surface, and the
# relay auto-updates every user - a regression there would sail past the C# gate. Run them here so
# a server change can't ship un-tested. Needs a Python interpreter; if none is found we SKIP loudly
# rather than fail (the C# gate doesn't depend on Python being installed on the build box).
Write-Host "`nRelay logic tests (server\test_relay.py):" -ForegroundColor Cyan
$py = $null
foreach ($cand in @('py', 'python', 'python3')) {
    $cmd = Get-Command $cand -ErrorAction SilentlyContinue
    # Skip the Windows Store execution-alias stubs (…\Microsoft\WindowsApps\py.exe|python.exe) —
    # they don't run Python, they print "Python was not found" and exit non-zero, which would fail
    # the relay-test step instead of running it. The real launcher (C:\Windows\py.exe) and a real
    # install (…\Programs\Python\…) are never under WindowsApps.
    if ($cmd -and $cmd.Source -notmatch 'WindowsApps') { $py = $cmd.Source; break }
}
if (-not $py) {
    Write-Host "  [SKIP] no Python interpreter found (py/python/python3) - relay logic tests did not run" -ForegroundColor Yellow
    Write-Host "  WARNING: the relay's address-proof / cap / eviction logic is NOT verified on this machine." -ForegroundColor Yellow
}
else {
    # Start-Process (not the call operator) so unittest's stderr can't trip $ErrorActionPreference=Stop,
    # and so it runs FROM server\ where the test's relative import of remsound-relay.py resolves.
    $serverDir = Join-Path $repo 'server'
    $rtOut = Join-Path $env:TEMP ("rs-relay-" + [guid]::NewGuid().ToString('N') + ".txt")
    $rtErr = Join-Path $env:TEMP ("rs-relay-" + [guid]::NewGuid().ToString('N') + ".err.txt")
    $rp = Start-Process -FilePath $py -ArgumentList @('-m', 'unittest', 'test_relay') -WorkingDirectory $serverDir `
        -Wait -NoNewWindow -PassThru -RedirectStandardOutput $rtOut -RedirectStandardError $rtErr
    $rtText = ((Get-Content -LiteralPath $rtOut -Raw -ErrorAction SilentlyContinue) + "`n" + (Get-Content -LiteralPath $rtErr -Raw -ErrorAction SilentlyContinue))
    Remove-Item $rtOut, $rtErr -Force -ErrorAction SilentlyContinue
    if ($rp.ExitCode -eq 0) {
        $ran = if ($rtText -match 'Ran (\d+) test') { $Matches[1] } else { '?' }
        Pass "relay logic tests passed ($ran tests: addr-proof, enforce/watch, IP cap, rebind, forged-BYE, header gate)"
    }
    else {
        Fail "relay logic tests FAILED:`n$rtText"
    }
}

# ---- 4. CLI SURFACE + IN-APP SELF-TEST (these launch the app, which consolidates sounds away;
#         that's why the package checks ran first) ----
function Invoke-RsCli([string[]]$cliArgs) {
    $out = Join-Path $env:TEMP ("rs-cli-" + [guid]::NewGuid().ToString('N') + ".txt")
    $p = Start-Process -FilePath $exe -ArgumentList $cliArgs -Wait -NoNewWindow -RedirectStandardOutput $out -PassThru
    $text = if (Test-Path $out) { Get-Content -LiteralPath $out -Raw -Encoding UTF8 } else { '' }
    Remove-Item $out -Force -ErrorAction SilentlyContinue
    [pscustomobject]@{ Code = $p.ExitCode; Text = ([string]$text) }
}

Write-Host "`nCLI surface:" -ForegroundColor Cyan
$v = Invoke-RsCli @('--version')
if ($v.Code -eq 0 -and $v.Text.Trim() -like 'RemSound *') { Pass "--version: $($v.Text.Trim())" } else { Fail "--version returned '$($v.Text.Trim())' (exit $($v.Code))" }
if ($v.Text -match "$([regex]::Escape($expectedMM))(\D|$)") { Pass "--version matches csproj $expectedMM" } else { Fail "--version '$($v.Text.Trim())' does not match csproj <Version> $expectedVersion" }

$h = Invoke-RsCli @('--help')
$needed = @('--devices', '--selftest', '--diagnostics', '--connect', '--profile', '--minimized', '--log', '--close', '--version')
$missing = @($needed | Where-Object { $h.Text -notlike "*$_*" })
if ($h.Code -eq 0 -and $missing.Count -eq 0) { Pass "--help documents every option" } else { Fail "--help missing or errored: $($missing -join ', ') (exit $($h.Code))" }

$dev = Invoke-RsCli @('--devices')
if ($dev.Code -eq 0 -and $dev.Text.Length -gt 0) { Pass "--devices ran and produced output" } else { Fail "--devices exit $($dev.Code)" }

Write-Host "`nIn-app self-test:" -ForegroundColor Cyan
$st = Invoke-RsCli @('--selftest')
foreach ($line in ($st.Text -split "`r?`n")) {
    if ($line -match '\[(PASS|FAIL|SKIP)\]|^RESULT:') { Write-Host "  $($line.Trim())" }
}
if ($st.Code -eq 0) { Pass "self-test passed (exit 0)" } else { Fail "self-test failed (exit $($st.Code))" }
# Skipped steps are coverage that did NOT run. The gate still passes (some skips are legitimate on a
# headless/agent box), but they must be impossible to miss in the output - a release verified with
# steps skipped is only as verified as what actually ran.
$skipLine = ($st.Text -split "`r?`n") | Where-Object { $_ -match '^SKIPPED STEPS' } | Select-Object -First 1
if ($skipLine) {
    Write-Host ""
    Write-Host "  WARNING: $($skipLine.Trim())" -ForegroundColor Yellow
    Write-Host "  These steps did not run on this machine - their coverage is NOT verified." -ForegroundColor Yellow
}

Write-Host "`nResource sanity (handle/memory leak check):" -ForegroundColor Cyan
$pf = Invoke-RsCli @('--perftest', '--seconds', '12')
foreach ($line in ($pf.Text -split "`r?`n")) {
    if ($line -match 'baseline:|cycle \d|net change|^\s*RESULT:') { Write-Host "  $($line.Trim())" }
}
if ($pf.Code -eq 0) { Pass "resources stayed bounded across cycles (or skipped - no audio device)" }
else { Fail "perf sanity flagged possible runaway (exit $($pf.Code))" }

# ---- 5. COLD START + CLEAN CLOSE, against an isolated --config-dir so the real settings are never
#         touched (smoke-test brief, safety rule 1 + baseline steps 3-4) ----
Write-Host "`nCold start and clean close (isolated config):" -ForegroundColor Cyan
$already = @(Get-Process RemSound -ErrorAction SilentlyContinue)
if ($already.Count -gt 0) {
    Write-Host "  [SKIP] a RemSound instance is already running (machine-wide single-instance lock) - close it to run this check" -ForegroundColor Yellow
}
else {
    $testCfg = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-cfg-" + [guid]::NewGuid().ToString('N'))
    $proc = $null
    try {
        # --silent so this throwaway launch makes no cue sounds (startup / connect) and pops no
        # dialogs at whoever's at the screen while the gate runs. The shipped default sounds live
        # install-side ('default sounds\' next to the exe) so this cold-start finds them with no
        # consolidation step and no missing-sound warning - nothing to restore here any more.
        $proc = Start-Process -FilePath $exe -ArgumentList @('--config-dir', $testCfg, '--connect', '127.0.0.1', '--minimized', '--silent') -PassThru
        Start-Sleep -Seconds 6
        if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) { Pass "GUI cold-started and stayed up (minimized to tray)" }
        else { Fail "GUI exited or crashed during cold start" }

        # The process creates its user-data folder at the --config-dir override (MigrateLegacyLayout),
        # so the override folder existing afterwards proves it honoured --config-dir and left the real
        # settings alone. (Sounds are NOT here - they're install-side now.)
        if (Test-Path -LiteralPath $testCfg) { Pass "ran against the isolated --config-dir folder (real settings untouched)" }
        else { Fail "--config-dir folder was not created - config isolation may not be working" }

        Invoke-RsCli @('--close') | Out-Null
        Start-Sleep -Seconds 2
        $still = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if (-not $still) { Pass "--close shut the GUI down cleanly (no orphan process)" }
        else { Fail "process still running after --close"; try { $still | Stop-Process -Force } catch { } }
    }
    catch {
        Fail "cold-start/close smoke threw: $($_.Exception.Message)"
        if ($proc) { try { Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force } catch { } }
    }
    finally {
        Remove-Item -LiteralPath $testCfg -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---- summary ----
Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
if ($script:failures.Count -eq 0) {
    Write-Host "RESULT: PASS - all gate checks passed. Safe to publish." -ForegroundColor Green
    exit 0
}
Write-Host "RESULT: FAIL - $($script:failures.Count) check(s) failed:" -ForegroundColor Red
$script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
