@echo off
setlocal
title Start the RemSound service
rem The same as Service, Start service in RemSound: RemSound itself does the work (RemSound.exe --service start).
set "REMSOUND=%~dp0..\RemSound.exe"
if not exist "%REMSOUND%" goto missing

echo.
echo  This starts the RemSound service, the same as Start service in RemSound's
echo  Service menu. Windows will ask for administrator permission.
echo.
"%REMSOUND%" --service start
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
