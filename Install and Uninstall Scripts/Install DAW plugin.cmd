@echo off
setlocal
title Install the RemSound DAW plugin
rem The same as DAW plugin, Install plugin in RemSound, choice for choice: RemSound itself does the work
rem (RemSound.exe --install-plugin followed by the folder).
set "REMSOUND=%~dp0..\RemSound.exe"
if not exist "%REMSOUND%" goto missing

echo.
echo  Where should the RemSound DAW plugin go? This is the same as choosing
echo  Install plugin from RemSound's DAW plugin menu.
echo.
echo  1. The standard place: your own VST3 folder. Most music software looks there,
echo     and no administrator permission is needed.
echo  2. The shared VST3 folder for everyone on this computer, for music software
echo     that only looks there, Audacity for one. Windows will ask for
echo     administrator permission.
echo  3. Another folder. You type it in.
echo  4. Cancel.
echo.
echo  Press 1, 2, 3 or 4.
choice /c 1234 /n >nul
if errorlevel 4 goto cancelled
if errorlevel 3 goto other
if errorlevel 2 goto shared
echo.
"%REMSOUND%" --install-plugin "%LOCALAPPDATA%\Programs\Common\VST3"
goto finish

:shared
echo.
"%REMSOUND%" --install-plugin "%CommonProgramFiles%\VST3"
goto finish

:other
echo.
set "FOLDER="
set /p "FOLDER=Type the whole folder path, then press Enter: "
if not defined FOLDER goto cancelled
echo.
"%REMSOUND%" --install-plugin "%FOLDER%"
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
