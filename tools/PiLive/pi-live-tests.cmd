@echo off
rem Live tests against the RemSound test server (the relay on a test box - Ed's Pi). Double-click, then press 1, 2 or 3.
rem Writes a report to the "reports" folder next to this file. Needs pi-live.local.json beside it (the box's address,
rem user and SSH key; git ignores it). Never runs anything while someone is on the server.
cd /d "%~dp0"
if not exist "%~dp0pi-live.local.json" (
  echo There are no settings yet: pi-live.local.json is missing next to this file.
  pause
  exit /b
)
echo.
echo Live tests against the RemSound test server.
echo 1. Everything (about 4 minutes)
echo 2. RemSound's own checks through the server
echo 3. Network timing only (about 45 seconds)
choice /c 123 /n /m "Press 1, 2 or 3: "
set "MODE=all"
if errorlevel 2 set "MODE=server"
if errorlevel 3 set "MODE=timing"
py -3 "%~dp0pi_live.py" %MODE%
echo.
echo The report is in the reports folder next to this file. Press any key to close.
pause >nul
