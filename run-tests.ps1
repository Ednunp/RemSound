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
# Checks that did not run on this machine. Named again in the verdict, so "Safe to publish" is never said over them
# without saying what was not verified (2026-09-24: a skipped relay test and skipped self-test steps went unmentioned).
$script:skips = @()
function Skip($m) { $script:skips += $m; Write-Host "  [SKIP] $m" -ForegroundColor Yellow }

# ---- expected version, read from the one source of truth (the csproj) ----
$csprojText = Get-Content -LiteralPath $proj -Raw
$expectedVersion = if ($csprojText -match '<Version>([^<]+)</Version>') { $Matches[1].Trim() } else { '' }
$expectedMM = ($expectedVersion -split '\.')[0..1] -join '.'   # major.minor, e.g. 3.9

# ---- 1. BUILD: publish to a throwaway folder ----
# A STABLE folder, deliberately not a fresh GUID per run. Windows Firewall keys its allow rules
# on the program path, so a new random path meant a brand-new "allow RemSound to communicate on
# these networks?" prompt on EVERY gate run — the app binds UDP 47821 (discovery) and 47830 (audio)
# during --selftest, which is enough to trigger it. One machine here had accumulated 22 rules, 18
# of them pointing at temp folders this script had already deleted. A stable path prompts once,
# ever. Set REMSOUND_TESTS_DIR to override (parallel runs, CI, or a path already allow-listed).
$publishDir = if ([string]::IsNullOrWhiteSpace($env:REMSOUND_TESTS_DIR)) {
    Join-Path ([System.IO.Path]::GetTempPath()) 'rs-runtests'
} else {
    $env:REMSOUND_TESTS_DIR
}
# Stable means it can hold last run's leftovers, and a stale file would let a package check pass
# on something this build never produced. Start empty every time.
# The file every DAW's plugin reads to find RemSound's log folder. Nothing in the gate may change it: on 2026-09-24 a gate
# run left it pointing at a deleted temp folder with logging off. Read now, compared at the end.
$realPointer = Join-Path $env:LOCALAPPDATA 'RemSound\plugin-home.txt'
function Get-PointerState { if (Test-Path -LiteralPath $realPointer) { (Get-FileHash -LiteralPath $realPointer).Hash + '@' + (Get-Item -LiteralPath $realPointer).LastWriteTimeUtc.Ticks } else { 'absent' } }
$pointerBefore = Get-PointerState

# And the rest of this computer's real RemSound state (2026-09-24): the settings and profiles of the copy the pointer names,
# the machine-wide folder (the service's settings and this computer's identity), and the "start when you sign in" entry.
# Nothing in the gate may change any of it; only the pointer was watched before.
function Get-RealState {
    $lines = @()
    $realData = $null
    if (Test-Path -LiteralPath $realPointer) { $realData = (Get-Content -LiteralPath $realPointer -TotalCount 1) }
    $roots = @()
    if ($realData -and (Test-Path -LiteralPath $realData)) {
        $roots += (Join-Path $realData 'config.json')
        $roots += (Join-Path $realData 'profiles')
    }
    $roots += (Join-Path $env:ProgramData 'RemSound')
    foreach ($r in $roots) {
        if (Test-Path -LiteralPath $r -PathType Leaf) { $lines += "$r=" + (Get-FileHash -LiteralPath $r).Hash }
        elseif (Test-Path -LiteralPath $r) {
            Get-ChildItem -LiteralPath $r -File -Recurse -ErrorAction SilentlyContinue |
                # Not the logs, the service's own program copy (it updates itself) or its live status (it writes that itself).
                Where-Object { $_.Extension -in '.json', '.txt' -and $_.FullName -notmatch '\\(logs|bin)\\' -and $_.Name -ne 'status.json' } |
                ForEach-Object { $lines += "$($_.FullName)=" + (Get-FileHash -LiteralPath $_.FullName -ErrorAction SilentlyContinue).Hash }
        }
    }
    $run = (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RemSound' -ErrorAction SilentlyContinue).RemSound
    $lines += "sign-in entry=$run"
    ($lines | Sort-Object) -join "`n"
}
$realStateBefore = Get-RealState

Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
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
# The scripts beside the exe (Ed, 2026-09-24): the .NET installers, and the plugin and service actions for anyone who
# can't reach the window. The folder was "Install Scripts" until that day; the old name must not ship again.
$scriptsDir = Join-Path $publishDir 'Install and Uninstall Scripts'
$wantScripts = @('Install .NET for RemSound.cmd', 'Install .NET for RemSound.ps1', 'Install DAW plugin.cmd', 'Uninstall DAW plugin.cmd',
                 'Install service.cmd', 'Uninstall service.cmd', 'Start service.cmd', 'Stop service.cmd')
$missingScripts = @($wantScripts | Where-Object { -not (Test-Path -LiteralPath (Join-Path $scriptsDir $_)) })
if ($missingScripts.Count -eq 0) { Pass "all $($wantScripts.Count) install and uninstall scripts bundled" } else { Fail "missing from 'Install and Uninstall Scripts': $($missingScripts -join ', ')" }
if (Test-Path -LiteralPath (Join-Path $publishDir 'Install Scripts')) { Fail "the old 'Install Scripts' folder was published" } else { Pass "no old 'Install Scripts' folder in the build" }
if (Test-Path -LiteralPath (Join-Path $publishDir 'runtimes\win-x64\native\opus.dll')) { Pass "native opus.dll bundled" } else { Fail "native opus.dll missing (runtimes\win-x64\native\)" }
# The VST plugin ships in a plugin\ folder next to the exe. RemSound.App.csproj copies it in after
# publish with ContinueOnError, so a failed copy would not stop the build. The DAW plugin menu's
# Install plugin copies from that folder, so an empty or missing one leaves it nothing to install.
$pluginFiles = @(Get-ChildItem -LiteralPath (Join-Path $publishDir 'plugin') -File -Recurse -ErrorAction SilentlyContinue)
if ($pluginFiles.Count -gt 0) { Pass "VST plugin bundled (plugin\ holds $($pluginFiles.Count) files)" } else { Fail "plugin\ folder missing or empty - the DAW plugin menu's Install plugin would have nothing to install" }
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
else { Fail "could not find RemPacket.DefaultPort"; $wireOk = $false }   # silently passed if the pattern stopped matching, until 2026-09-24
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
    Skip "the relay logic tests (no Python interpreter found: py, python or python3)"
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
        Pass "relay logic tests passed ($ran tests: addr-proof, enforce/watch, IP cap, rebind, forged-BYE, header gate, stats log)"
    }
    else {
        Fail "relay logic tests FAILED:`n$rtText"
    }
}

# ---- 4. CLI SURFACE + IN-APP SELF-TEST (these launch the app) ----
# The gate is run from a throwaway PUBLISH folder, so it cannot find the source tree by looking
# around itself. One check needs it: the event-coverage guard reads every "+=" handler wiring out of
# the source and insists each one is named in a spec. Handing it the repo path here is what keeps
# that guard running in the real gate instead of quietly skipping.
$env:REMSOUND_SOURCE_ROOT = $repo

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

# The one service command that only reads: how the service is, as the Service menu's status line says it.
$ss = Invoke-RsCli @('--service', 'status')
if ($ss.Code -eq 0 -and $ss.Text.Trim() -match '^The RemSound service is ') { Pass "--service status: $($ss.Text.Trim())" }
else { Fail "--service status returned '$($ss.Text.Trim())' (exit $($ss.Code))" }

$h = Invoke-RsCli @('--help')
$needed = @('--devices', '--selftest', '--diagnostics', '--connect', '--profile', '--minimized', '--log', '--close', '--version', '--headless', '--control', '--install-plugin', '--uninstall-plugin', '--service')
$missing = @($needed | Where-Object { $h.Text -notlike "*$_*" })
if ($h.Code -eq 0 -and $missing.Count -eq 0) { Pass "--help documents every option" } else { Fail "--help missing or errored: $($missing -join ', ') (exit $($h.Code))" }

$dev = Invoke-RsCli @('--devices')
# All three sections, not merely some output: an error message is output too (2026-09-24).
if ($dev.Code -eq 0 -and $dev.Text -match 'Microphones / line-in' -and $dev.Text -match 'Speakers / headphones' -and $dev.Text -match 'ASIO drivers:') {
    Pass "--devices lists the inputs, the outputs and the ASIO drivers"
}
else { Fail "--devices did not list all three kinds of device (exit $($dev.Code)): $($dev.Text.Substring(0, [Math]::Min(200, $dev.Text.Length)))" }

Write-Host "`nIn-app self-test:" -ForegroundColor Cyan
# --silent: the gate must be SILENT. The self-test drives controls that play cue sounds, and
# without this every gate run chimed at whoever was at the screen (Ed, 2026-08-15).
# The release-signing steps compare the publisher's private key with the public key built into RemSound. RemSound no
# longer knows where that key lives (2026-09-13), so hand it over when it is on this machine.
$defaultSigningKey = 'D:\Dropbox\proj\rsound key\remsound-signing-key.pem'
if (-not $env:REMSOUND_SIGNING_KEY -and (Test-Path -LiteralPath $defaultSigningKey)) { $env:REMSOUND_SIGNING_KEY = $defaultSigningKey }
$st = Invoke-RsCli @('--selftest', '--silent')
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
    $skippedSteps = @(($st.Text -split "`r?`n") | Where-Object { $_ -match '\[SKIP\]' }).Count
    $script:skips += "$skippedSteps self-test step(s)"
}

Write-Host "`nResource sanity (handle/memory leak check):" -ForegroundColor Cyan
$pf = Invoke-RsCli @('--perftest', '--seconds', '12')
foreach ($line in ($pf.Text -split "`r?`n")) {
    if ($line -match 'baseline:|cycle \d|net change|^\s*RESULT:') { Write-Host "  $($line.Trim())" }
}
# A skip exits 0 too, and was reported as a pass (2026-09-24).
if ($pf.Code -eq 0 -and $pf.Text -match 'RESULT: SKIP') { Skip "the resource check (it found no audio device to cycle)" }
elseif ($pf.Code -eq 0) { Pass "resources stayed bounded across cycles" }
else { Fail "perf sanity flagged possible runaway (exit $($pf.Code))" }

# ---- 5. COLD START + CLEAN CLOSE, against an isolated --config-dir so the real settings are never
#         touched (smoke-test brief, safety rule 1 + baseline steps 3-4) ----
Write-Host "`nCold start and clean close (isolated config):" -ForegroundColor Cyan
# Session 0 is the lock-screen service, which runs RemSound.exe but never holds the app's one-copy lock, so it
# does not stop this check. Counting it meant the check skipped on every machine with the service running.
$already = @(Get-Process RemSound -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 })
$script:coldStartSkipped = $false
if ($already.Count -gt 0) {
    # Remembered for the verdict. This is the ONE check that launches the real program, so a run without it cannot
    # call a build safe to publish: on 2026-09-23 a main window broken on purpose got past every other part of the
    # gate with RemSound open.
    $script:coldStartSkipped = $true
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

        # The process creates its user-data folder at the --config-dir override (the profile store
        # makes its folder there on start), so the override folder existing afterwards proves it
        # honoured --config-dir and left the real settings alone. (Cue sounds are never in that folder:
        # the shipped defaults live in 'default sounds\' next to the exe.)
        if (Test-Path -LiteralPath $testCfg) { Pass "ran against the isolated --config-dir folder (real settings untouched)" }
        else { Fail "--config-dir folder was not created - config isolation may not be working" }

        # Only a --headless copy listens for --control. A normal start must refuse it, and say why.
        $nc = Invoke-RsCli @('--control', 'windows')
        if ($nc.Code -eq 2 -and $nc.Text -match "wasn't started with --headless") { Pass "a normal start opens no control channel (--control is refused, and says why)" }
        else { Fail "a normal start answered --control, or said something else (exit $($nc.Code)): $($nc.Text.Trim())" }

        Invoke-RsCli @('--close') | Out-Null
        Start-Sleep -Seconds 2
        $still = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if (-not $still) { Pass "--close shut the GUI down cleanly (no orphan process)" }
        else { Fail "process still running after --close"; try { $still | Stop-Process -Force } catch { } }

        # "Stayed up" is not "did not crash": a crash that shows the Continue or Quit box keeps the process alive, on
        # screen. The crash handler writes a report whatever happens next, so look for one (2026-09-24).
        $crashes = @(Get-ChildItem -LiteralPath (Join-Path $testCfg 'logs') -Filter 'crash-*.txt' -ErrorAction SilentlyContinue)
        if ($crashes.Count -eq 0) { Pass "the cold start wrote no crash report" }
        else { Fail "the cold start wrote a crash report: $((Get-Content -LiteralPath $crashes[0].FullName -TotalCount 6) -join ' | ')" }
    }
    catch {
        Fail "cold-start/close smoke threw: $($_.Exception.Message)"
        if ($proc) { try { Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force } catch { } }
    }
    finally {
        Remove-Item -LiteralPath $testCfg -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---- 6. HEADLESS START: the real program with no window at all, driven only through --control (Ed, 2026-09-24:
#         "a way of driving remsound entirely without a window"). Every promise is checked on the real process:
#         nothing of it takes focus or appears on screen, it answers, it acts, it logs, and it closes. ----
Write-Host "`nHeadless start, driven through the control channel (isolated config):" -ForegroundColor Cyan
if ($script:coldStartSkipped) {
    Write-Host "  [SKIP] a RemSound instance is already running - close it to run this check" -ForegroundColor Yellow
}
else {
    $hlCfg = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-cfg-" + [guid]::NewGuid().ToString('N'))
    $hl = $null; $watch = $null
    try {
        Invoke-RsCli @('--log', 'on', '--config-dir', $hlCfg) | Out-Null
        # Watch the whole run from another process: which window has focus, and whether any window of this copy is on
        # screen, fifty times a second until it exits. The watcher is started and READY before the copy is: started
        # after it, it spent its first seconds compiling and missed the moment a window is most likely to flash up
        # (2026-09-24). It says it is ready in one file and is handed the copy's process id in another.
        $readyFile = Join-Path $env:TEMP ("rs-watch-ready-" + [guid]::NewGuid().ToString('N'))
        $pidFile = Join-Path $env:TEMP ("rs-watch-pid-" + [guid]::NewGuid().ToString('N'))
        $watch = Start-Job -ArgumentList $readyFile, $pidFile -ScriptBlock {
            param($readyFile, $pidFile)
            Add-Type -TypeDefinition ([string]::Join("`n", @(
                'using System; using System.Runtime.InteropServices; using System.Text;',
                'public static class HlWatch {',
                '    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();',
                '    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);',
                '    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);',
                '    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);',
                '    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);',
                '    public delegate bool EnumProc(IntPtr h, IntPtr l);',
                '    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);',
                '    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }',
                '    public static string Title(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }',
                '    public static string OnScreen(uint pid) {',
                '        var found = new StringBuilder();',
                '        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); RECT r;',
                '            if (p == pid && IsWindowVisible(h) && GetWindowRect(h, out r) && r.R > -10000 && r.B > -10000 && (r.R - r.L) > 2 && (r.B - r.T) > 2)',
                '                found.Append("[" + Title(h) + "] ");',
                '            return true; }, IntPtr.Zero);',
                '        return found.ToString();',
                '    }',
                '}')))
            Set-Content -LiteralPath $readyFile -Value 'ready'
            $wait = (Get-Date).AddSeconds(30)
            # Until the file holds a number: it can be read in the instant after it is made and before the number is in it,
            # and then this watched process 0 - and "never on screen" meant nothing (found 2026-09-26).
            $procId = 0
            while ($procId -eq 0 -and (Get-Date) -lt $wait) {
                if (Test-Path -LiteralPath $pidFile) { $procId = [int](Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1) }
                if ($procId -eq 0) { Start-Sleep -Milliseconds 2 }
            }
            $focus = @{}; $screen = @{}
            $end = (Get-Date).AddSeconds(60)
            while ((Get-Date) -lt $end -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)) {
                $fg = [HlWatch]::GetForegroundWindow(); $fp = [uint32]0; [void][HlWatch]::GetWindowThreadProcessId($fg, [ref]$fp)
                if ($fp -eq $procId) { $focus[[HlWatch]::Title($fg)] = 1 }
                $on = [HlWatch]::OnScreen([uint32]$procId); if ($on) { $screen[$on] = 1 }
                Start-Sleep -Milliseconds 20
            }
            [pscustomobject]@{ Focus = @($focus.Keys); OnScreen = @($screen.Keys); Watched = $procId }
        }
        $readyBy = (Get-Date).AddSeconds(30)
        while (-not (Test-Path -LiteralPath $readyFile) -and (Get-Date) -lt $readyBy) { Start-Sleep -Milliseconds 50 }
        if (-not (Test-Path -LiteralPath $readyFile)) { Fail "headless: the window watcher never became ready, so nothing below about windows can be trusted" }
        $hl = Start-Process -FilePath $exe -ArgumentList @('--headless', '--config-dir', $hlCfg) -PassThru
        Set-Content -LiteralPath $pidFile -Value $hl.Id
        Start-Sleep -Seconds 5
        $w = Invoke-RsCli @('--control', 'windows')
        if ($w.Code -eq 0 -and $w.Text -match 'pick a profile') { Pass "headless: started with no window, at the hidden profile picker, answering --control" }
        else { Fail "headless: --control windows did not find the hidden profile picker (exit $($w.Code)): $($w.Text.Trim())" }
        # Start-Process (Windows PowerShell) quotes nothing it is given: a command with spaces carries its own quotes.
        Invoke-RsCli @('--control', '"answer OK"') | Out-Null
        Start-Sleep -Seconds 4
        $l = Invoke-RsCli @('--control', 'list')
        if ($l.Code -eq 0 -and $l.Text -match 'tick box "Send my audio"' -and $l.Text -match 'tick list "Connected peers"') { Pass "headless: the main window is listed by the names a screen reader reads" }
        else { Fail "headless: list did not describe the main window (exit $($l.Code)): $($l.Text.Substring(0, [Math]::Min(300, $l.Text.Length)))" }
        $s = Invoke-RsCli @('--control', '"set Lock to these exact peer addresses on"', '--control', '"get Lock to these exact peer addresses"')
        if ($s.Code -eq 0 -and $s.Text -match 'tick box "Lock to these exact peer addresses[^"]*" ticked') { Pass "headless: a tick box set through --control is really ticked" }
        else { Fail "headless: set/get of a tick box failed (exit $($s.Code)): $($s.Text.Trim())" }
        # A profile switch closes the main window and opens a new one. The channel must follow it: until 2026-09-24 it
        # was tied to a helper window WinForms destroys at that moment, and refused every command after the first switch.
        $nw = Invoke-RsCli @('--control', '"menu File > New profile"')
        if ($nw.Text -match 'question') { Invoke-RsCli @('--control', '"answer No"') | Out-Null }
        $followed = $false
        for ($i = 0; $i -lt 20 -and -not $followed; $i++) {
            Start-Sleep -Milliseconds 500
            $g = Invoke-RsCli @('--control', '"get Lock to these exact peer addresses"')
            $followed = ($g.Code -eq 0 -and $g.Text -match 'not ticked')
        }
        if ($followed) { Pass "headless: after a profile switch the channel drives the new main window" }
        else { Fail "headless: after File, New profile the channel did not reach the new main window: $($g.Text.Trim())" }

        Invoke-RsCli @('--control', 'quit') | Out-Null
        $gone = $false
        for ($i = 0; $i -lt 40 -and -not $gone; $i++) { Start-Sleep -Milliseconds 250; $gone = -not (Get-Process -Id $hl.Id -ErrorAction SilentlyContinue) }
        if ($gone) { Pass "headless: --control quit closed it (no orphan process)" }
        else { Fail "headless: still running after --control quit"; try { Stop-Process -Id $hl.Id -Force } catch { } }
        $seen = Receive-Job -Job $watch -Wait
        if ($seen.Watched -ne $hl.Id) { Fail "headless: the window watcher watched process $($seen.Watched), not the headless copy ($($hl.Id))" }
        if ($seen.Focus.Count -eq 0) { Pass "headless: no window of it ever took focus" } else { Fail "headless: took focus: $($seen.Focus -join ' | ')" }
        if ($seen.OnScreen.Count -eq 0) { Pass "headless: no window of it was ever on screen" } else { Fail "headless: on screen: $($seen.OnScreen -join ' | ')" }
        $logText = (Get-ChildItem -LiteralPath (Join-Path $hlCfg 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
        if ($logText -match 'headless: running with no window' -and $logText -match 'remote control: answer OK -> ok' -and $logText -match 'remote control: set Lock to these exact peer addresses on -> ok') {
            Pass "headless: its start and every command, the picker's answer included, are in its log"
        }
        else { Fail "headless: the log is missing the headless start or a command" }
        $crashes = @(Get-ChildItem -LiteralPath (Join-Path $hlCfg 'logs') -Filter 'crash-*.txt' -ErrorAction SilentlyContinue)
        if ($crashes.Count -eq 0) { Pass "headless: no crash report" }
        else { Fail "headless: wrote a crash report: $((Get-Content -LiteralPath $crashes[0].FullName -TotalCount 6) -join ' | ')" }
    }
    catch {
        Fail "headless start threw: $($_.Exception.Message)"
        if ($hl) { try { Get-Process -Id $hl.Id -ErrorAction SilentlyContinue | Stop-Process -Force } catch { } }
    }
    finally {
        if ($watch) { Remove-Job -Job $watch -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $readyFile, $pidFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $hlCfg -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---- 7. RESTARTS, DIALOGS AND A SECOND COPY, on the real program with no window (2026-09-24). What the in-app steps
#         cannot reach: settings and a profile surviving a real quit and start, a second copy bowing out, a password
#         never reaching the log, and every menu dialog opening and closing. Isolated config, like 5 and 6. ----
Write-Host "`nRestarts, dialogs and a second copy, driven through the control channel (isolated config):" -ForegroundColor Cyan
if ($script:coldStartSkipped) {
    Write-Host "  [SKIP] a RemSound instance is already running - close it to run this check" -ForegroundColor Yellow
}
else {
    $rtCfg = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-cfg-" + [guid]::NewGuid().ToString('N'))
    $rtCfg2 = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-cfg-" + [guid]::NewGuid().ToString('N'))
    $rt = $null; $second = $null
    # One command each. Start-Process passes arguments as they are, so the command goes in quotes and any quotes inside it
    # are escaped the way Windows reads them (\"), or a control name in quotes splits the command in two.
    function Rc([string]$command) { Invoke-RsCli @('--control', ('"' + ($command -replace '"', '\"') + '"')) }
    function Start-Headless([string[]]$extra) {
        $p = Start-Process -FilePath $exe -ArgumentList (@('--headless', '--config-dir', $rtCfg) + $extra) -PassThru
        for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; if ((Rc 'windows').Code -eq 0) { break } }
        $p
    }
    function Stop-Headless($p) {
        Rc 'quit' | Out-Null
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 250
            if (-not (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) { return $true }
            # A question on the way out (unsaved changes) is answered No: nothing here should be saved by accident.
            if ($i -eq 8 -and (Rc 'windows').Text -match 'question') { Rc 'answer No' | Out-Null }
        }
        try { Stop-Process -Id $p.Id -Force } catch { }
        $false
    }
    try {
        Invoke-RsCli @('--log', 'on', '--config-dir', $rtCfg) | Out-Null

        # --- A. Change things and save them: a profile control, a profile, a machine-wide preference. ---
        $rt = Start-Headless @()
        Rc 'answer OK' | Out-Null   # the start-up profile picker
        for ($i = 0; $i -lt 20 -and (Rc 'list').Text -notmatch 'tick box "Send my audio"'; $i++) { Start-Sleep -Milliseconds 250 }
        $a1 = Rc 'set "Master volume for received audio" 37'
        $a2 = Rc 'set "Lock to these exact peer addresses" on'
        # Save as is the Windows save window, opening in the profiles folder (profiles\<this computer>\). The full path, so
        # nothing can land anywhere else.
        Rc 'menu File > Save as' | Out-Null
        for ($i = 0; $i -lt 20 -and (Rc 'windows').Text -notmatch 'Save profile as'; $i++) { Start-Sleep -Milliseconds 250 }
        $a3 = Rc "type $([System.IO.Path]::Combine($rtCfg, 'profiles', $env:COMPUTERNAME, 'Gate.json'))"
        $a4 = Rc 'answer Save'
        # --- D (here, where it happens). A new profile asks for its password at once. Set it in that real dialog: it must
        #     never come back through the channel, and never reach the log (checked at the end). ---
        $secret = 'gate-secret-' + (Get-Random -Minimum 1000 -Maximum 9999)
        for ($i = 0; $i -lt 20 -and (Rc 'windows').Text -notmatch 'Change profile password'; $i++) { Start-Sleep -Milliseconds 250 }
        $pw = Rc "set ""Password for profile Gate"" $secret"
        $listed = (Rc 'list').Text
        $a6 = Rc 'answer OK'
        if ($pw.Code -eq 0 -and $pw.Text -notmatch '^error') { Pass "password: a new profile's password set in the real dialog" } else { Fail "password: could not be set: $($pw.Text.Trim())" }
        if ($pw.Text -notmatch [regex]::Escape($secret) -and $listed -notmatch [regex]::Escape($secret)) { Pass "password: never read back through the channel" }
        else { Fail "password: the channel showed the password" }
        Rc 'menu Options > Preferences' | Out-Null
        $a5 = Rc 'set "Start minimised to tray" on'
        Rc 'answer Close' | Out-Null
        $setOk = @($a1, $a2, $a3, $a4, $a5, $a6) | Where-Object { $_.Code -ne 0 -or $_.Text -match '^error' }
        if ($setOk.Count -eq 0) { Pass "restart: the volume, the lock tick, a new profile called Gate and a preference were set through the real window and dialogs" }
        else { Fail "restart: setting things up failed: $(($setOk | ForEach-Object { $_.Text.Trim() }) -join ' | ')" }
        if (-not (Stop-Headless $rt)) { Fail "restart: the first copy did not quit" }

        # --- B. Start again on that profile: everything must have come back. ---
        $rt = Start-Headless @('--profile', 'Gate')
        for ($i = 0; $i -lt 20 -and (Rc 'list').Text -notmatch 'tick box "Send my audio"'; $i++) { Start-Sleep -Milliseconds 250 }
        $vol = Rc 'get "Master volume for received audio"'
        if ($vol.Text -match '\b37\b') { Pass "restart: the Gate profile came back with its volume (37)" } else { Fail "restart: the volume did not survive a real restart: $($vol.Text.Trim())" }
        $lock = Rc 'get "Lock to these exact peer addresses"'
        if ($lock.Text -match '" ticked') { Pass "restart: and with its lock tick" } else { Fail "restart: the lock tick did not survive a real restart: $($lock.Text.Trim())" }
        Rc 'menu Options > Preferences' | Out-Null
        $pref = Rc 'get "Start minimised to tray"'
        # An Alt key on the real program: Alt+L presses "Clear remembered applications list", which asks first.
        Rc 'key "Start minimised to tray" alt+l' | Out-Null
        $asked = (Rc 'windows').Text -match 'Clear the whole remembered applications list'
        Rc 'answer No' | Out-Null
        Rc 'answer Close' | Out-Null
        $closed = Rc 'wait gone Preferences 5'
        if ($pref.Text -match '" ticked') { Pass "restart: a preference set in the real dialog came back after a real restart" } else { Fail "restart: Start minimised to tray did not survive a restart: $($pref.Text.Trim())" }
        if ($asked) { Pass "keys: an Alt key pressed a button in the real Preferences" } else { Fail "keys: Alt+L in Preferences did not press Clear remembered applications list" }
        if ($closed.Text -match '^ok') { Pass "wait: saw Preferences close" } else { Fail "wait: did not see Preferences close: $($closed.Text.Trim())" }
        $settings = (Rc 'settings').Text
        if ($settings -match '"Password": "\(hidden\)"' -and $settings -notmatch [regex]::Escape($secret)) { Pass "settings: shown with the profile's password hidden" }
        else { Fail "settings: the password was not hidden, or the settings were not shown" }

        # --- C. A second copy while this one runs: it must bow out, and this one must carry on answering. ---
        $second = Start-Process -FilePath $exe -ArgumentList @('--headless', '--config-dir', $rtCfg2) -PassThru
        $bowed = $false
        for ($i = 0; $i -lt 40 -and -not $bowed; $i++) { Start-Sleep -Milliseconds 250; $bowed = -not (Get-Process -Id $second.Id -ErrorAction SilentlyContinue) }
        if ($bowed) { Pass "second copy: a second headless start bowed out" } else { Fail "second copy: a second headless copy is still running beside the first"; try { Stop-Process -Id $second.Id -Force } catch { } }
        if ((Rc 'windows').Code -eq 0) { Pass "second copy: the first copy still answers" } else { Fail "second copy: the first copy stopped answering after a second start" }


        # --- E. Every menu item that opens a dialog (they end in "...") opens one, and it closes again. Items that act on
        #        the computer or pick files are left alone. ---
        $before = (Rc 'windows').Text
        $tree = (Rc 'menus').Text -split "`r?`n"
        $path = @(); $opened = 0; $bad = @()
        for ($i = 0; $i -lt $tree.Count; $i++) {
            $line = $tree[$i]; if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $depth = [int](($line.Length - $line.TrimStart().Length) / 2)
            $text = ($line.Trim() -replace ' \((ticked|unavailable)\)', '')
            $path = @($path | Select-Object -First $depth) + $text
            $nextDepth = if ($i + 1 -lt $tree.Count) { [int](($tree[$i + 1].Length - $tree[$i + 1].TrimStart().Length) / 2) } else { 0 }
            $isLeaf = $nextDepth -le $depth
            if (-not $isLeaf -or $line -match '\(unavailable\)' -or $text -notmatch '(\.\.\.|…)$') { continue }
            $full = $path -join ' > '
            if ($full -match 'Service|DAW plugin|Install|Uninstall|[Uu]pdate|Open profile|[Ff]older|Delete|Remove|Import|Export') { continue }
            Rc "menu $full" | Out-Null
            Start-Sleep -Milliseconds 300
            $during = (Rc 'windows').Text
            if ($during -eq $before) { $bad += "$full opened nothing"; continue }
            $opened++
            foreach ($button in @('Cancel', 'Close', 'OK')) {
                if ((Rc 'windows').Text -eq $before) { break }
                Rc "answer $button" | Out-Null
                Start-Sleep -Milliseconds 200
            }
            if ((Rc 'windows').Text -ne $before) { $bad += "$full would not close"; break }
        }
        if ($opened -ge 5 -and $bad.Count -eq 0) { Pass "dialogs: all $opened menu dialogs opened on the real program and closed again" }
        else { Fail "dialogs: $opened opened; $($bad -join '; ')" }

        if (-not (Stop-Headless $rt)) { Fail "restart: the second start did not quit" }
        $rt = $null

        # --- F. What the log must and must not say. ---
        $rtLog = (Get-ChildItem -LiteralPath (Join-Path $rtCfg 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
        if ($rtLog -match 'updater: background checks are off in a --headless run' -and $rtLog -match 'updater: no startup check in a --headless run') {
            Pass "log: a headless copy says it made no update checks, at start-up or in the background"
        }
        else { Fail "log: a headless copy did not say it skipped its update checks" }
        if ($rtLog -notmatch [regex]::Escape($secret)) { Pass "log: the password is nowhere in it" } else { Fail "log: THE PASSWORD IS IN THE LOG" }
        $crashes = @(Get-ChildItem -LiteralPath (Join-Path $rtCfg 'logs') -Filter 'crash-*.txt' -ErrorAction SilentlyContinue)
        if ($crashes.Count -eq 0) { Pass "restarts and dialogs: no crash report" }
        else { Fail "restarts and dialogs: a crash report was written: $((Get-Content -LiteralPath $crashes[0].FullName -TotalCount 6) -join ' | ')" }
    }
    catch {
        Fail "restarts and dialogs threw: $($_.Exception.Message)"
    }
    finally {
        foreach ($p in @($rt, $second)) { if ($p) { try { Get-Process -Id $p.Id -ErrorAction SilentlyContinue | Stop-Process -Force } catch { } } }
        Remove-Item -LiteralPath $rtCfg, $rtCfg2 -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "`nThe real plugin pointer and this computer's real RemSound state:" -ForegroundColor Cyan
if ((Get-PointerState) -eq $pointerBefore) { Pass "the whole run, real starts included, left the plugin's real log pointer alone" }
else { Fail "the run changed $realPointer - every DAW's plugin reads it to find RemSound's log folder" }
$realStateAfter = Get-RealState
if ($realStateAfter -eq $realStateBefore) { Pass "the run left the real settings, profiles, service settings and sign-in entry exactly as they were" }
else {
    $changed = Compare-Object ($realStateBefore -split "`n") ($realStateAfter -split "`n") | ForEach-Object { ($_.InputObject -split '=')[0] } | Sort-Object -Unique
    Fail "the run changed this computer's real RemSound state: $($changed -join ', ')"
}

# ---- summary ----
Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
$skipNote = if ($script:skips.Count -gt 0) { " Not run on this machine: $($script:skips -join '; ')." } else { '' }
if ($script:failures.Count -eq 0 -and $script:coldStartSkipped) {
    # Still a pass, so test copies and tester zips carry on. Not a clearance to publish: build-release.ps1 refuses to
    # run with RemSound open for exactly this reason.
    Write-Host "RESULT: PASS - but the cold-start check did not run because RemSound is open, so this build has not been" -ForegroundColor Yellow
    Write-Host "        launched for real. Not cleared for release: close RemSound and run the gate again before publishing.$skipNote" -ForegroundColor Yellow
    exit 0
}
if ($script:failures.Count -eq 0 -and $script:skips.Count -gt 0) {
    # A pass, and it says what it did not check rather than calling itself complete.
    Write-Host "RESULT: PASS - every check that ran passed.$skipNote" -ForegroundColor Yellow
    exit 0
}
if ($script:failures.Count -eq 0) {
    Write-Host "RESULT: PASS - all gate checks passed. Safe to publish." -ForegroundColor Green
    exit 0
}
Write-Host "RESULT: FAIL - $($script:failures.Count) check(s) failed:" -ForegroundColor Red
$script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
