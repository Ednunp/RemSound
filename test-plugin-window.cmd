@echo off
REM Opens the RemSound PLUGIN window on its own, with no DAW involved, so its controls can be
REM checked with NVDA. Double-click this file in the repository folder.
REM If RemSound itself is running, the window talks to it over the plugin link and lists its peers.
REM Escape or the Close button shuts it. No audio is carried.
REM
REM It uses the first RemSound.exe it finds: the Debug build, then the Release build, then publish\
REM (the hand-test copy that deploy-test.ps1 fills).
setlocal
set "EXE=%~dp0src\RemSound.App\bin\Debug\net10.0-windows\RemSound.exe"
if not exist "%EXE%" set "EXE=%~dp0src\RemSound.App\bin\Release\net10.0-windows\RemSound.exe"
if not exist "%EXE%" set "EXE=%~dp0publish\RemSound.exe"
if not exist "%EXE%" goto notfound
start "" "%EXE%" --plugin-window
exit /b 0

:notfound
echo RemSound.exe was not found. Build RemSound first, or run deploy-test.ps1 to fill the publish folder.
pause
exit /b 1
