# bootstrap-service-dev.ps1  --  ONE-TIME, run in an ADMINISTRATOR PowerShell.
#
# The RemSound service currently installed predates the self-contained/auto-update rework, and its
# program folder is still admin-only. This brings it up to the new build in one elevated step and,
# crucially, makes its folder writable by you AND records where the app lives -- so from now on:
#   * real releases auto-update the service (no admin, no clicks), and
#   * same-version dev refreshes can be dropped in by simply stop -> copy -> start, no admin.
#
# After you have run this once, you never need to run it again.

$ErrorActionPreference = 'Continue'
$svc     = 'RemSoundService'
$publish = 'D:\proj\RemSound\publish'
$bin     = 'C:\ProgramData\RemSound\service\bin'
$svcDir  = 'C:\ProgramData\RemSound\service'

Write-Host "Stopping $svc ..." -ForegroundColor Cyan
Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue

Write-Host "Copying the current build into the service folder ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $bin -Force | Out-Null
robocopy $publish $bin /E /XD 'user settings and logs' logs recordings profiles config /XF 'global config.json' 'remsound.config.json' /R:2 /W:1 | Out-Null
Write-Host ("  robocopy exit {0} (0-7 = ok)" -f $LASTEXITCODE)

Write-Host "Granting your account write access to the service folder (so future updates need no admin) ..." -ForegroundColor Cyan
# *S-1-5-32-545 = BUILTIN\Users (locale-independent); (OI)(CI)(M) = inherit + Modify; /T = existing contents too.
icacls $bin /grant "*S-1-5-32-545:(OI)(CI)(M)" /T /C | Out-Null
Write-Host ("  icacls exit {0} (0 = ok)" -f $LASTEXITCODE)

Write-Host "Recording the app location for auto-update ..." -ForegroundColor Cyan
Set-Content -LiteralPath (Join-Path $svcDir 'app-source.txt') -Value $publish -Encoding utf8

Write-Host "Starting $svc ..." -ForegroundColor Cyan
Start-Service -Name $svc -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Write-Host ("Done. Service status: {0}" -f (Get-Service -Name $svc).Status) -ForegroundColor Green
