@echo off
setlocal
title Uninstall the RemSound service
rem The same as Service, Uninstall service in RemSound, question for question: RemSound itself does the work
rem (RemSound.exe --service uninstall).
set "REMSOUND=%~dp0..\RemSound.exe"
if not exist "%REMSOUND%" goto missing

echo.
echo  Uninstall the RemSound service?
echo  This is the same as Uninstall service in RemSound's Service menu.
echo  Windows will ask for administrator permission.
echo.
echo  Press 1 to uninstall it, or 2 to cancel.
choice /c 12 /n >nul
if errorlevel 2 goto cancelled
echo.
"%REMSOUND%" --service uninstall
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
