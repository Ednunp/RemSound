@echo off
setlocal
title Uninstall the RemSound DAW plugin
rem The same as DAW plugin, Remove plugin in RemSound: RemSound itself does the work (RemSound.exe --uninstall-plugin).
set "REMSOUND=%~dp0..\RemSound.exe"
if not exist "%REMSOUND%" goto missing

echo.
echo  This removes the RemSound DAW plugin from the folder it was installed in, the
echo  same as choosing Remove plugin from RemSound's DAW plugin menu. Only the
echo  files RemSound put there are removed. If it is in the shared VST3 folder,
echo  Windows will ask for administrator permission. Close your music software first.
echo.
"%REMSOUND%" --uninstall-plugin
echo.
echo  Press any key to close this window.
pause >nul
exit /b 0

:missing
echo.
echo  RemSound.exe isn't in the folder above this one, so this script can't run it.
echo  Keep this folder inside the RemSound folder, next to RemSound.exe.
echo.
echo  Press any key to close this window.
pause >nul
exit /b 1
