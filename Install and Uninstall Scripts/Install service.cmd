@echo off
setlocal
title Install the RemSound service
rem The same as Service, Install service in RemSound, question for question: RemSound itself does the work
rem (RemSound.exe --service install, then --service start if asked).
set "REMSOUND=%~dp0..\RemSound.exe"
if not exist "%REMSOUND%" goto missing

echo.
echo  Install the RemSound send-only service? It will start automatically at boot
echo  and stream your service profile whenever you're not using RemSound normally.
echo  This is the same as Install service in RemSound's Service menu.
echo  Windows will ask for administrator permission.
echo.
echo  Press 1 to install it, or 2 to cancel.
choice /c 12 /n >nul
if errorlevel 2 goto cancelled
echo.
"%REMSOUND%" --service install
if errorlevel 1 goto finish

echo.
echo  Do you want to start it now? It will also start automatically at every boot.
echo  Press 1 to start it now, or 2 to leave it until the next boot.
choice /c 12 /n >nul
if errorlevel 2 goto finish
echo.
"%REMSOUND%" --service start
goto finish

:cancelled
echo  Cancelled. Nothing was changed.

:finish
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
